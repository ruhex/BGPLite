using System.Collections.Concurrent;
using BGPLite.Configuration;
using BGPLite.Protocol;
using Microsoft.Extensions.Logging;
using BGPLite.Contracts;

namespace BGPLite.Providers;

public sealed class PrefixService : IPrefixService
{
    // #267 item 5: the per-ASN RIPEstat cache is a shared component — PrefixService (RipeStat
    // AsnLists + custom ASNs) and AsnPrefixProvider (Kind: asn sources) consume ONE instance, so
    // an ASN configured in both mechanisms is fetched and cached once. TTL/stale-on-failure/
    // negative-cache/eviction (#163/#164/#165) live there now.
    private readonly RipeStatPrefixCache _ripeStatCache;
    private readonly IPrefixSourceService _prefixSources;
    private readonly AppConfig _config;
    private readonly HttpPrefixProvider _httpProvider;
    // Freshness of the RU/default projection cache below (the per-ASN TTL moved to the shared cache).
    private readonly TimeSpan _ruCacheTtl;
    private readonly ILogger<PrefixService>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly UserSourceCache _userSourceCache;
    private readonly IPrefixSourceProvider _userSourceHttpProvider;
    // #229: gate serializing the RU/default-prefix cache-miss fetch path. The RU set is a single
    // shared cache (unlike the per-ASN _cache), so a single SemaphoreSlim is enough — it prevents a
    // thundering herd of N concurrent sessions from all re-fetching the ~11k-prefix default list
    // when the TTL elapses. Mirrors the per-ASN gate pattern in GetPrefixesAsync.
    private readonly SemaphoreSlim _ruGate = new(1, 1);

    public PrefixService(AppConfig config, RipeStatPrefixCache ripeStatCache, IPrefixSourceService prefixSources, HttpPrefixProvider httpProvider, TimeSpan? ruCacheTtl = null, ILogger<PrefixService>? logger = null, TimeProvider? timeProvider = null, int? userSourceTimeoutSeconds = null, Func<string, Task>? onSourceChanged = null, IPrefixSourceProvider? userSourceHttpProvider = null)
    {
        _config = config;
        _ripeStatCache = ripeStatCache;
        _prefixSources = prefixSources;
        _httpProvider = httpProvider;
        _ruCacheTtl = ruCacheTtl ?? TimeSpan.FromHours(1);
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        // #320: fetch budget for peer-supplied URL sources — generous for a real prefix list
        // (10 MB cap streams in well under it), bounded enough that a dripping/hung body cannot
        // stall the route dump behind the per-URL gate.
        _userSourceTimeoutSeconds = userSourceTimeoutSeconds ?? DefaultUserSourceTimeoutSeconds;
        _userSourceCache = new UserSourceCache(logger: logger, timeProvider: _timeProvider);
        // #416: convergence push for default-source changes detected on the RU path. Fired AFTER
        // _ruGate.Release() in GetRuPrefixesAsync — never while the gate is held.
        _onSourceChanged = onSourceChanged;
        // #425: the peer-supplied user-source path uses its OWN http provider (the retry-only
        // pipeline) so peer-controlled failures cannot open the breaker gating operator sources;
        // falls back to the operator provider when not wired (tests).
        _userSourceHttpProvider = userSourceHttpProvider ?? httpProvider;
        // #452: a committed content change to the DEFAULT source must invalidate the RU projection
        // immediately — the auto-refresh path (RefreshAsync) never fires the push callback, so
        // without this the fleet rebuild triggered afterwards re-reads the stale fast path for up
        // to the RU TTL. The handler is a Volatile.Write: cheap, non-blocking, safe off the
        // per-source gate (LoadCachedAsync fires the event only after releasing it).
        // Guarded: tests compose PrefixService WITHOUT a source service (the interface sits only
        // on the RU paths), passing null — those compositions simply get no invalidation signal.
        if (prefixSources is not null)
            _prefixSources.ContentCommitted += OnSourceContentCommitted;
    }

    /// <summary>
    /// #452 handler for <see cref="IPrefixSourceService.ContentCommitted"/>: drops the cached RU
    /// projection when the changed source is the one it is projected from. The next
    /// <see cref="GetRuPrefixesAsync"/> takes the gate path, re-projects from the already-new
    /// source-level cache, and reports no change — so the fleet push is not duplicated.
    /// </summary>
    private void OnSourceContentCommitted(string sourceName)
    {
        if (!string.Equals(sourceName, _config.DefaultPrefixSource, StringComparison.Ordinal))
            return;
        Volatile.Write(ref _ruCache, null);
    }

