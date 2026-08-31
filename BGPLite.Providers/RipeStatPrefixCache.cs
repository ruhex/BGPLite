using System.Collections.Concurrent;
using BGPLite.Configuration;
using Microsoft.Extensions.Logging;

namespace BGPLite.Providers;

/// <summary>
/// The single per-ASN RIPEstat prefix cache (#267 item 5). Every consumer of RIPEstat
/// per-ASN data goes through one instance of this class — the legacy
/// <c>RipeStat.AsnLists</c>/custom-ASN path (<see cref="PrefixService"/>) and
/// <c>PrefixSources</c> entries of <c>Kind: asn</c> (<see cref="AsnPrefixProvider"/>).
/// Before it existed the two mechanisms fetched and cached the same ASN independently with
/// separate TTLs — an ASN configured in both doubled RIPEstat traffic and could serve
/// disagreeing snapshots.
/// <para>
/// <para>
/// #377 review: the negative-entry fast path serves [] to callers that accept the
/// "nothing this cycle" semantic (the RouteAssembler fan-out). A SOURCE provider must pass
/// <c>serveNegativeEntries: false</c> — wrapping a cached failure into SourceLoadResult.Ok([])
/// would store a positive empty list in the name-level cache and mark the source changed,
/// withdrawing a previously-good advertisement over a transient RIPEstat blip.
/// </para>
/// Shape (moved verbatim from <see cref="PrefixService"/>): per-ASN TTL cache with
/// stale-on-failure (#163), short negative TTL for failed fetches, per-ASN fetch gates against
/// thundering herds (#164), and a capacity cap with the #165 eviction sweep.
/// </para>
/// </summary>
public sealed class RipeStatPrefixCache
{
    private readonly RipeStatProvider _ripe;
    private readonly ILogger<RipeStatPrefixCache>? _logger;
    private readonly TimeSpan _cacheTtl;
    private readonly TimeSpan _negativeTtl;
    // Upper bound on _cache/_locks entries (#165). Without a cap, a malicious or churning peer
    // querying distinct ASNs grows the dictionaries without limit. Exceeded → least-recently-used
    // sweep before insert. The configured ASN universe is small (operator AsnLists), so a generous
    // default does not constrain real deployments.
    private readonly int _maxCacheEntries;
    private readonly TimeProvider _timeProvider;
    // asn → (prefix list, cached at, is negative). Negative entries (failed RIPEstat fetches) use
    // _negativeTtl. The tuple is shaped to mirror PrefixSourceService so the resilience semantics
    // (stale-on-failure, negative cache, bounded sweep) are identical across both caches.
    private readonly ConcurrentDictionary<uint, (IReadOnlyList<(uint Prefix, byte Length)> Data, DateTime CachedAt, bool Negative)> _cache = new();
    // asn → gate serializing the cache-miss fetch path (prevents thundering herd on cold/expired
    // ASNs — #164).
    private readonly ConcurrentDictionary<uint, SemaphoreSlim> _locks = new();

