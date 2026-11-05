using System.Collections.Concurrent;
using BGPLite.Configuration;
using BGPLite.Protocol;
using Microsoft.Extensions.Logging;

namespace BGPLite.Providers;

/// <summary>
/// The single per-ASN RIPEstat prefix cache. Every consumer of RIPEstat
/// per-ASN data goes through one instance of this class — the
/// <c>RipeStat.AsnLists</c>/custom-ASN path (<see cref="PrefixService"/>) and
/// <c>PrefixSources</c> entries of <c>Kind: asn</c> (<see cref="AsnPrefixProvider"/>).
/// Letting the two mechanisms fetch the same ASN independently with separate TTLs would
/// double RIPEstat traffic for an ASN configured in both and could serve disagreeing snapshots.
/// <para>
/// A negative entry represents a cached failure, not successful empty content. Production route
/// assembly and source providers pass <c>serveNegativeEntries: false</c> and receive an exception
/// during the negative TTL; the optional empty-list path is retained only for explicit callers.
/// </para>
/// <para>
/// Shape: per-ASN TTL cache with stale-on-failure, a short negative TTL for failed fetches,
/// per-ASN fetch gates against thundering herds, and a capacity cap with an eviction sweep.
/// </para>
/// </summary>
public sealed class RipeStatPrefixCache
{
    private readonly RipeStatProvider _ripe;
    private readonly ILogger<RipeStatPrefixCache>? _logger;
    private readonly TimeSpan _cacheTtl;
    private readonly TimeSpan _negativeTtl;
    // Upper bound on _cache/_locks entries. Without a cap, a malicious or churning peer
    // querying distinct ASNs grows the dictionaries without limit. Exceeded → least-recently-used
    // sweep before insert. The configured ASN universe is small (operator AsnLists), so a generous
    // default does not constrain real deployments.
    private readonly int _maxCacheEntries;
    private readonly TimeProvider _timeProvider;
    // asn → (prefix list, cached at, is negative). Negative entries (failed RIPEstat fetches) use
    // _negativeTtl. The tuple is shaped to mirror PrefixSourceService so the resilience semantics
    // (stale-on-failure, negative cache, bounded sweep) are identical across both caches.
    private readonly ConcurrentDictionary<uint, (IReadOnlyList<IpPrefix> Data, DateTime CachedAt, bool Negative)> _cache = new();
    // asn → gate serializing the cache-miss fetch path (prevents thundering herd on cold/expired
    // ASNs).
    private readonly ConcurrentDictionary<uint, SemaphoreSlim> _locks = new();
    // ASNs with callers currently inside the fetch path. The capacity sweep must
    // never evict an in-flight ASN's entry + gate: the fetcher holds that gate, and removing it
    // lets a later GetOrAdd create a second semaphore — two concurrent RIPEstat fetches for one
    // ASN, breaking the single-fetch-per-ASN invariant. The value is an ACTIVE-CALLER COUNT:
    // a plain presence marker would be removed by the first caller's exit
    // while a second caller is still inside/queued on the same gate.
    private readonly ConcurrentDictionary<uint, int> _inflight = new();
    // Amortized expired-entry sweep state (see GetPrefixesAsync). Injectable for tests.
    private int _callsSinceSweep;
    private readonly int _sweepEvery;
    // Compound gate lifecycle operations: registration/last-leaver accounting and eviction must
    // not split the in-flight check from gate creation/removal. Never held across network awaits.
    private readonly object _gateSync = new();
    internal Action<uint>? EvictionProbe { get; set; }

