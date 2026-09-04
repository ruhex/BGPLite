using System.Net;
using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Routing;
using Microsoft.Extensions.Logging;
using BGPLite.Contracts;

namespace BGPLite.Server;

/// <summary>
/// Pure outbound route-assembly policy, extracted from <c>BgpSession</c> (#93 Phase 2). Resolves
/// "which prefixes does this peer get" — the RU-default vs subscription vs custom-prefix vs
/// custom-AS vs user-source decision tree — and returns the filtered <see cref="Route"/> set.
/// Does NOT send anything on the wire: the caller (BgpSession) does the aggregate + batch + send.
/// <para>
/// Absorbs the decision tree (<c>SendAllRoutesAsync</c>), <see cref="MakeRoute"/>,
/// <see cref="AddUserSourceRoutesAsync"/>, and <see cref="GroupByCommunitySet"/> — the policy +
/// route-shaping helpers that were <c>internal static</c> on BgpSession. The send/withdraw mirror
/// (<c>_advertisedPrefixes</c>) and the codec glue (<c>SendRoutesAsync</c>) stay in BgpSession.
/// </para>
/// <para>
/// #263: the peer store, prefix service and <c>AppConfig</c> are required. They used to be nullable
/// and a null in any of them silently switched every peer over to the shared route table, so a
/// dropped DI registration read as "why is this peer not getting its prefixes" rather than as a
/// startup error. That degraded mode is now <see cref="SharedTableRouteAssembler"/> — a type a
/// caller has to pick — and this one cannot be constructed without the configuration it needs.
/// </para>
/// </summary>
public sealed class RouteAssembler : IRouteAssembler
{
    private readonly IPrefixService _prefixService;
    private readonly IPeerStore _peerStore;
    private readonly ICommunityResolver _communityResolver;
    private readonly IRouteFilter _routeFilter;
    private readonly AppConfig _appConfig;
    private readonly BgpConfig _bgpConfig;
    private readonly ILogger<RouteAssembler> _logger;