    public RipeStatPrefixCache(
        RipeStatProvider ripe,
        ILogger<RipeStatPrefixCache>? logger = null,
        TimeSpan? cacheTtl = null,
        TimeSpan? negativeTtl = null,
        int? maxCacheEntries = null,
        TimeProvider? timeProvider = null)
    {
        _ripe = ripe;
        _logger = logger;
        _cacheTtl = cacheTtl ?? TimeSpan.FromHours(1);
        _negativeTtl = negativeTtl ?? TimeSpan.FromSeconds(30);
        // 2× a generous operator-configured ASN universe (a route server typically tracks tens to
        // low-hundreds of origin ASNs). The cap defends against unbounded growth from adversarial /
        // churn traffic, not normal operation.
        _maxCacheEntries = maxCacheEntries ?? 4096;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<(uint Prefix, byte Length)>> GetPrefixesAsync(uint asn, CancellationToken ct = default, bool serveNegativeEntries = true)
    {
        // Fast path: fresh entry (positive, or negative within its TTL when the caller accepts
        // the []-on-recent-failure semantic — the RouteAssembler fan-out does; a source provider
        // must NOT, see the class doc).
        if (TryGetFresh(asn, out var fresh, out var freshIsNegative) && (serveNegativeEntries || !freshIsNegative))
            return fresh;

        // Serialize per-ASN so concurrent callers share a single RIPEstat fetch — no thundering herd
        // on a cold or just-expired ASN (#164).
        var gate = _locks.GetOrAdd(asn, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-check after acquiring the lock: another caller may have just populated the entry
            // (with the same negative-entry discriminator as the outer fast path — #377 review:
            // forgetting it here re-introduces exactly the Ok([]) hole the provider opts out of).
            if (TryGetFresh(asn, out var rechecked, out var recheckIsNegative) && (serveNegativeEntries || !recheckIsNegative))
                return rechecked;

            IReadOnlyList<(uint Prefix, byte Length)> prefixes;
            try
            {
                prefixes = await _ripe.GetPrefixesAsync(asn, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Stale-on-failure (#163): serve the last good copy regardless of its age so a
                // transient RIPEstat outage (429/5xx/network) does not drop the ASN's routes the
                // instant its TTL elapses.
                if (_cache.TryGetValue(asn, out var stale) && !stale.Negative)
                {
                    _logger?.LogWarning(ex,
                        "AS{Asn}: RIPEstat fetch failed; serving cached copy ({Count} prefixes).", asn, stale.Data.Count);
                    return stale.Data;
                }

                // No cached copy: remember the failure briefly so repeated calls don't hammer RIPEstat.
                EvictIfAtCapacity(asn);
                _cache[asn] = ([], _timeProvider.GetUtcNow().UtcDateTime, Negative: true);
                throw;
            }

            EvictIfAtCapacity(asn);
            _cache[asn] = (prefixes, _timeProvider.GetUtcNow().UtcDateTime, Negative: false);
            return prefixes;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>True if <paramref name="asn"/> has a non-expired entry (positive within _cacheTtl,
    /// negative within _negativeTtl). #377 review: the negative flag comes out of the SAME read —
    /// a separate IsNegative lookup could miss an eviction racing between the two and serve a
    /// captured [] to a caller that opted out of negatives.</summary>
    private bool TryGetFresh(uint asn, out IReadOnlyList<(uint Prefix, byte Length)> data, out bool isNegative)
    {
        data = null!;
        isNegative = false;
        if (!_cache.TryGetValue(asn, out var entry)) return false;

        var ttl = entry.Negative ? _negativeTtl : _cacheTtl;
        if (_timeProvider.GetUtcNow().UtcDateTime - entry.CachedAt < ttl)
        {
            data = entry.Data;
            isNegative = entry.Negative;
            return true;
        }
        return false;
    }

    /// <summary>Enforces the _maxCacheEntries bound (#165). Called before inserting a NEW key,
    /// under the caller's per-ASN gate. Removes a few least-recently-cached entries (by CachedAt)
    /// plus any expired entries it encounters, and drops the corresponding _locks entries. Under
    /// the lock of the caller's per-ASN gate, so the sweep is serialized against itself;
    /// ConcurrentDictionary enumeration is a snapshot and safe against concurrent writers.</summary>
    private void EvictIfAtCapacity(uint insertingKey)
    {
        if (_cache.Count < _maxCacheEntries) return;
        if (_cache.ContainsKey(insertingKey)) return; // already present, no insert coming

        // Snapshot, drop expired entries first (cheapest eviction), then by oldest CachedAt.
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var toEvict = new List<uint>();
        foreach (var (key, entry) in _cache)
        {
            var ttl = entry.Negative ? _negativeTtl : _cacheTtl;
            if (now - entry.CachedAt >= ttl)
                toEvict.Add(key);
        }
        // If still at/over capacity after dropping expired, evict the oldest until below the cap.
        if (_cache.Count - toEvict.Count >= _maxCacheEntries)
        {
            var oldest = _cache
                .Where(kvp => !toEvict.Contains(kvp.Key))
                .OrderBy(kvp => kvp.Value.CachedAt)
                .Select(kvp => kvp.Key);
            foreach (var key in oldest)
            {
                toEvict.Add(key);
                if (_cache.Count - toEvict.Count < _maxCacheEntries) break;
            }
        }

        foreach (var key in toEvict)
        {
            // TryUpdate/TryRemove under no per-key lock: a concurrent fetch for this key may be
            // racing, but that's fine — it will re-populate the entry; losing a transient cache hit
            // is acceptable, and never losing correctness.
            if (_cache.TryRemove(key, out _))
                _locks.TryRemove(key, out _);
        }
    }
}
