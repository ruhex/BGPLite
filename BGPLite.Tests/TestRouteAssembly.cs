using BGPLite.Api;
using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

internal static class TestRouteAssembly
{
    public static IPrefixExportService CreateExporter(PeerStore store, AppConfig config, IPrefixService prefixes) =>
        new PrefixExportService(
            new RouteAssembler(prefixes, store, new ConfigCommunityResolver(config, config.Bgp),
                AllowAllFilter.Instance, config, config.Bgp, NullLogger<RouteAssembler>.Instance),
            new ExactUnionPrefixAggregator());
}