    public RouteAssembler(
        IPrefixService prefixService,
        IPeerStore peerStore,
        ICommunityResolver communityResolver,
        IRouteFilter routeFilter,
        AppConfig appConfig,
        BgpConfig bgpConfig,
        ILogger<RouteAssembler> logger)
    {
        _prefixService = prefixService;
        _peerStore = peerStore;
        _communityResolver = communityResolver;
        _routeFilter = routeFilter;
        _appConfig = appConfig;
        _bgpConfig = bgpConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<List<Route>> BuildOutboundRoutesAsync(
        string peerIp, uint remoteAsn, PeerConfig filterPeerConfig, string peerLabel, CancellationToken ct)
    {
        var nextHop = BgpConstants.IPAddressToUint(_bgpConfig.GetRouterIdAddress());
        var routes = new List<Route>();
        var defaultComms = _communityResolver.Resolve(
            new CommunitySource(CommunitySourceKind.PrefixSource, _appConfig.DefaultPrefixSource));

        var peer = await _peerStore.LoadPeerRoutingViewAsync(peerIp, remoteAsn, ct);
        if (peer is not null)
        {
            var subscriptionIds = peer.Subscriptions;
            var customPrefixes = peer.CustomPrefixes;
            var customAsns = peer.CustomAsns;

            // Unconfigured peer — send RU defaults. A peer whose only configuration is active
            // user URL sources (#147) is NOT unconfigured — it must not fall through to RU.
            if (subscriptionIds.Count == 0 && customPrefixes.Count == 0 && customAsns.Count == 0
                && peer.UserSources.Count == 0)
            {
                _logger.LogInformation("Unconfigured peer {Peer}, sending RU defaults", peerLabel);
                try
                {
                    var ruPrefixes = await _prefixService.GetRuPrefixesAsync(ct);
                    foreach (var p in ruPrefixes)
                        routes.Add(MakeRoute(p.Prefix, p.Length, p.IsIpv4, nextHop, null, defaultComms));
                    _logger.LogInformation("Sent {Count} RU prefixes to unconfigured peer {Peer}",
                        ruPrefixes.Count, peerLabel);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }  // #114/#330: only CALLER cancellation — a per-source timeout OCE (HttpPrefixProvider's linked CTS, live ct) must stay a fetch failure below
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to fetch RU prefixes for {Peer}", peerLabel);
                }

                return await FilterAndReturnAsync(routes, filterPeerConfig, ct);
            }

            _logger.LogInformation("Peer {Peer} subscriptions: [{Subs}]", peerLabel, string.Join(", ", subscriptionIds));

            // #488 (D26): fetch outcome counters — the RU fallback below is suppressed only on a
            // TOTAL failure (every attempted fetch failed). A mixed build (one source failed,
            // another resolved — even to an empty list) is not total: the fallback keeps the
            // documented "configured peer resolved 0 prefixes" behavior.
            var fetchAttempts = 0;
            var fetchFailures = 0;

            var subscribedLists = _appConfig.RipeStat?.AsnLists
                .Where(l => subscriptionIds.Contains(l.Name))
                .ToList() ?? [];

            // ASN-based lists — resolve per list so each list's community is stamped on its prefixes.
            var asnLists = subscribedLists.Where(l => l.Asns.Count > 0).ToList();

            _logger.LogInformation("Peer {Peer} resolved {Count} ASNs from subscriptions",
                peerLabel, asnLists.SelectMany(l => l.Asns).Count());

            if (asnLists.Count > 0)
            {
                var before = routes.Count;
                foreach (var list in asnLists)
                {
                    fetchAttempts++;
                    try
                    {
                        var comms = _communityResolver.Resolve(
                            new CommunitySource(CommunitySourceKind.AsnList, list.Name));
                        var prefixes = await _prefixService.GetPrefixesForAsns(list.Asns, ct);
                        foreach (var p in prefixes)
                            // #85: AsPath is overwritten by the local ASN in the outbound codec
                            // (BuildUpdateAttributes), so the per-prefix asn value is never used
                            // on the wire — pass null instead of allocating [asn] per prefix.
                            routes.Add(MakeRoute(p.Prefix, p.Length, p.IsIpv4, nextHop, null, comms));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }  // #114/#330
                    catch (Exception ex)
                    {
                        fetchFailures++;   // #488
                        _logger.LogError(ex, "Failed to fetch prefixes for {Peer} (list '{List}')", peerLabel, list.Name);
                    }
                }
                _logger.LogInformation("Fetched {Count} prefixes for {Peer} from ASN subscriptions",
                    routes.Count - before, peerLabel);
            }

            // Country-based lists (e.g. RU with no ASNs → use local nets.txt).
            var countryLists = subscribedLists.Where(l => l.Asns.Count == 0 && l.Country is not null).ToList();
            if (countryLists.Count > 0)
            {
                fetchAttempts++;
                try
                {
                    var comms = _communityResolver.Resolve(
                        new CommunitySource(CommunitySourceKind.Country, countryLists[0].Name));
                    var ruPrefixes = await _prefixService.GetRuPrefixesAsync(ct);
                    foreach (var p in ruPrefixes)
                        routes.Add(MakeRoute(p.Prefix, p.Length, p.IsIpv4, nextHop, null, comms));
                    _logger.LogInformation("Fetched {Count} RU prefixes for {Peer}", ruPrefixes.Count, peerLabel);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }  // #114/#330
                catch (Exception ex)
                {
                    fetchFailures++;   // #488
                    _logger.LogError(ex, "Failed to fetch RU prefixes for {Peer}", peerLabel);
                }
            }

            // Prefix-source subscriptions: subscribed names that match a configured PrefixSource.
            var resolvedAsRipe = subscribedLists.Select(l => l.Name).ToHashSet();
            var prefixSources = _appConfig.PrefixSources ?? []; // #477: YAML null = no sources
            var sourceNames = subscriptionIds
                .Where(n => !resolvedAsRipe.Contains(n) && prefixSources.Any(s => s.Name == n))
                .ToList();

            // #488: a subscription matching no AsnLists entry and no PrefixSource is a config
            // typo — invisible until now (silently ignored on every build). Name it so the row
            // can be fixed.
            foreach (var unknown in subscriptionIds.Where(n =>
                         !resolvedAsRipe.Contains(n) && !prefixSources.Any(s => s.Name == n)))
                _logger.LogWarning(
                    "Subscription '{Name}' on {Peer} matches no RipeStat.AsnLists entry and no PrefixSource — ignored (fix the peer's subscription)",
                    unknown, peerLabel);

            foreach (var name in sourceNames)
            {
                fetchAttempts++;
                try
                {
                    var comms = _communityResolver.Resolve(new CommunitySource(CommunitySourceKind.PrefixSource, name));
                    var srcPrefixes = await _prefixService.GetSourcePrefixesAsync(name, ct);
                    foreach (var p in srcPrefixes)
                        routes.Add(MakeRoute(p.Prefix, p.Length, p.IsIpv4, nextHop, null, comms));
                    _logger.LogInformation("Fetched {Count} prefixes from source '{Source}' for {Peer}",
                        srcPrefixes.Count, name, peerLabel);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }  // #114/#330
                catch (Exception ex)
                {
                    fetchFailures++;   // #488
                    _logger.LogError(ex, "Failed to fetch source '{Source}' for {Peer}", name, peerLabel);
                }
            }

            _logger.LogInformation("Peer {Peer} has {SubRoutes} subscription routes + {CustomCount} custom prefixes",
                peerLabel, routes.Count, customPrefixes.Count);

            // Custom prefixes carry the static "custom prefix" community (<Asn>:100).
            // #236: parse via the canonical PrefixCidr parser — host-bit masking + range check +
            // IPv4-only, shared with the API and file sources. Custom prefixes are validated at
            // write time (ParseCustomPrefix), but a corrupt row or a write path that bypassed the
            // API must not throw a FormatException out of the BGP send path — skip + log instead.
            var customPrefixComms = _communityResolver.Resolve(new CommunitySource(CommunitySourceKind.Custom));
            var customRanges = new List<(uint Network, byte Length)>();
            foreach (var cidr in customPrefixes)
            {
                if (!PrefixCidr.TryParse(cidr, out var prefix, out var length))
                {
                    _logger.LogWarning("Skipping malformed custom prefix '{Cidr}' for {Peer}", cidr, peerLabel);
                    continue;
                }
                customRanges.Add((prefix, length));
                routes.Add(MakeRoute(prefix, length, isIpv4: true, nextHop, null, customPrefixComms));
            }

            // Add custom AS prefixes. Custom-AS routes carry the static "custom AS" community.
            if (customAsns.Count > 0)
            {
                fetchAttempts++;
                try
                {
                    var customAsnComms = _communityResolver.Resolve(new CommunitySource(CommunitySourceKind.CustomAsn));
                    var asnPrefixes = await _prefixService.GetPrefixesForAsns(customAsns, ct);
                    foreach (var p in asnPrefixes)
                        routes.Add(MakeRoute(p.Prefix, p.Length, p.IsIpv4, nextHop, null, customAsnComms));
                    _logger.LogInformation("Peer {Peer} custom AS: {Asns} -> {Count} prefixes",
                        peerLabel, string.Join(",", customAsns), asnPrefixes.Count);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }  // #114/#330
                catch (Exception ex)
                {
                    fetchFailures++;   // #488
                    _logger.LogError(ex, "Failed to fetch custom AS prefixes for {Peer}", peerLabel);
                }
            }

            // Per-peer user URL sources (#143/#147): each Active source fetched + community-stamped.
            foreach (var source in peer.UserSources)
            {
                fetchAttempts++;
                if (!await AddUserSourceRoutesAsync(
                        routes, source, nextHop, _prefixService, _communityResolver, _logger, peerLabel, ct))
                    fetchFailures++;
            }

            // #220 "suppress more-specifics": a custom prefix is an explicit operator override, so
            // any other route it covers is dropped before aggregation — the operator's broader
            // prefix wins over the source lists.
            if (customRanges.Count > 0)
            {
                var before = routes.Count;
                routes = SuppressCoveredByCustomPrefixes(routes, customRanges);
                if (routes.Count < before)
                    _logger.LogInformation(
                        "Suppressed {Suppressed} source prefixes covered by custom prefixes for {Peer}",
                        before - routes.Count, peerLabel);
            }

            _logger.LogInformation("Sending {Count} total routes to {Peer}", routes.Count, peerLabel);

            // Configured peer resolved 0 prefixes — fall back to RU. #488 (D26): suppressed only
            // on a TOTAL failure — every attempted fetch failed (RIPEstat outage / network
            // partition). Substituting the full RU dump then would advertise hundreds of thousands
            // of prefixes the peer never asked for — fail CLOSED: the peer keeps an empty set and
            // the per-source errors above carry the cause. A MIXED build (some failed, some
            // resolved to empty) is not total and keeps the documented fallback.
            if (routes.Count == 0 && fetchAttempts > 0 && fetchFailures == fetchAttempts)
            {
                _logger.LogWarning(
                    "Peer {Peer} resolved 0 prefixes WITH fetch failures — NOT falling back to RU defaults (total-source failure fails closed)",
                    peerLabel);
            }
            else if (routes.Count == 0)
            {
                _logger.LogInformation("Peer {Peer} resolved 0 prefixes, falling back to RU defaults", peerLabel);
                try
                {
                    var ruPrefixes = await _prefixService.GetRuPrefixesAsync(ct);
                    foreach (var p in ruPrefixes)
                        routes.Add(MakeRoute(p.Prefix, p.Length, p.IsIpv4, nextHop, null, defaultComms));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }  // #114/#330
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to fetch RU fallback for {Peer}", peerLabel);
                }
            }

            return await FilterAndReturnAsync(routes, filterPeerConfig, ct);
        }
        else
        {
            // A cancelled token here means the session is being torn down (Dispose cancels its
            // token before a peer deletion removes the row, #323) — a refresh or initial dump that
            // straddles the Dispose must not auto-register the just-deleted peer back into the
            // store. Shutdown (StopAsync) cancels the same token, so the gate covers it too.
            if (ct.IsCancellationRequested)
            {
                _logger.LogInformation("Cancelled build for unknown peer {Ip} — not auto-registering", peerLabel);
                return await FilterAndReturnAsync(routes, filterPeerConfig, ct);
            }

            // Unknown peer — auto-register and send default RU list.
            _logger.LogInformation("Unknown peer {Ip}, auto-registering with RU defaults", peerLabel);
            await _peerStore.CreatePeerAsync(peerIp, remoteAsn, null, ct);

            try
            {
                var ruPrefixes = await _prefixService.GetRuPrefixesAsync(ct);
                foreach (var p in ruPrefixes)
                    routes.Add(MakeRoute(p.Prefix, p.Length, p.IsIpv4, nextHop, null, defaultComms));
                _logger.LogInformation("Fetched {Count} RU prefixes for unknown peer {Peer}",
                    ruPrefixes.Count, peerLabel);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }  // #114/#330
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch RU prefixes for {Peer}", peerLabel);
            }

