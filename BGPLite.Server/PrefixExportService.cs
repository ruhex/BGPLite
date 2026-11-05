using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Protocol;
using BGPLite.Routing;

namespace BGPLite.Server;

/// <summary>Uses the production assembler and aggregator for a canonical, explicitly degraded export.</summary>
public sealed class PrefixExportService(IRouteAssembler routeAssembler, IPrefixAggregator prefixAggregator) : IPrefixExportService
{
    public async Task<PrefixExportResult> ExportAsync(string peerIp, uint remoteAsn, CancellationToken ct = default)
    {
        var peer = new PeerConfig { Address = peerIp, RemoteAsn = remoteAsn };
        var result = await routeAssembler.BuildOutboundRouteResultAsync(peerIp, remoteAsn, peer, peerIp, ct);
        if (!result.Complete)
            return new PrefixExportResult([], Complete: false);

        var prefixes = prefixAggregator.Aggregate(result.Routes)
            .Select(route => new IpPrefix(route.Prefix, route.PrefixLength, route.IsIpv4).ToString())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(prefix => prefix, StringComparer.Ordinal)
            .ToArray();
        return new PrefixExportResult(prefixes, Complete: true);
    }
}
