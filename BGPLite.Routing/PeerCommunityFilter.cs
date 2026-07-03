using BGPLite.Configuration;
using BGPLite.Protocol;

namespace BGPLite.Routing;

public sealed class PeerCommunityFilter : IRouteFilter
{
    private readonly uint _localAsn;
    private readonly Func<string, uint?, HashSet<uint>> _getCommunities;

    public PeerCommunityFilter(uint localAsn, Func<string, uint?, HashSet<uint>> getCommunities)
    {
        _localAsn = localAsn;
        _getCommunities = getCommunities;
    }

    public bool AcceptIncoming(Route route, PeerConfig peer) => true;

    /// <summary>
    /// Resolves the peer's community allow-set once per send. This is the only place the
    /// (potentially database-backed) resolver runs on the advertise path — never per route.
    /// </summary>
    public IReadOnlySet<uint> ResolveOutgoingAllowSet(PeerConfig peer)
        => _getCommunities(peer.Address, peer.RemoteAsn);

    public bool AcceptOutgoing(Route route, PeerConfig peer, IReadOnlySet<uint> allowSet)
    {
        var isEbgp = !peer.RemoteAsn.HasValue || peer.RemoteAsn.Value != _localAsn;

        if (HasWellKnownSuppressingCommunity(route, isEbgp))
            return false;

        if (allowSet.Count == 0)
            return true; // no filter = all routes

        if (route.Communities.Length == 0)
            return true; // routes without community always pass

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
