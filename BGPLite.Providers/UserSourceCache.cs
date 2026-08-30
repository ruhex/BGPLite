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
    private readonly TimeProvider _timeProvider;
    // Upper bound on _cache/_locks entries (#261, port of PrefixService's #165 pattern). Without a
    // cap, every unique peer-supplied URL leaves a permanent entry — up to ~10 MB of parsed prefixes
    // each (the HTTP body cap) plus a SemaphoreSlim — so operator churn or an API client loop grows
    // the cache without limit. Exceeded → sweep expired entries first, then the oldest by CachedAt.
    private readonly int _maxEntries;

    // url → (list, cached at, is negative). Negative entries (failed loads) use _negativeTtl.
    private readonly ConcurrentDictionary<string, (IReadOnlyList<(uint Prefix, byte Length)> List, DateTime CachedAt, bool Negative)> _cache = new();
    // url → gate serializing the cache-miss fetch path (prevents thundering herd on cold/expired keys).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public UserSourceCache(TimeSpan? positiveTtl = null, TimeSpan? negativeTtl = null, ILogger? logger = null, TimeProvider? timeProvider = null, int? maxCacheEntries = null)
    {
        _positiveTtl = positiveTtl ?? TimeSpan.FromHours(1);
        _negativeTtl = negativeTtl ?? TimeSpan.FromSeconds(30);
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        // Generous vs. real deployments (peers × a few active sources each); the cap defends
        // against unbounded growth from churn/adversarial URL variety, not normal operation.
        _maxEntries = maxCacheEntries ?? 1024;
    }

    /// <summary>Entries currently tracked (test/observability).</summary>
    internal int TrackedCount => _cache.Count;

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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // #114: CALLER-initiated cancellation always propagates and is never cached — it
                // is teardown, not a property of the source. A foreign-token OCE (e.g. the #320
                // per-fetch budget's linked CTS firing on a live caller token) falls through to
                // the failure handling below instead: stale-on-failure serve if possible, else a
                // brief negative-cache so repeated refreshes do not re-pay the full budget.
                throw;
            }
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
                EvictIfAtCapacity(url);
                _cache[url] = ([], _timeProvider.GetUtcNow().UtcDateTime, Negative: true);
                _logger?.LogWarning(ex, "User-source '{Name}' load failed (no cached copy); negative-cached for {Seconds}s.",
                    logLabel, (int)_negativeTtl.TotalSeconds);
                throw;
            }

            EvictIfAtCapacity(url);
            _cache[url] = (prefixes, _timeProvider.GetUtcNow().UtcDateTime, Negative: false);
            return prefixes;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Enforces the _maxEntries bound (#261). Called before inserting a NEW key, under the caller's
    /// per-URL gate. Drops expired entries first (cheapest eviction), then the oldest by CachedAt
    /// until below the cap, removing the matching _locks entries with them. Same tradeoff as
    /// PrefixService's #165 sweep: a concurrent loader may still hold an evicted URL's semaphore — it
    /// finishes on its own reference and a fresh caller GetOrAdd's a new gate, so the worst case is
    /// one duplicate idempotent GET, never a correctness loss.
    /// </summary>
    private void EvictIfAtCapacity(string insertingUrl)
    {
        if (_cache.Count < _maxEntries) return;
        if (_cache.ContainsKey(insertingUrl)) return; // already present, no insert coming

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var toEvict = new List<string>();
        foreach (var (key, entry) in _cache)
        {
            var ttl = entry.Negative ? _negativeTtl : _positiveTtl;
            if (now - entry.CachedAt >= ttl)
                toEvict.Add(key);
        }
        // If still at/over capacity after dropping expired, evict the oldest until below the cap.
        if (_cache.Count - toEvict.Count >= _maxEntries)
        {
            var oldest = _cache
                .Where(kvp => !toEvict.Contains(kvp.Key))
                .OrderBy(kvp => kvp.Value.CachedAt)
                .Select(kvp => kvp.Key);
            foreach (var key in oldest)
            {
                toEvict.Add(key);
                if (_cache.Count - toEvict.Count < _maxEntries) break;
            }
        }

        foreach (var key in toEvict)
        {
            if (_cache.TryRemove(key, out _))
                _locks.TryRemove(key, out _);
        }
    }

    private bool TryGetFresh(string url, out IReadOnlyList<(uint Prefix, byte Length)> list)
    {
        list = null!;
        if (!_cache.TryGetValue(url, out var entry)) return false;

        var ttl = entry.Negative ? _negativeTtl : _positiveTtl;
        if (_timeProvider.GetUtcNow().UtcDateTime - entry.CachedAt < ttl)
        {
            list = entry.List;
            return true;
        }

        return false;
    }
}
