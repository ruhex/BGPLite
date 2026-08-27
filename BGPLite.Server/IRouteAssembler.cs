using BGPLite.Configuration;
using BGPLite.Routing;

namespace BGPLite.Server;

/// <summary>
/// Resolves the outbound route set for one peer — "which prefixes does this peer get" — without
/// touching the transport. The caller (<see cref="BgpSession"/>) aggregates, batches and sends.
/// <para>
/// The seam exists so <see cref="BgpSession"/> stops constructing its own assembler (#263). The
/// production implementation (<c>RouteAssembler</c>) requires the peer store, prefix service and
/// <c>AppConfig</c> as non-nullable dependencies, so a composition that cannot serve per-peer
/// configuration no longer type-checks; the degraded shared-table behavior is a separate, named
/// implementation (<see cref="SharedTableRouteAssembler"/>) that a caller has to choose on purpose.
/// </para>
/// </summary>
public interface IRouteAssembler
{
    /// <summary>
    /// Resolves the routes to advertise to <paramref name="peerIp"/>/<paramref name="remoteAsn"/>,
    /// already passed through the outgoing community filter for <paramref name="filterPeerConfig"/>.
    /// </summary>
    /// <param name="peerIp">Peer IP, the durable store identity together with <paramref name="remoteAsn"/>.</param>
    /// <param name="remoteAsn">Peer ASN as negotiated in OPEN.</param>
    /// <param name="filterPeerConfig">Peer config the outgoing community filter is resolved against.</param>
    /// <param name="peerLabel">Display form of the peer (<c>ip:port</c>), used only for logging.</param>
    /// <param name="ct">Cancels the fetches the assembly performs.</param>
    Task<List<Route>> BuildOutboundRoutesAsync(
        string peerIp, uint remoteAsn, PeerConfig filterPeerConfig, string peerLabel, CancellationToken ct);
}
