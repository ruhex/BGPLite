using BGPLite.Configuration;
using BGPLite.Protocol;

namespace BGPLite.Routing;

public sealed class PeerCommunityFilter : IRouteFilter
{
    private readonly uint _localAsn;
    private readonly Func<string, uint?, CancellationToken, Task<HashSet<uint>>> _getCommunities;

    public PeerCommunityFilter(uint localAsn, Func<string, uint?, CancellationToken, Task<HashSet<uint>>> getCommunities)
    {
        _localAsn = localAsn;
        _getCommunities = getCommunities;
    }

    public bool AcceptIncoming(Route route, PeerConfig peer) => true;

    /// <summary>
    /// Resolves the peer's community allow-set once per send. This is the only place the
    /// (potentially database-backed) resolver runs on the advertise path — never per route.
    /// Asynchronous since #262: the resolver's DB read ran synchronously on the session send path.
    /// </summary>
    public async Task<IReadOnlySet<uint>> ResolveOutgoingAllowSetAsync(PeerConfig peer, CancellationToken ct = default)
        => await _getCommunities(peer.Address, peer.RemoteAsn, ct);

    public bool AcceptOutgoing(Route route, PeerConfig peer, IReadOnlySet<uint> allowSet)
    {
        var isEbgp = !peer.RemoteAsn.HasValue || peer.RemoteAsn.Value != _localAsn;

        if (HasWellKnownSuppressingCommunity(route, isEbgp))
            return false;

        if (allowSet.Count == 0)
            return true; // no filter = all routes

        // #389: with an ACTIVE allowlist, a community-less route is an untagged stranger — the
        // operator's allowlist is the peer's consent to receive specific tags, and an untagged
        // route carries no such consent. Default-deny; documented as D20.
        if (route.Communities.Count == 0)
            return false;

        foreach (var c in route.Communities)
        {
            if (allowSet.Contains(c))
                return true;
        }

        return false;
    }

    private static bool HasWellKnownSuppressingCommunity(Route route, bool isEbgp) =>
        route.Communities.Contains(BgpConstants.Community.NoAdvertise) ||
        (isEbgp && (
            route.Communities.Contains(BgpConstants.Community.NoExport) ||
            route.Communities.Contains(BgpConstants.Community.NoExportSubconfed)));
}
