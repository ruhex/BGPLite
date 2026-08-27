using System.Collections.Concurrent;
using BGPLite.Configuration;
using Microsoft.Extensions.Logging;
using BGPLite.Contracts;

namespace BGPLite.Providers;

public sealed class PrefixService : IPrefixService
{
    private readonly RipeStatProvider _ripeStat;
    private readonly IPrefixSourceService _prefixSources;
    private readonly AppConfig _config;
    private readonly HttpPrefixProvider _httpProvider;
    // asn → (prefix list, cached at, is negative). Negative entries (failed RIPEstat fetches) use
    // _negativeTtl. The tuple is shaped to mirror PrefixSourceService so the resilience semantics
    // (stale-on-failure, negative cache, bounded sweep) are identical across both caches.
    private readonly ConcurrentDictionary<uint, (IReadOnlyList<(uint Prefix, byte Length)> Data, DateTime CachedAt, bool Negative)> _cache = new();
    // asn → gate serializing the cache-miss fetch path (prevents thundering herd on cold/expired
    // ASNs — #164). Mirrors PrefixSourceService._locks.
    private readonly ConcurrentDictionary<uint, SemaphoreSlim> _locks = new();
    private readonly TimeSpan _cacheTtl;
    private readonly TimeSpan _negativeTtl;
    // Upper bound on _cache/_locks entries (#165). Without a cap, a malicious or churning peer
    // querying distinct ASNs grows the dictionaries without limit. Exceeded → least-recently-used
    // sweep before insert. The configured ASN universe is small (operator AsnLists), so a generous
    // default does not constrain real deployments.
    private readonly int _maxCacheEntries;
    private readonly ILogger<PrefixService>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly UserSourceCache _userSourceCache;
    // #229: gate serializing the RU/default-prefix cache-miss fetch path. The RU set is a single
    // shared cache (unlike the per-ASN _cache), so a single SemaphoreSlim is enough — it prevents a
    // thundering herd of N concurrent sessions from all re-fetching the ~11k-prefix default list
    // when the TTL elapses. Mirrors the per-ASN gate pattern in GetPrefixesAsync.
    private readonly SemaphoreSlim _ruGate = new(1, 1);

    public PrefixService(AppConfig config, RipeStatProvider ripeStat, IPrefixSourceService prefixSources, HttpPrefixProvider httpProvider, TimeSpan? cacheTtl = null, ILogger<PrefixService>? logger = null, TimeSpan? negativeTtl = null, int? maxCacheEntries = null, TimeProvider? timeProvider = null)
    {
        _config = config;
        _ripeStat = ripeStat;
        _prefixSources = prefixSources;
        _httpProvider = httpProvider;
        _cacheTtl = cacheTtl ?? TimeSpan.FromHours(1);
        _negativeTtl = negativeTtl ?? TimeSpan.FromSeconds(30);
        // 2× a generous operator-configured ASN universe (a route server typically tracks tens to
        // low-hundreds of origin ASNs). The cap defends against unbounded growth from adversarial /
        // churn traffic, not normal operation.
        _maxCacheEntries = maxCacheEntries ?? 4096;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _userSourceCache = new UserSourceCache(logger: logger, timeProvider: _timeProvider);
    }

    /// <summary>
    /// Fetches a per-peer user-supplied URL prefix-list source (issues #147 / #150). The URL is
    /// peer-supplied (not in <c>AppConfig.PrefixSources</c>, so the name-keyed <see cref="PrefixSourceService"/>
    /// cache can't help); instead a URL-keyed TTL cache (<see cref="UserSourceCache"/>) dedupes fetches
    /// across peers and serves a stale copy on transient failure. SSRF defense (#144) is inherited from
    /// the http provider's named client. The <c>Active</c>
    /// lifecycle is handled by the caller (LoadPeerRoutingView filters Active before this is reached),
    /// so paused sources are never advertised regardless of cache state.
    /// </summary>
    public async Task<IReadOnlyList<(uint Prefix, byte Length)>> GetUserSourcePrefixesAsync(string name, string url, string? community, CancellationToken ct = default)
    {
        var source = new PrefixSourceConfig
        {
            Kind = "http",
            Name = name,
            Url = url,
            Community = community
        };
        return await _userSourceCache.GetOrLoadAsync(url, name, async ct =>
        {
            var result = await _httpProvider.LoadAsync(source, ct: ct);
            return result.Prefixes;
        }, ct);
    }

