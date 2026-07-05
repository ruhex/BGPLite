using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BGPLite.Providers;

/// <summary>
/// URL-keyed in-memory TTL cache for per-peer user-supplied prefix-list sources (issue #150, epic #143).
/// Mirrors <see cref="PrefixSourceService"/>'s cache shape: separate positive/negative TTL, per-key
/// serialization (no thundering herd on a cold/expired key), and stale-on-failure serving so a peer's
/// routes stay stable through transient fetch errors. Keyed by URL (not source name) so peers that
/// subscribe to the same list share a single fetch.
/// </summary>
/// <remarks>
/// <b>Layering / Active-state contract.</b> This cache knows nothing about a source's <c>Active</c>
/// flag or peer ownership — it is a pure URL → prefix-list memo. The Active/pause lifecycle is owned
/// by the caller: <c>PeerStore.LoadPeerRoutingView</c> filters <c>Where(c =&gt; c.Active)</c> before the
/// send path ever reaches this cache, so a <b>paused source contributes no prefixes regardless of what
/// is cached</b>. Because the cache is shared across peers (URL-keyed), pausing or deleting a source in
/// one peer does NOT evict the entry here — another peer with the same URL may still need it; orphaned
/// entries (no active subscriber) simply expire via TTL. The cache sits above the fetcher and is
/// transparent to the #144 SSRF defense, which lives in <see cref="HttpPrefixProvider"/>'s named client.
/// <see cref="OperationCanceledException"/> always propagates (#114) and is never cached as negative.
/// </remarks>
internal sealed class UserSourceCache
{
    private readonly TimeSpan _positiveTtl;
    private readonly TimeSpan _negativeTtl;
    private readonly ILogger? _logger;

    // url → (list, cached at, is negative). Negative entries (failed loads) use _negativeTtl.
    private readonly ConcurrentDictionary<string, (IReadOnlyList<(uint Prefix, byte Length)> List, DateTime CachedAt, bool Negative)> _cache = new();
    // url → gate serializing the cache-miss fetch path (prevents thundering herd on cold/expired keys).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public UserSourceCache(TimeSpan? positiveTtl = null, TimeSpan? negativeTtl = null, ILogger? logger = null)
    {
        _positiveTtl = positiveTtl ?? TimeSpan.FromHours(1);
        _negativeTtl = negativeTtl ?? TimeSpan.FromSeconds(30);
        _logger = logger;
    }

    /// <param name="url">Cache key (the source URL — dedupes across peers).</param>
    /// <param name="logLabel">Safe identifier (the source <c>Name</c>) for log lines — the URL itself is
    /// never logged, since peer URLs may carry query-string tokens (#149).</param>
    /// <param name="loadAsync">The fetcher ( HttpPrefixProvider.LoadAsync closed over the source config).</param>
    public async Task<IReadOnlyList<(uint Prefix, byte Length)>> GetOrLoadAsync(
        string url,
        string logLabel,
        Func<CancellationToken, Task<IReadOnlyList<(uint Prefix, byte Length)>>> loadAsync,
        CancellationToken ct)
    {
        if (TryGetFresh(url, out var fresh))
            return fresh;

        // Serialize per-key so concurrent callers (e.g. several peers refreshing the same URL) share
        // a single fetch — no thundering herd.
        var gate = _locks.GetOrAdd(url, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (TryGetFresh(url, out var rechecked))
                return rechecked;

            IReadOnlyList<(uint Prefix, byte Length)> prefixes;
            try
            {
                prefixes = await loadAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Serve the last good copy if we have one (regardless of its age).
                if (_cache.TryGetValue(url, out var stale) && !stale.Negative)
                {
                    _logger?.LogWarning(ex, "User-source '{Name}' load failed; serving cached copy ({Count} prefixes).",
                        logLabel, stale.List.Count);
                    return stale.List;
                }

                // Otherwise remember the failure briefly so repeated calls don't hammer the fetcher.
                _cache[url] = ([], DateTime.UtcNow, Negative: true);
                _logger?.LogWarning(ex, "User-source '{Name}' load failed (no cached copy); negative-cached for {Seconds}s.",
                    logLabel, (int)_negativeTtl.TotalSeconds);
                throw;
            }

            _cache[url] = (prefixes, DateTime.UtcNow, Negative: false);
            return prefixes;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryGetFresh(string url, out IReadOnlyList<(uint Prefix, byte Length)> list)
    {
        list = null!;
        if (!_cache.TryGetValue(url, out var entry)) return false;

        var ttl = entry.Negative ? _negativeTtl : _positiveTtl;
        if (DateTime.UtcNow - entry.CachedAt < ttl)
        {
            list = entry.List;
            return true;
        }

        return false;
    }
}
