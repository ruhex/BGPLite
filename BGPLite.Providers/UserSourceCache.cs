using System.Collections.Concurrent;
using BGPLite.Protocol;
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
    // #426: entry COUNT caps say nothing about memory — one entry can hold ~1M prefixes (a 10 MB
    // response parsed). The budget bounds the TOTAL parsed prefixes across all entries; over
    // budget → the sweep drops the OLDEST positive entries until under. 2M prefixes ≈ 50 MB worst
    // case — generous for real peer sources, hostile to unbounded growth.
    internal const long DefaultMaxTotalPrefixes = 2_000_000;
    private readonly long _maxTotalPrefixes;
    // #426: expired entries used to be PINNED until the entry cap was hit — steady-state memory
    // was "everything fetched recently", not "live entries". The amortized sweep (every
    // _sweepEvery calls) now removes expired entries and enforces the prefix budget regardless
    // of the cap. internal-settable via ctor for tests.
    private int _callsSinceSweep;
    private readonly int _sweepEvery;
    internal int SweepEvery => _sweepEvery;

    /// <summary>Total parsed prefixes currently held in positive entries (test/observability).</summary>
    internal long TrackedPrefixes => _cache.Where(kvp => !kvp.Value.Negative).Sum(kvp => (long)kvp.Value.List.Count);

    // url → (list, cached at, is negative). Negative entries (failed loads) use _negativeTtl.
    private readonly ConcurrentDictionary<string, (IReadOnlyList<IpPrefix> List, DateTime CachedAt, bool Negative)> _cache = new();
    // url → gate serializing the cache-miss fetch path (prevents thundering herd on cold/expired keys).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    // url → ACTIVE-loader count (#450 review): the sweeps never remove a gate whose loaders are
    // still inside or queued — otherwise two gates coexist and two loaders race one URL, with the
    // older response able to overwrite the newer (RipeStatPrefixCache's #267-item-3 invariant).
    private readonly ConcurrentDictionary<string, int> _inflight = new();
    // #478: serializes the COMPOUND bookkeeping over _inflight and _locks — registration,
    // last-leaver gate removal, and the sweeps' inflight-check-then-remove. Without it the
    // decrement→zero-check→gate-removal sequence in ExitInflight could interleave with a fresh
    // registration, pair-removing a gate its loader still held (duplicate concurrent fetch — the
    // #468 consequence in a nanosecond window). Never held across an await: the gate Wait/Release
    // stay outside.
    private readonly object _gateSync = new();

    public UserSourceCache(TimeSpan? positiveTtl = null, TimeSpan? negativeTtl = null, ILogger? logger = null, TimeProvider? timeProvider = null, int? maxCacheEntries = null, long? maxTotalPrefixes = null, int? sweepEvery = null)
    {
        _positiveTtl = positiveTtl ?? TimeSpan.FromHours(1);
        _negativeTtl = negativeTtl ?? TimeSpan.FromSeconds(30);
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        // Generous vs. real deployments (peers × a few active sources each); the cap defends
        // against unbounded growth from churn/adversarial URL variety, not normal operation.
        _maxEntries = maxCacheEntries ?? 1024;
        _maxTotalPrefixes = maxTotalPrefixes ?? DefaultMaxTotalPrefixes;
        _sweepEvery = sweepEvery ?? 64;
    }

    /// <summary>Entries currently tracked (test/observability).</summary>
    internal int TrackedCount => _cache.Count;

    /// <summary>Gates currently tracked (test/observability — orphan-lock hygiene, #358 review).</summary>
    internal int TrackedGateCount => _locks.Count;

    /// <param name="url">Cache key (the source URL — dedupes across peers).</param>
    /// <param name="logLabel">Safe identifier (the source <c>Name</c>) for log lines — the URL itself is
    /// never logged, since peer URLs may carry query-string tokens (#149).</param>
    /// <param name="loadAsync">The fetcher ( HttpPrefixProvider.LoadAsync closed over the source config).</param>
    public async Task<IReadOnlyList<IpPrefix>> GetOrLoadAsync(
        string url,
        string logLabel,
        Func<CancellationToken, Task<IReadOnlyList<IpPrefix>>> loadAsync,
        CancellationToken ct)
    {
        // #426: amortized expired-entry + prefix-budget sweep — expired entries used to be pinned
        // until the entry cap was hit. Cheap (every Nth call), idempotent under concurrency.
        SweepIfNeeded();

        if (TryGetFresh(url, out var fresh))
            return fresh;

        // Serialize per-key so concurrent callers (e.g. several peers refreshing the same URL) share
        // a single fetch — no thundering herd.
        // #468: register as an active loader BEFORE taking the gate from _locks. The inverse
        // ordering let the LAST leaver's gate removal race a caller that had already taken the
        // (now-removed) gate instance but not yet registered, parking it on an orphaned
        // semaphore while the next caller minted a second gate. With this order every
        // participant is counted in _inflight before it can touch _locks, so ExitInflight only
        // ever removes a gate that nobody holds or queues on. #478: register+take run as one
        // atomic section under _gateSync — the counter bump and the gate lookup must not be
        // split by a concurrent last-leaver removal.
        SemaphoreSlim gate;
        lock (_gateSync)
        {
            _inflight.AddOrUpdate(url, 1, (_, c) => c + 1);
            gate = _locks.GetOrAdd(url, _ => new SemaphoreSlim(1, 1));
        }
        var entered = false;
        try
        {
            // #468: a caller cancelled while queued does NOT remove the gate here — that pair-
            // removed the shared instance out from under the still-running first loader, minted
            // a second gate for the next caller and raced a newer load against an older
            // snapshot. Cleanup is ExitInflight's job: the LAST leaver removes the gate.
            await gate.WaitAsync(ct);
            entered = true;
            try
            {
                if (TryGetFresh(url, out var rechecked))
                    return rechecked;

                IReadOnlyList<IpPrefix> prefixes;
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
                if (entered) gate.Release();
            }
        }
        finally
        {
            ExitInflight(url, gate);
        }
    }

    /// <summary>
    /// Decrements the active-loader count for <paramref name="url"/>; the LAST leaver removes
    /// the gate, so an unbacked gate cannot outlive its participants (the #358 orphan-lock
    /// hygiene). #468: a cancelled waiter no longer removes the gate directly — that pair-
    /// removed the shared instance out from under the still-running first loader, minted a
    /// second gate for the next caller, and raced a newer load against an older snapshot.
    /// #478: the whole sequence runs as one atomic section under <see cref="_gateSync"/>, with
    /// registration taking the same lock — the previous unlocked decrement→zero-check→gate-
    /// removal could interleave with a fresh registration landing between the check and the
    /// pair-removal, deleting a gate its loader still held (duplicate concurrent fetch,
    /// last-writer-wins on the cache entry).
    /// </summary>
    private void ExitInflight(string url, SemaphoreSlim gate)
    {
        lock (_gateSync)
        {
            var remaining = _inflight.AddOrUpdate(url, 0, (_, c) => c - 1);
            if (remaining > 0) return;
            _inflight.TryRemove(new KeyValuePair<string, int>(url, 0));
            ((ICollection<KeyValuePair<string, SemaphoreSlim>>)_locks).Remove(new KeyValuePair<string, SemaphoreSlim>(url, gate));
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
        // #487: HashSet — the Contains in the oldest-selection loop below made eviction O(n²) at
        // the 1024-entry cap.
        var toEvict = new HashSet<string>();
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
            // #450 review: a URL with active loaders keeps its gate (see SweepIfNeeded).
            // #478: inflight-check and gate removal run under _gateSync so a loader registering
            // between the check and the removal cannot lose its gate to the eviction.
            lock (_gateSync)
            {
                if (_inflight.TryGetValue(key, out var active) && active > 0) continue;
                if (_cache.TryRemove(key, out _))
                    _locks.TryRemove(key, out _);
            }
        }
    }

    private bool TryGetFresh(string url, out IReadOnlyList<IpPrefix> list)
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

    /// <summary>
    /// #426: amortized housekeeping, run every <see cref="_sweepEvery"/> calls. (1) Removes EXPIRED
    /// entries regardless of the entry cap — they used to be pinned until the cap was hit. (2)
    /// Enforces the total-prefix budget: over budget, the OLDEST positive entries are dropped until
    /// under (a later caller re-fetches them — a duplicate idempotent GET, never a correctness
    /// loss, the same tradeoff <see cref="EvictIfAtCapacity"/> accepts).
    /// </summary>
    private void SweepIfNeeded()
    {
        if (Interlocked.Increment(ref _callsSinceSweep) < _sweepEvery)
            return;
        Volatile.Write(ref _callsSinceSweep, 0);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var toEvict = new HashSet<string>();   // #487: O(1) Contains for the budget loop below
        long total = 0;
        foreach (var (key, entry) in _cache)
        {
            var ttl = entry.Negative ? _negativeTtl : _positiveTtl;
            if (now - entry.CachedAt >= ttl)
            {
                toEvict.Add(key);
                continue;
            }
            if (!entry.Negative)
                total += entry.List.Count;
        }

        foreach (var kvp in _cache
                     .Where(kvp => !kvp.Value.Negative && !toEvict.Contains(kvp.Key))
                     .OrderBy(kvp => kvp.Value.CachedAt))
        {
            if (total <= _maxTotalPrefixes) break;
            toEvict.Add(kvp.Key);
            total -= kvp.Value.List.Count;
        }

        foreach (var key in toEvict)
        {
            // #450 review: a URL with active loaders keeps its gate (its loader writes the entry
            // on its own reference) — removing the gate would mint a duplicate concurrent fetch.
            // #478: same _gateSync atomicity as EvictIfAtCapacity's removal loop.
            lock (_gateSync)
            {
                if (_inflight.TryGetValue(key, out var active) && active > 0) continue;
                if (_cache.TryRemove(key, out _))
                    _locks.TryRemove(key, out _);
            }
        }
    }
}
