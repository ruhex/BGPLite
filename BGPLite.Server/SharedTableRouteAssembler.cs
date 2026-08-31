using BGPLite.Configuration;
using BGPLite.Routing;
using Microsoft.Extensions.Logging;

namespace BGPLite.Server;

/// <summary>
/// The explicit degraded assembler (#263): serves every peer the shared route table's SEEDED
/// routes, with no per-peer configuration at all. This is what a <see cref="BgpSession"/> built
/// without an <see cref="IRouteAssembler"/> falls back to — previously the same behavior arose
/// implicitly, from <c>RouteAssembler</c> being handed a null peer store / prefix service /
/// <c>AppConfig</c>, which made a composition mistake indistinguishable from a deliberate choice.
/// <para>
/// It logs its activation ONCE at Warning: reaching it in production means peers receive the seed
/// instead of what their operator selected, which is a wrong route set rather than a disabled
/// feature, and the per-send warning #307 added would otherwise repeat on every refresh.
/// </para>
/// </summary>
public sealed class SharedTableRouteAssembler : IRouteAssembler
{
    private readonly RouteTable _routeTable;
    private readonly IRouteFilter _routeFilter;
    private readonly ILogger _logger;
    private int _announced;

    /// <summary>Serves <paramref name="routeTable"/>'s unowned entries, filtered per peer.</summary>
    public SharedTableRouteAssembler(RouteTable routeTable, IRouteFilter routeFilter, ILogger logger)
    {
        _routeTable = routeTable;
        _routeFilter = routeFilter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<List<Route>> BuildOutboundRoutesAsync(
        string peerIp, uint remoteAsn, PeerConfig filterPeerConfig, string peerLabel, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _announced, 1) == 0)
        {
            _logger.LogWarning(
                "Route assembly is running WITHOUT per-peer configuration — every peer, starting with " +
                "{Peer}, receives the shared table's seeded routes instead of its configured prefixes. " +
                "In a production composition this is a wiring error (#263).",
                peerLabel);
        }

        // EnumerateUnowned, not Enumerate: everything a peer announced inbound is installed in this
        // same table owned by its session (#289). Advertising those here would hand one peer's
        // injected routes to every other peer — a tenant-isolation failure, not just a wrong list.
        // The startup seed is written with no owner and is what this fallback is meant to serve.
        var allowSet = await _routeFilter.ResolveOutgoingAllowSetAsync(filterPeerConfig, ct);
        var filtered = new List<Route>();
        foreach (var route in _routeTable.EnumerateUnowned())
        {
            if (_routeFilter.AcceptOutgoing(route, filterPeerConfig, allowSet))
                filtered.Add(route);
        }

        return filtered;
    }
}