    /// <summary>Default per-fetch budget for peer-supplied URL sources (#320).</summary>
    public const int DefaultUserSourceTimeoutSeconds = 30;
    private readonly int _userSourceTimeoutSeconds;

    /// <summary>
    /// Fetches a per-peer user-supplied URL prefix-list source (issues #147 / #150). The URL is
    /// peer-supplied (not in <c>AppConfig.PrefixSources</c>, so the name-keyed <see cref="PrefixSourceService"/>
    /// cache can't help); instead a URL-keyed TTL cache (<see cref="UserSourceCache"/>) dedupes fetches
    /// across peers and serves a stale copy on transient failure. SSRF defense (#144) is inherited from
    /// the http provider's named client. The <c>Active</c>
    /// lifecycle is handled by the caller (LoadPeerRoutingView filters Active before this is reached),
    /// so paused sources are never advertised regardless of cache state.
    /// <para>
    /// #320: the config is built WITH a fetch budget. Without one, HttpPrefixProvider's linked-CTS
    /// timeout is never armed and the body-read loop is guarded only by the session token — a
    /// server that answers headers then drips (or never sends) the body hangs the whole route dump
    /// behind the per-URL gate while the session stays Established (KEEPALIVEs keep the hold timer
    /// fed). A timed-out fetch throws OCE with a live caller token — a per-source fetch failure per
    /// the #342 boundary — and is negative-cached by <see cref="UserSourceCache"/>, so repeated
    /// refreshes do not re-pay the budget.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetUserSourcePrefixesAsync(string name, string url, string? community, CancellationToken ct = default)
    {
        var source = new PrefixSourceConfig
        {
            Kind = "http",
            Name = name,
            Url = url,
            Community = community,
            Timeout = _userSourceTimeoutSeconds
        };
        var prefixes = await _userSourceCache.GetOrLoadAsync(url, name, async ct =>
        {
            var result = await _userSourceHttpProvider.LoadAsync(source, ct: ct);
            return result.Prefixes;   // provider-native IpPrefix list
        }, ct);
        return ToContract(prefixes);
    }

    public async Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetPrefixesAsync(uint asn, CancellationToken ct = default)
        // Per-ASN caching, stale-on-failure, negative TTL, fetch gates, and the #165 eviction
        // sweep all live in the shared cache (#267 item 5) — one wire fetch per ASN regardless
        // of which mechanism asked.
        => ToContract(await _ripeStatCache.GetPrefixesAsync(asn, ct));

    /// <summary>Bounds how many ASNs are resolved against RIPEstat concurrently on a cold cache
    /// (warm traffic is cache-flat). Keeps cold-start fan-out from tripping RIPEstat rate limits.</summary>
    private const int MaxDegreeOfParallelism = 8;

