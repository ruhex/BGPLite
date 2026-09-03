using System.Collections.Concurrent;
using BGPLite.Configuration;
using BGPLite.Protocol;
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
    private readonly ConcurrentDictionary<uint, (IReadOnlyList<IpPrefix> Data, DateTime CachedAt, bool Negative)> _cache = new();
    // asn → gate serializing the cache-miss fetch path (prevents thundering herd on cold/expired
    // ASNs — #164).
    private readonly ConcurrentDictionary<uint, SemaphoreSlim> _locks = new();
    // #267 item 3: ASNs with callers currently inside the fetch path. The capacity sweep must
    // never evict an in-flight ASN's entry + gate: the fetcher holds that gate, and removing it
    // let a later GetOrAdd create a second semaphore — two concurrent RIPEstat fetches for one
    // ASN, breaking the #164 invariant. The value is an ACTIVE-CALLER COUNT (CodeRabbit on the
    // integration review): a plain presence marker would be removed by the first caller's exit
    // while a second caller is still inside/queued on the same gate.
    private readonly ConcurrentDictionary<uint, int> _inflight = new();
    // #426: amortized expired-entry sweep state (see GetPrefixesAsync). injectable for tests.
    private int _callsSinceSweep;
    private readonly int _sweepEvery;

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

    /// <summary>Entries currently tracked (test/observability — #426 sweep coverage).</summary>
    internal int TrackedCount => _cache.Count;

    public async Task<IReadOnlyList<IpPrefix>> GetPrefixesAsync(uint asn, CancellationToken ct = default, bool serveNegativeEntries = true)
    {
        // #426: amortized expired-entry sweep — expired entries used to be pinned until the entry
        // cap was hit. Cheap (every Nth call), idempotent, in-flight ASNs are never touched.
        if (Interlocked.Increment(ref _callsSinceSweep) >= _sweepEvery)
        {
            Volatile.Write(ref _callsSinceSweep, 0);
            SweepExpired();
        }

        // Fast path: fresh entry (positive, or negative within its TTL when the caller accepts
        // the []-on-recent-failure semantic — the RouteAssembler fan-out does; a source provider
        // must NOT, see the class doc).
        if (TryGetFresh(asn, out var fresh, out var freshIsNegative) && (serveNegativeEntries || !freshIsNegative))
            return fresh;

        // #267 item 3: register in-flight BEFORE the gate is taken (unregistered only after this
        // caller's own turn is over) so the capacity sweep can never drop the gate this caller is
        // about to wait on or holds — see the _inflight note. AddOrUpdate is atomic; the exit
        // decrements and removes the key only while it still reads 0 (a concurrent re-enter wins
        // the race: its increment lands between, and the conditional remove sees 1 and no-ops).
        _inflight.AddOrUpdate(asn, 1, (_, count) => count + 1);
        try
        {
            return await FetchThroughGateAsync(asn, ct, serveNegativeEntries);
        }
        finally
        {
            _inflight.AddOrUpdate(asn, 0, (_, count) => count - 1);
            _inflight.TryRemove(new KeyValuePair<uint, int>(asn, 0));
        }
    }

    /// <summary>The gated fetch path of <see cref="GetPrefixesAsync"/> — runs under the caller's
    /// in-flight mark, sharing a single RIPEstat fetch per ASN (#164).</summary>
    private async Task<IReadOnlyList<IpPrefix>> FetchThroughGateAsync(uint asn, CancellationToken ct, bool serveNegativeEntries)
    {
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

            IReadOnlyList<IpPrefix> prefixes;
            try
            {
                prefixes = await _ripe.GetPrefixesAsync(asn, ct);
            }
            // #485 (#320/#324 contract): only CALLER cancellation propagates. A foreign-token OCE
            // (the per-attempt/body deadline inside RipeStatProvider, fired on a live ct) is a
            // load FAILURE and takes the stale-on-failure / negative-cache branches below — the
            // unfiltered rethrow meant every retry re-paid the full fetch budget with no backoff
            // ever recorded.
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
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

    /// <summary>Enforces the _maxCacheEntries bound (#165). Called before inserting a NEW key,
    /// under the caller's per-ASN gate. Removes a few least-recently-cached entries (by CachedAt)
    /// plus any expired entries it encounters, and drops the corresponding _locks entries — never
    /// for an in-flight ASN (#267 item 3). Under the lock of the caller's per-ASN gate, so the
    /// sweep is serialized against itself; ConcurrentDictionary enumeration is a snapshot and safe
    /// against concurrent writers.</summary>
    /// <summary>
    /// #426: removes EXPIRED entries regardless of the entry cap — they used to be pinned until
    /// the cap was hit, so steady-state memory was "every ASN fetched recently", not "live ASNs".
    /// In-flight ASNs are never touched (#267 item 3); concurrent sweeps are idempotent.
    /// </summary>
    private void SweepExpired()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var (key, entry) in _cache)
        {
            var ttl = entry.Negative ? _negativeTtl : _cacheTtl;
            if (now - entry.CachedAt < ttl) continue;
            if (_inflight.TryGetValue(key, out var active) && active > 0) continue;
            if (_cache.TryRemove(key, out _))
                _locks.TryRemove(key, out _);
        }
    }

    private void EvictIfAtCapacity(uint insertingKey)
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
            // #267 item 3: an in-flight ASN's entry is re-populated by its fetch — evicting both
            // while ANY of its callers holds/waits on the gate made a later GetOrAdd mint a second
            // semaphore and issue a duplicate concurrent fetch (#164). Skipping keeps the gate
            // intact; losing a transient eviction slot is acceptable, never losing correctness.
            if (_inflight.TryGetValue(key, out var active) && active > 0) continue;
            if (_cache.TryRemove(key, out _))
                _locks.TryRemove(key, out _);
        }
    }
}
