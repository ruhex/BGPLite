using BGPLite.Configuration;

namespace BGPLite.Routing;

public sealed class AllowAllFilter : IRouteFilter
{
    private static readonly IReadOnlySet<uint> EmptyAllowSet = new HashSet<uint>();

    public static AllowAllFilter Instance { get; } = new();

    public bool AcceptIncoming(Route route, PeerConfig peer) => true;

    public Task<IReadOnlySet<uint>> ResolveOutgoingAllowSetAsync(PeerConfig peer, CancellationToken ct = default)
        => Task.FromResult(EmptyAllowSet);

    public bool AcceptOutgoing(Route route, PeerConfig peer, IReadOnlySet<uint> allowSet) => true;
}