    public async Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default)
    {
        // Materialize once: we enumerate for fan-out and again for ordered assembly.
        var asnList = asns as IList<uint> ?? asns.ToList();
        if (asnList.Count == 0) return [];

        // Resolve each DISTINCT ASN concurrently (bounded) — latency is max, not sum, on cold
        // RIPEstat misses. Duplicates are coalesced for the fan-out so they cannot race the cold
        // per-ASN cache and double-fetch (CodeRabbit #130); output multiplicity is preserved below.
        // Each ASN keeps its own try/catch so one failure (incl. cancellation) can't drop the others.
        using var gate = new SemaphoreSlim(MaxDegreeOfParallelism, MaxDegreeOfParallelism);
        var resolvedByAsn = new Dictionary<uint, Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>>>();
        foreach (var asn in asnList.Distinct())
            resolvedByAsn[asn] = ResolveAsnAsync(asn, gate, ct);

        await Task.WhenAll(resolvedByAsn.Values);

        // Reassemble in ORIGINAL input order (and multiplicity) — byte-for-byte identical to the
        // prior sequential output, including for duplicate ASNs. Await each completed task (rather
        // than .Result) so a faulted task surfaces its real exception, not an AggregateException,
        // and never blocks the threadpool thread.
        var result = new List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>();
        foreach (var asn in asnList)
            foreach (var p in await resolvedByAsn[asn])
                result.Add((p.Prefix, p.Length, p.IsIpv4, asn));
        return result;
    }

    private async Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> ResolveAsnAsync(uint asn, SemaphoreSlim gate, CancellationToken ct)
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
        catch (Exception ex)
        {
            // Skip the failed ASN (a transient RIPEstat error) and continue with the others — but
            // not silently: its prefixes vanish from this cycle's advertisement, and the operator
            // must be able to tell "ASN no longer has prefixes" from "fetch failed" (#330).
            _logger?.LogWarning(ex, "AS{Asn}: RIPEstat resolve failed — advertising no prefixes for this ASN this cycle", asn);
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
    /// <para>
    /// #416: the convergence push for a detected content change fires AFTER <c>_ruGate.Release()</c>
    /// — the load itself runs callback-free (<see cref="IPrefixSourceService.LoadDefaultAsync"/>).
    /// Firing it while the gate was held deadlocked the fleet refresh: the push re-enters this
    /// method from other sessions' builds, which blocked on the gate the triggering frame still
    /// held, while that frame awaited the push (Task.WhenAll over every session) — a permanent
    /// circular wait with sessions staying Established and nothing in the logs.
    /// </para>
    /// </summary>
    private sealed record RuCacheEntry(List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)> Projected, long CachedAtTicks);
    private RuCacheEntry? _ruCache;
    private readonly Func<string, Task>? _onSourceChanged;

    public async Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default)
    {
        // Fast path: fresh entry. A single Volatile.Read of the immutable entry reference gives a
        // consistent (projection, ticks) snapshot — no two-field torn read.
        var cached = Volatile.Read(ref _ruCache);
        if (cached is not null && _timeProvider.GetUtcNow().UtcDateTime.Ticks - cached.CachedAtTicks < _ruCacheTtl.Ticks)
            return cached.Projected;

        // Serialize the cache-miss fetch so concurrent callers share one default-source fetch
        // (thundering-herd defense — #229). Mirrors the per-ASN gate in GetPrefixesAsync.
        List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)> projected;
        var changed = false;
        await _ruGate.WaitAsync(ct);
        try
        {
            // Re-check under the lock: another caller may have just populated the entry.
            cached = Volatile.Read(ref _ruCache);
            if (cached is not null && _timeProvider.GetUtcNow().UtcDateTime.Ticks - cached.CachedAtTicks < _ruCacheTtl.Ticks)
                return cached.Projected;

            try
            {
                // Callback-free load (#416): no onSourceChanged can fire while this frame holds
                // the gate. Failures propagate so the stale-on-failure branch below stays live —
                // a failed cold load must not be cached as a positive empty set.
                var (prefixes, loadChanged) = await _prefixSources.LoadDefaultAsync(ct);
                projected = prefixes.Select(p => (p.Address, p.Length, p.IsIpv4, 0u)).ToList();
                changed = loadChanged;
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
        }
        finally
        {
            _ruGate.Release();
        }

        // #416: the push runs AFTER the gate is released — a re-entrant GetRuPrefixesAsync (another
        // session's build) takes the fast path against the just-written cache instead of blocking
        // on this frame. A failure is logged, never propagated into the caller's route dump.
        if (changed && _onSourceChanged is not null)
        {
            try { await _onSourceChanged(_config.DefaultPrefixSource ?? string.Empty); }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "OnSourceChanged callback failed for the default prefix source.");
            }
        }

        return projected;
    }

    /// <summary>Prefixes of a configured source by name (cache-through).</summary>
    public async Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default) =>
        ToContract(await _prefixSources.GetAsync(name, ct));

    /// <summary>
    /// Provider-native <see cref="IpPrefix"/> values mapped onto the Contracts tuple layout
    /// (Contracts is a dependency-free leaf and cannot see the Protocol type).
    /// <para>
    /// #429: the projection is memoized by the NATIVE list instance (ConditionalWeakTable — freed
    /// with it, no eviction needed). All three cache layers return the SAME list instance until
    /// their next reload, so every warm call was re-projecting an 11k-entry list per peer per
    /// refresh; now the tuple list is built once per load and shared read-only.
    /// </para>
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable
        <IReadOnlyList<IpPrefix>, IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> ContractMemo = new();

    private static IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)> ToContract(IReadOnlyList<IpPrefix> prefixes) =>
        ContractMemo.GetValue(prefixes, p =>
            (IReadOnlyList<(UInt128, byte, bool)>)p.Select(x => (x.Address, x.Length, x.IsIpv4)).ToList());

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
