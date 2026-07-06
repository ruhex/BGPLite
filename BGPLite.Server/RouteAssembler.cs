using System.Net;
using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Routing;
using Microsoft.Extensions.Logging;

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
/// </summary>
internal sealed class RouteAssembler
{
    private readonly IPrefixService? _prefixService;
    private readonly IPeerStore? _peerStore;
    private readonly ICommunityResolver _communityResolver;
    private readonly IRouteFilter _routeFilter;
    private readonly AppConfig? _appConfig;
    private readonly BgpConfig _bgpConfig;
    private readonly RouteTable _routeTable;
    private readonly ILogger _logger;
    private readonly string _peer;

    public RouteAssembler(
        IPrefixService? prefixService,
        IPeerStore? peerStore,
        ICommunityResolver communityResolver,
        IRouteFilter routeFilter,
        AppConfig? appConfig,
        BgpConfig bgpConfig,
        RouteTable routeTable,
        ILogger logger,
        string peer)
    {
        _prefixService = prefixService;
        _peerStore = peerStore;
        _communityResolver = communityResolver;
        _routeFilter = routeFilter;
        _appConfig = appConfig;
        _bgpConfig = bgpConfig;
        _routeTable = routeTable;
        _logger = logger;
        _peer = peer;
    }

    /// <summary>
    /// Resolves the outbound route set for the given peer: the per-peer decision tree (RU defaults,
    /// subscriptions, custom prefixes, custom AS, user URL sources) or the shared-table fallback.
    /// Returns the filtered routes — the caller does aggregate + batch + send. No transport access.
    /// </summary>
    public async Task<List<Route>> BuildOutboundRoutesAsync(
        string peerIp, uint remoteAsn, PeerConfig filterPeerConfig, CancellationToken ct)
    {
        var nextHop = BgpConstants.IPAddressToUint(_bgpConfig.GetRouterIdAddress());
        var routes = new List<Route>();
        var defaultComms = _communityResolver.Resolve(
            new CommunitySource(CommunitySourceKind.PrefixSource, _appConfig?.DefaultPrefixSource));

        if (_peerStore is not null && _prefixService is not null && _appConfig is not null)
        {
            // Capture non-null locals so the compiler tracks the null-guard through the nested
            // branches without null-forgiving operators (#105 nullable).
            var peerStore = _peerStore;
            var prefixService = _prefixService;
            var appConfig = _appConfig;
            var peer = peerStore.LoadPeerRoutingView(peerIp, remoteAsn);
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
                    _logger.LogInformation("Unconfigured peer {Peer}, sending RU defaults", _peer);
                    try
                    {
                        var ruPrefixes = await prefixService.GetRuPrefixesAsync(ct);
                        foreach (var (prefix, length, _) in ruPrefixes)
                            routes.Add(MakeRoute(prefix, length, nextHop, null, defaultComms));
                        _logger.LogInformation("Sent {Count} RU prefixes to unconfigured peer {Peer}",
                            ruPrefixes.Count, _peer);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to fetch RU prefixes for {Peer}", _peer);
                    }

                    return FilterAndReturn(routes, filterPeerConfig);
                }

                _logger.LogInformation("Peer {Peer} subscriptions: [{Subs}]", _peer, string.Join(", ", subscriptionIds));

                var subscribedLists = appConfig.RipeStat?.AsnLists
                    .Where(l => subscriptionIds.Contains(l.Name))
                    .ToList() ?? [];

                // ASN-based lists — resolve per list so each list's community is stamped on its prefixes.
                var asnLists = subscribedLists.Where(l => l.Asns.Count > 0).ToList();

                _logger.LogInformation("Peer {Peer} resolved {Count} ASNs from subscriptions",
                    _peer, asnLists.SelectMany(l => l.Asns).Count());

                if (asnLists.Count > 0)
                {
                    var before = routes.Count;
                    foreach (var list in asnLists)
                    {
                        try
                        {
                            var comms = _communityResolver.Resolve(
                                new CommunitySource(CommunitySourceKind.AsnList, list.Name));
                            var prefixes = await prefixService.GetPrefixesForAsns(list.Asns, ct);
                            foreach (var (prefix, length, _) in prefixes)
                                // #85: AsPath is overwritten by the local ASN in the outbound codec
                                // (BuildUpdateAttributes), so the per-prefix asn value is never used
                                // on the wire — pass null instead of allocating [asn] per prefix.
                                routes.Add(MakeRoute(prefix, length, nextHop, null, comms));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to fetch prefixes for {Peer} (list '{List}')", _peer, list.Name);
                        }
                    }
                    _logger.LogInformation("Fetched {Count} prefixes for {Peer} from ASN subscriptions",
                        routes.Count - before, _peer);
                }

                // Country-based lists (e.g. RU with no ASNs → use local nets.txt).
                var countryLists = subscribedLists.Where(l => l.Asns.Count == 0 && l.Country is not null).ToList();
                if (countryLists.Count > 0)
                {
                    try
                    {
                        var comms = _communityResolver.Resolve(
                            new CommunitySource(CommunitySourceKind.Country, countryLists[0].Name));
                        var ruPrefixes = await prefixService.GetRuPrefixesAsync(ct);
                        foreach (var (prefix, length, _) in ruPrefixes)
                            routes.Add(MakeRoute(prefix, length, nextHop, null, comms));
                        _logger.LogInformation("Fetched {Count} RU prefixes for {Peer}", ruPrefixes.Count, _peer);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to fetch RU prefixes for {Peer}", _peer);
                    }
                }

                // Prefix-source subscriptions: subscribed names that match a configured PrefixSource.
                var resolvedAsRipe = subscribedLists.Select(l => l.Name).ToHashSet();
                var prefixSources = appConfig.PrefixSources;  // safe: we're inside the _appConfig is not null guard
                var sourceNames = subscriptionIds
                    .Where(n => !resolvedAsRipe.Contains(n) && prefixSources.Any(s => s.Name == n))
                    .ToList();
                foreach (var name in sourceNames)
                {
                    try
                    {
                        var comms = _communityResolver.Resolve(new CommunitySource(CommunitySourceKind.PrefixSource, name));
                        var srcPrefixes = await prefixService.GetSourcePrefixesAsync(name, ct);
                        foreach (var (prefix, length) in srcPrefixes)
                            routes.Add(MakeRoute(prefix, length, nextHop, null, comms));
                        _logger.LogInformation("Fetched {Count} prefixes from source '{Source}' for {Peer}",
                            srcPrefixes.Count, name, _peer);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to fetch source '{Source}' for {Peer}", name, _peer);
                    }
                }

                _logger.LogInformation("Peer {Peer} has {SubRoutes} subscription routes + {CustomCount} custom prefixes",
                    _peer, routes.Count, customPrefixes.Count);

                // Custom prefixes carry the static "custom prefix" community (<Asn>:100).
                var customPrefixComms = _communityResolver.Resolve(new CommunitySource(CommunitySourceKind.Custom));
                foreach (var cidr in customPrefixes)
                {
                    var slash = cidr.IndexOf('/');
                    var ip = IPAddress.Parse(cidr[..slash]);
                    var length = byte.Parse(cidr[(slash + 1)..]);
                    var prefix = BgpConstants.IPAddressToUint(ip);
                    routes.Add(MakeRoute(prefix, length, nextHop, null, customPrefixComms));
                }

                // Add custom AS prefixes. Custom-AS routes carry the static "custom AS" community.
                if (customAsns.Count > 0)
                {
                    try
                    {
                        var customAsnComms = _communityResolver.Resolve(new CommunitySource(CommunitySourceKind.CustomAsn));
                        var asnPrefixes = await prefixService.GetPrefixesForAsns(customAsns, ct);
                        foreach (var (prefix, length, _) in asnPrefixes)
                            routes.Add(MakeRoute(prefix, length, nextHop, null, customAsnComms));
                        _logger.LogInformation("Peer {Peer} custom AS: {Asns} -> {Count} prefixes",
                            _peer, string.Join(",", customAsns), asnPrefixes.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to fetch custom AS prefixes for {Peer}", _peer);
                    }
                }

                // Per-peer user URL sources (#143/#147): each Active source fetched + community-stamped.
                foreach (var source in peer.UserSources)
                {
                    await AddUserSourceRoutesAsync(
                        routes, source, nextHop, prefixService, _communityResolver, _logger, _peer, ct);
                }

                _logger.LogInformation("Sending {Count} total routes to {Peer}", routes.Count, _peer);

                // Configured peer resolved 0 prefixes — fall back to RU.
                if (routes.Count == 0)
                {
                    _logger.LogInformation("Peer {Peer} resolved 0 prefixes, falling back to RU defaults", _peer);
                    try
                    {
                        var ruPrefixes = await prefixService.GetRuPrefixesAsync(ct);
                        foreach (var (prefix, length, _) in ruPrefixes)
                            routes.Add(MakeRoute(prefix, length, nextHop, null, defaultComms));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to fetch RU fallback for {Peer}", _peer);
                    }
                }

                return FilterAndReturn(routes, filterPeerConfig);
            }
            else
            {
                // Unknown peer — auto-register and send default RU list.
                _logger.LogInformation("Unknown peer {Ip}, auto-registering with RU defaults", _peer);
                peerStore.CreatePeer(peerIp, remoteAsn, null);

                try
                {
                    var ruPrefixes = await prefixService.GetRuPrefixesAsync(ct);
                    foreach (var (prefix, length, _) in ruPrefixes)
                        routes.Add(MakeRoute(prefix, length, nextHop, null, defaultComms));
                    _logger.LogInformation("Fetched {Count} RU prefixes for unknown peer {Peer}",
                        ruPrefixes.Count, _peer);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to fetch RU prefixes for {Peer}", _peer);
                }

                return FilterAndReturn(routes, filterPeerConfig);
            }
        }