            return await FilterAndReturnAsync(routes, filterPeerConfig, ct);
        }
    }

    /// <summary>Applies the per-peer outgoing community filter and returns the filtered list.</summary>
    private async Task<List<Route>> FilterAndReturnAsync(List<Route> routes, PeerConfig filterPeerConfig, CancellationToken ct)
    {
        // Resolve the community allow-set ONCE for the whole send — not once per route (#79).
        var allowSet = await _routeFilter.ResolveOutgoingAllowSetAsync(filterPeerConfig, ct);
        return routes.Where(r => _routeFilter.AcceptOutgoing(r, filterPeerConfig, allowSet)).ToList();
    }

    /// <summary>
    /// #220 "suppress more-specifics": a custom prefix is an explicit operator override, so any
    /// SOURCE route covered by one is dropped from the outbound list, regardless of its source or
    /// community set. Two things survive: an exact custom==source duplicate (its communities get
    /// unioned by <c>BgpSession.MergeDuplicatePrefixes</c>, #209) and every CONFIGURED CUSTOM
    /// prefix itself — nested customs (e.g. /8 + a deliberate /16) must not suppress each other
    /// (CodeRabbit on the integration review). Runs on the flat per-peer list BEFORE the
    /// per-community-set aggregator sees it. Extracted as a pure function for unit tests.
    /// </summary>
    internal static List<Route> SuppressCoveredByCustomPrefixes(
        List<Route> routes, List<(uint Network, byte Length)> customRanges)
    {
        if (customRanges.Count == 0)
            return routes; // nothing to suppress — skip the pass entirely

        static UInt128 Mask(byte length) => length == 0 ? 0u : (UInt128)(0xFFFFFFFFu << (32 - length));

        // #429: precompute the coverage index ONCE (exact-match set + length → custom networks)
        // instead of scanning the custom list per route — O(routes + customs) instead of the
        // O(routes × customs) mask+compare the per-route Where paid (11k routes × 1k customs ≈
        // 11M compares per peer per refresh, multiplied by fleet size on a RefreshAllEstablished).
        // #450 review: the per-length network sets are HASH sets (per-route Contains is O(1)),
        // and the mask is stored once per length (identical for every network of that length).
        var exact = new HashSet<(uint Network, byte Length)>(customRanges);
        var networksByLength = new Dictionary<byte, (UInt128 Mask, HashSet<UInt128> Networks)>(customRanges.Count);
        foreach (var cr in customRanges)
        {
            if (!networksByLength.TryGetValue(cr.Length, out var entry))
                networksByLength[cr.Length] = entry = (Mask(cr.Length), []);
            entry.Networks.Add(cr.Network);
        }

        bool IsCovered(Route r)
        {
            foreach (var (length, (mask, networks)) in networksByLength)
            {
                if (length >= r.PrefixLength) continue; // only a STRICTLY broader custom covers
                if (networks.Contains((uint)r.Prefix & mask))
                    return true;
            }
            return false;
        }

        return routes
            .Where(r =>
                // A configured custom prefix is never suppressed — including by a broader
                // custom prefix of the same peer. Exact (network, length) == custom match.
                (r.IsIpv4 && exact.Contains(((uint)r.Prefix, r.PrefixLength)))
                || !r.IsIpv4
                || !IsCovered(r))
            .ToList();
    }

    /// <summary>
    /// Builds a <see cref="Route"/> from its components. Static so it can be called from
    /// <see cref="AddUserSourceRoutesAsync"/> and unit-tested directly. IPv4 addresses occupy
    /// the low 32 bits of <paramref name="prefix"/> (the implicit uint→UInt128 widening);
    /// <paramref name="isIpv4"/> carries the family (#14 phase 4).
    /// </summary>
    internal static Route MakeRoute(
        UInt128 prefix, byte length, bool isIpv4, uint nextHop, uint[]? asPath, uint[] communities,
        (uint Global, uint Local1, uint Local2)[]? largeCommunities = null) => new()
        {
            Prefix = prefix,
            IsIpv4 = isIpv4,
            PrefixLength = length,
            NextHop = nextHop,
            AsPath = asPath ?? [],
            Communities = communities,
            LargeCommunities = largeCommunities ?? []
        };

    /// <summary>
    /// Fetches one per-peer user URL source and appends its routes (stamped with the UserSource
    /// community) to <paramref name="routes"/>. Static so all dependencies are parameters —
    /// unit-testable without a RouteAssembler instance. Catches all exceptions except an OCE
    /// raised by the CALLER's cancellation (#114/#342): a per-source timeout OCE (a live token,
    /// e.g. #320's linked CTS in HttpPrefixProvider) is a fetch failure like any other, so one
    /// slow URL skips its source instead of aborting the whole dump.
    /// </summary>
    /// <returns><c>true</c> when the source loaded (even to an empty list); <c>false</c> when the
    /// fetch failed — the #488 fail-closed fallback gate consumes the failure signal.</returns>
    internal static async Task<bool> AddUserSourceRoutesAsync(
        List<Route> routes, CustomSourceView source, uint nextHop,
        IPrefixService prefixService, ICommunityResolver communityResolver,
        ILogger logger, string peerLabel, CancellationToken ct)
    {
        try
        {
            var comms = communityResolver.Resolve(
                new CommunitySource(CommunitySourceKind.UserSource, source.Name, source.Community));
            var prefixes = await prefixService.GetUserSourcePrefixesAsync(source.Name, source.Url, source.Community, ct);
            foreach (var p in prefixes)
                routes.Add(MakeRoute(p.Prefix, p.Length, p.IsIpv4, nextHop, null, comms));
            logger.LogInformation("User-source '{Name}': {Count} prefixes for {Peer}", source.Name, prefixes.Count, peerLabel);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }  // #114/#342: only CALLER cancellation — a per-source timeout OCE (#320's linked CTS, live ct) must stay a fetch failure below
        catch (Exception ex)
        {
            logger.LogWarning(ex, "User-source '{Name}' failed for {Peer}; skipped", source.Name, peerLabel);
            return false;
        }
    }

    /// <summary>
    /// Partitions routes into groups that share an identical (regular + large) community set,
    /// so each emitted UPDATE carries a single COMMUNITY and a single LARGE_COMMUNITY attribute.
    /// </summary>
    internal static List<List<Route>> GroupByCommunitySet(IReadOnlyList<Route> routes)
    {
        if (routes.Count == 0)
            return [];

        var first = routes[0];
        for (var i = 1; i < routes.Count; i++)
        {
            if (!SameCommunitySet(first, routes[i]))
                return PartitionByCommunitySet(routes);
        }

        return [new List<Route>(routes)];
    }

    private static bool SameCommunitySet(Route a, Route b) =>
        CommunitySetComparer.Instance.Equals(a.Communities, b.Communities) &&
        LargeCommunitySetComparer.Instance.Equals(a.LargeCommunities, b.LargeCommunities);

    private static List<List<Route>> PartitionByCommunitySet(IReadOnlyList<Route> routes) =>
        routes.GroupBy(r => (r.Communities, r.LargeCommunities), CommunitySetPairComparer.Instance)
              .Select(g => g.ToList())
              .ToList();
}