    public async Task<IReadOnlyList<(uint Prefix, byte Length)>> GetPrefixesAsync(uint asn, CancellationToken ct = default)
    {
        // Fast path: fresh entry (positive or negative within its TTL).
        if (TryGetFresh(asn, out var fresh))
            return fresh;

        // Serialize per-ASN so concurrent callers share a single RIPEstat fetch — no thundering herd
        // on a cold or just-expired ASN (#164). Mirrors PrefixSourceService.LoadCachedAsync.
        var gate = _locks.GetOrAdd(asn, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-check after acquiring the lock: another caller may have just populated the entry.
            if (TryGetFresh(asn, out var rechecked))
                return rechecked;

            IReadOnlyList<(uint Prefix, byte Length)> prefixes;
            try
            {
                prefixes = await _ripeStat.GetPrefixesAsync(asn, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Stale-on-failure (#163): serve the last good copy regardless of its age so a
                // transient RIPEstat outage (429/5xx/network) does not drop the ASN's routes the
                // instant its TTL elapses. Asymmetric with the old behavior — previously the throw
                // propagated and (via GetPrefixesForAsns) the ASN's prefixes vanished from peers.
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
    /// negative within _negativeTtl).</summary>
    private bool TryGetFresh(uint asn, out IReadOnlyList<(uint Prefix, byte Length)> data)
    {
        data = null!;
        if (!_cache.TryGetValue(asn, out var entry)) return false;

        var ttl = entry.Negative ? _negativeTtl : _cacheTtl;
        if (_timeProvider.GetUtcNow().UtcDateTime - entry.CachedAt < ttl)
        {
            data = entry.Data;
            return true;
        }
        return false;
    }

    /// <summary>Enforces the _maxCacheEntries bound (#165). Called before inserting a NEW key.
    /// Removes a few least-recently-cached entries (by CachedAt) plus any expired entries it
    /// encounters, and drops the corresponding _locks entries. Under the lock of the caller's
    /// per-ASN gate, so the sweep is serialized against itself; ConcurrentDictionary enumeration
    /// is a snapshot and safe against concurrent writers.</summary>
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

    /// <summary>Bounds how many ASNs are resolved against RIPEstat concurrently on a cold cache
    /// (warm traffic is cache-flat). Keeps cold-start fan-out from tripping RIPEstat rate limits.</summary>
    private const int MaxDegreeOfParallelism = 8;

    public async Task<List<(uint Prefix, byte Length, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default)
    {
        // Materialize once: we enumerate for fan-out and again for ordered assembly.
        var asnList = asns as IList<uint> ?? asns.ToList();
        if (asnList.Count == 0) return [];

        // Resolve each DISTINCT ASN concurrently (bounded) — latency is max, not sum, on cold
        // RIPEstat misses. Duplicates are coalesced for the fan-out so they cannot race the cold
        // per-ASN cache and double-fetch (CodeRabbit #130); output multiplicity is preserved below.
        // Each ASN keeps its own try/catch so one failure (incl. cancellation) can't drop the others.
        using var gate = new SemaphoreSlim(MaxDegreeOfParallelism, MaxDegreeOfParallelism);
        var resolvedByAsn = new Dictionary<uint, Task<IReadOnlyList<(uint Prefix, byte Length)>>>();
        foreach (var asn in asnList.Distinct())
            resolvedByAsn[asn] = ResolveAsnAsync(asn, gate, ct);

        await Task.WhenAll(resolvedByAsn.Values);

        // Reassemble in ORIGINAL input order (and multiplicity) — byte-for-byte identical to the
        // prior sequential output, including for duplicate ASNs. Await each completed task (rather
        // than .Result) so a faulted task surfaces its real exception, not an AggregateException,
        // and never blocks the threadpool thread.
        var result = new List<(uint Prefix, byte Length, uint Asn)>();
        foreach (var asn in asnList)
            foreach (var (prefix, length) in await resolvedByAsn[asn])
                result.Add((prefix, length, asn));
        return result;
    }

    private async Task<IReadOnlyList<(uint Prefix, byte Length)>> ResolveAsnAsync(uint asn, SemaphoreSlim gate, CancellationToken ct)
    {
        try
        {
            await gate.WaitAsync(ct);
            try
            {
                return await GetPrefixesAsync(asn, ct);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // #225: when the caller's token is cancelled (shutdown / refresh-with-cancel), the OCE
            // MUST propagate — otherwise GetPrefixesForAsns silently returns a partial prefix list
            // (cancelled ASNs dropped to []) instead of throwing. This is the specific shutdown-path
            // regression #225 fixes; it mirrors the OCP-propagation policy enforced across the rest
            // of the prefix-sourcing stack (UserSourceCache #114, GetPrefixesAsync:97).
            //
            // The `when (ct.IsCancellationRequested)` guard is narrow on purpose: an OCE raised by
            // something OTHER than the caller's token (e.g. a Polly pipeline internal timeout using
            // its own linked CTS, surfacing here as OCE) is NOT rethrown — it falls through to the
            // generic catch below and is treated as a transient per-ASN failure. That is the desired
            // resilience behavior (one ASN's timeout should not abort the whole fan-out); only true
            // caller-initiated cancellation propagates.
            throw;
        }
        catch
        {
            // skip failed ASN (a transient RIPEstat error), continue with the others
            return [];
        }
    }

    public async Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default)
    {
        var prefixes = await GetPrefixesAsync(asn, ct);
        return prefixes.Count;
    }

    /// <summary>The RU/default prefix set — backed by the configured default prefix source.
    /// The projection is cached for one TTL so repeated calls (multiple sessions / refreshes)
    /// don't re-allocate the same ~11k-entry list.
    /// <para>
    /// #229: the cache is a single immutable <see cref="RuCacheEntry"/> reference swapped atomically
    /// via <see cref="Interlocked.Exchange(ref RuCacheEntry?, RuCacheEntry?)"/>, so a reader always
    /// observes a consistent (projection, timestamp) pair — no torn read where the new projection
    /// is paired with the old timestamp. A <see cref="SemaphoreSlim"/> gate serializes the cache-miss
    /// fetch so concurrent callers share one default-source load (thundering-herd defense), and
    /// stale-on-failure serves the last good copy on a transient fetch error (#163 parity).
    /// </para>
    /// </summary>
    private sealed record RuCacheEntry(List<(uint Prefix, byte Length, uint Asn)> Projected, long CachedAtTicks);
    private RuCacheEntry? _ruCache;

    public async Task<List<(uint Prefix, byte Length, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default)
    {
        // Fast path: fresh entry. A single Volatile.Read of the immutable entry reference gives a
        // consistent (projection, ticks) snapshot — no two-field torn read.
        var cached = Volatile.Read(ref _ruCache);
        if (cached is not null && _timeProvider.GetUtcNow().UtcDateTime.Ticks - cached.CachedAtTicks < _cacheTtl.Ticks)
            return cached.Projected;

        // Serialize the cache-miss fetch so concurrent callers share one default-source fetch
        // (thundering-herd defense — #229). Mirrors the per-ASN gate in GetPrefixesAsync.
        await _ruGate.WaitAsync(ct);
        try
        {
            // Re-check under the lock: another caller may have just populated the entry.
            cached = Volatile.Read(ref _ruCache);
            if (cached is not null && _timeProvider.GetUtcNow().UtcDateTime.Ticks - cached.CachedAtTicks < _cacheTtl.Ticks)
                return cached.Projected;

            List<(uint Prefix, byte Length, uint Asn)> projected;
            try
            {
                var prefixes = await _prefixSources.GetDefaultAsync(ct);
                projected = prefixes.Select(p => (p.Prefix, p.Length, 0u)).ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Stale-on-failure (#163 parity): serve the last good copy regardless of its age so
                // a transient default-source outage does not drop the RU/default routes the instant
                // the TTL elapses. Asymmetric with the no-cache path — previously the throw
                // propagated and unconfigured/unknown peers lost their RU routes.
                var stale = Volatile.Read(ref _ruCache);
                if (stale is not null)
                {
                    _logger?.LogWarning(ex,
                        "RU/default prefix source fetch failed; serving cached copy ({Count} prefixes).", stale.Projected.Count);
                    return stale.Projected;
                }
                throw;
            }

            // Swap the whole entry atomically — projection and timestamp move together, so a reader
            // can never observe a mismatched (new projection, old timestamp) pair.
            Interlocked.Exchange(ref _ruCache, new RuCacheEntry(projected, _timeProvider.GetUtcNow().UtcDateTime.Ticks));
            return projected;
        }
        finally
        {
            _ruGate.Release();
        }
    }

    /// <summary>Prefixes of a configured source by name (cache-through).</summary>
    public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default) =>
        _prefixSources.GetAsync(name, ct);

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        var lists = _config.RipeStat?.AsnLists ?? [];

        var allAsns = lists.SelectMany(l => l.Asns).Distinct().ToList();
        foreach (var asn in allAsns)
        {
            try
            {
                await GetPrefixesAsync(asn, ct);
                _logger?.LogInformation("WarmUp: AS{Asn} cached", asn);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "WarmUp: AS{Asn} failed", asn);
            }
        }

        // Pre-load all configured prefix sources (file/HTTP/...) into the in-memory cache.
        await _prefixSources.WarmUpAsync(ct);
    }
}
