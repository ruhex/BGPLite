using BGPLite.Configuration;

namespace BGPLite.Routing;

public interface IRouteFilter
{
    bool AcceptIncoming(Route route, PeerConfig peer);

    /// <summary>
    /// Resolves the per-peer state the outgoing filter needs, ONCE per send (the community
    /// allow-set, which may require a database roundtrip). The returned set is passed to
    /// <see cref="AcceptOutgoing"/> for every route in that send, so the resolution happens once
    /// per peer per refresh rather than once per advertised route. An empty set means "no
    /// community restriction" (all routes pass).
    /// </summary>
    IReadOnlySet<uint> ResolveOutgoingAllowSet(PeerConfig peer);

    /// <summary>
    /// Per-route outgoing decision. <paramref name="allowSet"/> is the value returned by
    /// <see cref="ResolveOutgoingAllowSet"/> for the current send and must not perform any
    /// per-route I/O.
    /// </summary>
    bool AcceptOutgoing(Route route, PeerConfig peer, IReadOnlySet<uint> allowSet);
}
