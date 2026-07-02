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

    public bool AcceptOutgoing(Route route, PeerConfig peer)
    {
        var isEbgp = !peer.RemoteAsn.HasValue || peer.RemoteAsn.Value != _localAsn;

        if (HasWellKnownSuppressingCommunity(route, isEbgp))
            return false;

        var allowed = _getCommunities(peer.Address, peer.RemoteAsn);
        if (allowed.Count == 0)
            return true; // no filter = all routes

        if (route.Communities.Length == 0)
            return true; // routes without community always pass

        foreach (var c in route.Communities)
        {
            if (allowed.Contains(c))
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
