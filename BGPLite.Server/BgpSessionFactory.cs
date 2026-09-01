using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Routing;
using Microsoft.Extensions.Logging;

namespace BGPLite.Server;

/// <summary>
/// Creates a <see cref="BgpSession"/> for an accepted connection. The seam exists so
/// <see cref="BgpServer"/> stops carrying the session's dependencies (#263): the accept loop needs
/// none of the peer store, prefix service, aggregator or community resolver for itself — it only
/// forwarded them — and every one of them was optional, so a dropped DI registration turned into
/// wrong route sets at runtime instead of a startup failure.
/// </summary>
public interface IBgpSessionFactory
{
    /// <summary>Creates a session bound to <paramref name="connection"/> for <paramref name="peerConfig"/>.</summary>
    BgpSession Create(IBgpConnection connection, PeerConfig peerConfig);
}

/// <summary>
/// The production factory. Every dependency is required: resolving it is what makes an incomplete
/// composition fail at startup (<c>GetRequiredService</c> throws naming the missing service) rather
/// than at the first route send.
/// </summary>
public sealed class BgpSessionFactory : IBgpSessionFactory
{
    private readonly BgpConfig _bgpConfig;
    private readonly RouteTable _routeTable;
    private readonly IRouteFilter _routeFilter;
    private readonly BgpMetrics _metrics;
    private readonly ILogger<BgpSession> _sessionLogger;
    private readonly IPeerStore _peerStore;
    private readonly IPrefixAggregator _prefixAggregator;
    private readonly IRouteAssembler _routeAssembler;
    private readonly TimeProvider _timeProvider;

    /// <summary>Captures the composition every accepted connection's session is built from.</summary>
    public BgpSessionFactory(
        BgpConfig bgpConfig,
        RouteTable routeTable,
        IRouteFilter routeFilter,
        BgpMetrics metrics,
        ILogger<BgpSession> sessionLogger,
        IPeerStore peerStore,
        IPrefixAggregator prefixAggregator,
        IRouteAssembler routeAssembler,
        TimeProvider timeProvider)
    {
        _bgpConfig = bgpConfig;
        _routeTable = routeTable;
        _routeFilter = routeFilter;
        _metrics = metrics;
        _sessionLogger = sessionLogger;
        _peerStore = peerStore;
        _prefixAggregator = prefixAggregator;
        _routeAssembler = routeAssembler;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public BgpSession Create(IBgpConnection connection, PeerConfig peerConfig) =>
        new(connection, peerConfig, _bgpConfig, _routeTable, _routeFilter, _metrics, _sessionLogger,
            // The peer row is upserted as soon as OPEN identifies the peer, so a peer that has never
            // been configured in the UI still shows up there. Previously a lambda hand-wired in
            // Program.cs and threaded through BgpServer. Async since #262 — the upsert ran
            // synchronously on the OPEN-handshake path.
            onPeerIdentified: (ip, asn, ct) => _peerStore.UpsertPeerAsync(ip, asn, ct),
            peerStore: _peerStore,
            prefixAggregator: _prefixAggregator,
            routeAssembler: _routeAssembler,
            timeProvider: _timeProvider);
}