    public RipeStatPrefixCache(
        RipeStatProvider ripe,
        ILogger<RipeStatPrefixCache>? logger = null,
        TimeSpan? cacheTtl = null,
        TimeSpan? negativeTtl = null,
        int? maxCacheEntries = null,
        TimeProvider? timeProvider = null,
        int? sweepEvery = null)
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
        _sweepEvery = sweepEvery ?? 64;
    }

    /// <summary>Entries currently tracked (test/observability of sweep coverage).</summary>
    internal int TrackedCount => _cache.Count;

    /// <summary>Returns fresh, stale, or shared in-flight ASN data according to the cache policy, preserving negative failures unless explicitly allowed.</summary>
    public async Task<IReadOnlyList<IpPrefix>> GetPrefixesAsync(uint asn, CancellationToken ct = default, bool serveNegativeEntries = true)
    {
        // Amortized expired-entry sweep — expired entries would otherwise stay pinned until the entry
        // cap was hit. Cheap (every Nth call), idempotent, in-flight ASNs are never touched.
        if (Interlocked.Increment(ref _callsSinceSweep) >= _sweepEvery)
        {
            Volatile.Write(ref _callsSinceSweep, 0);
            SweepExpired();
        }

        // A cached negative result stays a failure unless the caller explicitly opted into []
        // so route assembly cannot mistake an outage for a successfully empty ASN.
        if (TryGetFresh(asn, out var fresh, out var freshIsNegative))
        {
            if (!freshIsNegative || serveNegativeEntries)
                return fresh;
            throw new InvalidOperationException($"AS{asn}: RIPEstat fetch is in negative-cache backoff.");
        }

        // Gate creation and caller registration are one atomic lifecycle operation with eviction.
        SemaphoreSlim gate;
        lock (_gateSync)
        {
            _inflight.AddOrUpdate(asn, 1, (_, count) => count + 1);
            gate = _locks.GetOrAdd(asn, _ => new SemaphoreSlim(1, 1));
        }
        try
        {
            return await FetchThroughGateAsync(asn, ct, serveNegativeEntries, gate);
        }
        finally
        {
            lock (_gateSync)
            {
                _inflight.AddOrUpdate(asn, 0, (_, count) => count - 1);
                _inflight.TryRemove(new KeyValuePair<uint, int>(asn, 0));
            }
        }
    }

    /// <summary>The gated fetch path of <see cref="GetPrefixesAsync"/> — runs under the caller's
    /// in-flight mark, sharing a single RIPEstat fetch per ASN.</summary>
    private async Task<IReadOnlyList<IpPrefix>> FetchThroughGateAsync(
        uint asn, CancellationToken ct, bool serveNegativeEntries, SemaphoreSlim gate)
    {
        await gate.WaitAsync(ct);
        try
        {
            // Re-check after acquiring the lock: another caller may have just populated the entry
            // (with the same negative-entry discriminator as the outer fast path —
            // forgetting it here re-introduces exactly the Ok([]) hole the provider opts out of).
            if (TryGetFresh(asn, out var rechecked, out var recheckIsNegative))
            {
                if (!recheckIsNegative || serveNegativeEntries)
                    return rechecked;
                throw new InvalidOperationException($"AS{asn}: RIPEstat fetch is in negative-cache backoff.");
            }

            IReadOnlyList<IpPrefix> prefixes;
            try
            {
                prefixes = await _ripe.GetPrefixesAsync(asn, ct);
            }
            // Only CALLER cancellation propagates. A foreign-token OCE
            // (the per-attempt/body deadline inside RipeStatProvider, fired on a live ct) is a
            // load FAILURE and takes the stale-on-failure / negative-cache branches below — an
            // unfiltered rethrow would make every retry re-pay the full fetch budget with no
            // backoff ever recorded.
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Stale-on-failure: serve the last good copy regardless of its age so a
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
    /// negative within _negativeTtl). The negative flag comes out of the SAME read —
    /// a separate IsNegative lookup could miss an eviction racing between the two and serve a
    /// captured [] to a caller that opted out of negatives.</summary>
    private bool TryGetFresh(uint asn, out IReadOnlyList<IpPrefix> data, out bool isNegative)
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

    /// <summary>
    /// Removes EXPIRED entries regardless of the entry cap — otherwise they stay pinned until
    /// the cap is hit, so steady-state memory is "every ASN fetched recently", not "live ASNs".
    /// In-flight ASNs are never touched; concurrent sweeps are idempotent.
    /// </summary>
    internal void SweepExpired()
    {
        lock (_gateSync)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            foreach (var (key, entry) in _cache)
            {
                var ttl = entry.Negative ? _negativeTtl : _cacheTtl;
                if (now - entry.CachedAt < ttl) continue;
                if (_inflight.TryGetValue(key, out var active) && active > 0) continue;
                EvictionProbe?.Invoke(key);
                if (_cache.TryRemove(key, out _))
                    _locks.TryRemove(key, out _);
            }
        }
    }

    /// <summary>Enforces the _maxCacheEntries bound. Called before inserting a NEW key,
    /// under the caller's per-ASN gate. Removes a few least-recently-cached entries (by CachedAt)
    /// plus any expired entries it encounters, and drops the corresponding _locks entries — never
    /// for an in-flight ASN. The caller's per-ASN gate is held, and _gateSync makes each
    /// in-flight check plus cache/gate removal atomic with registration.</summary>
    private void EvictIfAtCapacity(uint insertingKey)
    {
        lock (_gateSync)
        {
            if (_cache.Count < _maxCacheEntries) return;
            if (_cache.ContainsKey(insertingKey)) return; // already present, no insert coming

            // Snapshot, drop expired entries first (cheapest eviction), then by oldest CachedAt.
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var toEvict = new HashSet<uint>();
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
                // Never remove an active ASN's cache/gate; the gateSync lock makes the check
                // and both removals atomic with gate registration and last-leaver accounting.
                if (_inflight.TryGetValue(key, out var active) && active > 0) continue;
                EvictionProbe?.Invoke(key);
                if (_cache.TryRemove(key, out _))
                    _locks.TryRemove(key, out _);
            }
        }
    }
}