        // Final fallback: send from shared route table.
        var sharedAllowSet = _routeFilter.ResolveOutgoingAllowSet(filterPeerConfig);
        var filtered = new List<Route>();
        foreach (var r in _routeTable.Enumerate())
        {
            if (_routeFilter.AcceptOutgoing(r, filterPeerConfig, sharedAllowSet))
                filtered.Add(r);
        }
        return filtered;
    }

    /// <summary>Applies the per-peer outgoing community filter and returns the filtered list.</summary>
    private List<Route> FilterAndReturn(List<Route> routes, PeerConfig filterPeerConfig)
    {
        // Resolve the community allow-set ONCE for the whole send — not once per route (#79).
        var allowSet = _routeFilter.ResolveOutgoingAllowSet(filterPeerConfig);
        return routes.Where(r => _routeFilter.AcceptOutgoing(r, filterPeerConfig, allowSet)).ToList();
    }

    /// <summary>
    /// Builds a <see cref="Route"/> from its components. Static so it can be called from
    /// <see cref="AddUserSourceRoutesAsync"/> and unit-tested directly.
    /// </summary>
    internal static Route MakeRoute(
        uint prefix, byte length, uint nextHop, uint[]? asPath, uint[] communities,
        (uint Global, uint Local1, uint Local2)[]? largeCommunities = null) => new()
        {
            Prefix = prefix,
            PrefixLength = length,
            NextHop = nextHop,
            AsPath = asPath ?? [],
            Communities = communities,
            LargeCommunities = largeCommunities ?? []
        };

    /// <summary>
    /// Fetches one per-peer user URL source and appends its routes (stamped with the UserSource
    /// community) to <paramref name="routes"/>. Static so all dependencies are parameters —
    /// unit-testable without a RouteAssembler instance. Catches all exceptions except
    /// <see cref="OperationCanceledException"/> (#114 propagation).
    /// </summary>
    internal static async Task AddUserSourceRoutesAsync(
        List<Route> routes, CustomSourceView source, uint nextHop,
        IPrefixService prefixService, ICommunityResolver communityResolver,
        ILogger logger, string peerLabel, CancellationToken ct)
    {
        try
        {
            var comms = communityResolver.Resolve(
                new CommunitySource(CommunitySourceKind.UserSource, source.Name, source.Community));
            var prefixes = await prefixService.GetUserSourcePrefixesAsync(source.Name, source.Url, source.Community, ct);
            foreach (var (prefix, length) in prefixes)
                routes.Add(MakeRoute(prefix, length, nextHop, null, comms));
            logger.LogInformation("User-source '{Name}': {Count} prefixes for {Peer}", source.Name, prefixes.Count, peerLabel);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "User-source '{Name}' failed for {Peer}; skipped", source.Name, peerLabel);
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
