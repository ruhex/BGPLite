using System.Net;
using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// #220 "suppress more-specifics": a custom prefix is the operator's explicit override, so source
/// routes it covers (strictly more specific) are dropped from the outbound list; source prefixes
/// NOT covered are sent normally, and an exact custom==source duplicate stays for the #209
/// community-union merge. Suppression runs in the assembler, before the per-community-set
/// aggregator — so it covers every trigger (initial dump, ROUTE_REFRESH, API-triggered refresh),
/// which all rebuild the list through <see cref="RouteAssembler.BuildOutboundRoutesAsync"/>.
/// </summary>
public class RouteAssemblerSuppressionTests
{
    private static uint Net(string ip) => BgpConstants.IPAddressToUint(IPAddress.Parse(ip));

    private static Route R(string ip, byte length) => new()
    {
        Prefix = Net(ip),
        PrefixLength = length,
        NextHop = Net("10.0.0.1"),
        Communities = [65001u << 16 | 770]
    };

    // --- pure helper -----------------------------------------------------------------------------

    [Fact]
    public void Custom24_Covers_Source25_SourceSuppressed()
    {
        var customs = new List<(uint, byte)> { (Net("1.2.3.0"), 24) };
        var routes = new List<Route> { R("1.2.3.0", 24), R("1.2.3.0", 25) };

        var result = RouteAssembler.SuppressCoveredByCustomPrefixes(routes, customs);

        // RED pre-fix: both were sent — the /16-style override did not suppress the covered /25.
        Assert.Equal([(Net("1.2.3.0"), (byte)24)], result.Select(r => (r.Prefix, r.PrefixLength)));
    }

    [Fact]
    public void Custom8_Covers_DeeperSourcePrefixes()
    {
        var customs = new List<(uint, byte)> { (Net("10.0.0.0"), 8) };
        var routes = new List<Route> { R("10.1.0.0", 16), R("10.2.3.0", 24), R("10.0.0.0", 8) };

        var result = RouteAssembler.SuppressCoveredByCustomPrefixes(routes, customs);

        Assert.Equal([(Net("10.0.0.0"), (byte)8)], result.Select(r => (r.Prefix, r.PrefixLength)));
    }

    [Fact]
    public void NonOverlappingPrefixes_AllKept()
    {
        var customs = new List<(uint, byte)> { (Net("1.2.3.0"), 24) };
        var routes = new List<Route> { R("1.2.4.0", 24), R("1.2.3.0", 23) /* shorter, not covered */ };

        var result = RouteAssembler.SuppressCoveredByCustomPrefixes(routes, customs);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void NoCustomPrefixes_UnchangedBehavior()
    {
        var routes = new List<Route> { R("1.2.3.0", 24), R("10.0.0.0", 8) };

        var result = RouteAssembler.SuppressCoveredByCustomPrefixes(routes, []);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ExactCustomEqualsSource_NotSuppressed_LeftToUnionMerge()
    {
        // The exact match is handled by BgpSession.MergeDuplicatePrefixes (#209), which unions the
        // custom and source communities — suppressing it here would drop the source's tags.
        var customs = new List<(uint, byte)> { (Net("1.2.3.0"), 24) };
        var routes = new List<Route> { R("1.2.3.0", 24) };

        var result = RouteAssembler.SuppressCoveredByCustomPrefixes(routes, customs);

        Assert.Single(result);
    }

    [Fact]
    public void NestedCustomPrefixes_DoNotSuppressEachOther()
    {
        // CodeRabbit (integration review): both are CONFIGURED custom prefixes — the operator's
        // deliberate /16 must survive its own broader /8. Only source routes are suppressed.
        var customs = new List<(uint, byte)> { (Net("10.0.0.0"), 8), (Net("10.1.0.0"), 16) };
        var routes = new List<Route>
        {
            R("10.0.0.0", 8), R("10.1.0.0", 16),          // the two configured customs
            R("10.2.0.0", 16), R("10.1.1.0", 24)          // source routes covered by them
        };

        var result = RouteAssembler.SuppressCoveredByCustomPrefixes(routes, customs);

        Assert.Equal([(Net("10.0.0.0"), (byte)8), (Net("10.1.0.0"), (byte)16)],
            result.Select(r => (r.Prefix, r.PrefixLength)));
    }

    [Fact]
    public void ZeroLengthCustom_Covers_Everything()
    {
        var customs = new List<(uint, byte)> { (Net("0.0.0.0"), 0) };
        var routes = new List<Route> { R("1.2.3.0", 24), R("203.0.113.0", 24) };

        var result = RouteAssembler.SuppressCoveredByCustomPrefixes(routes, customs);

        Assert.Empty(result);
    }

    // --- end-to-end through BuildOutboundRoutesAsync ----------------------------------------------

    [Fact]
    public async Task BuildOutboundRoutes_CustomPrefix_SuppressesCoveredSourcePrefixes()
    {
        // The issue's scenario: a custom 91.108.0.0/16 overrides a YouTube-like source whose /22s
        // (arriving here through the custom-ASN path) sit inside it.
        var config = new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } };
        var assembler = new RouteAssembler(
            new StubPrefixService(("91.108.4.0", 22), ("91.108.8.0", 22)),
            new ConfiguredPeerStore(["91.108.0.0/16"], [64512]),
            new ConfigCommunityResolver(config, config.Bgp),
            AllowAllFilter.Instance,
            config,
            config.Bgp,
            NullLogger<RouteAssembler>.Instance);

        var routes = await assembler.BuildOutboundRoutesAsync(
            "203.0.113.9", 65002, new PeerConfig { Address = "203.0.113.9" }, "203.0.113.9",
            CancellationToken.None);

        Assert.Equal([(Net("91.108.0.0"), (byte)16)], routes.Select(r => ((uint)r.Prefix, (byte)r.PrefixLength)));
    }

    [Fact]
    public async Task BuildOutboundRoutes_NestedCustomPrefixes_BothAdvertised_SourceSuppressed()
    {
        var config = new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } };
        var assembler = new RouteAssembler(
            new StubPrefixService(("10.2.3.0", 24)),
            new ConfiguredPeerStore(["10.0.0.0/8", "10.1.0.0/16"], [64512]),
            new ConfigCommunityResolver(config, config.Bgp),
            AllowAllFilter.Instance,
            config,
            config.Bgp,
            NullLogger<RouteAssembler>.Instance);

        var routes = await assembler.BuildOutboundRoutesAsync(
            "203.0.113.9", 65002, new PeerConfig { Address = "203.0.113.9" }, "203.0.113.9",
            CancellationToken.None);

        // The /24 (from the custom-ASN fetch) is suppressed as a source route; BOTH configured
        // customs — including the nested /16 — are advertised.
        Assert.Equal(
            [(Net("10.0.0.0"), (byte)8), (Net("10.1.0.0"), (byte)16)],
            routes.Select(r => ((uint)r.Prefix, (byte)r.PrefixLength)));
    }

    /// <summary>Serves the configured prefixes for the custom-ASN fetch; nothing else.</summary>
    private sealed class StubPrefixService(params (string Ip, byte Length)[] prefixes) : IPrefixService
    {
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetPrefixesAsync(uint asn, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>>([]);
        public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default)
            => Task.FromResult(prefixes.Select(p => ((UInt128)Net(p.Ip), p.Length, true, 64512u)).ToList());
        public Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default) => Task.FromResult(0);
        public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default)
            => Task.FromResult(new List<(UInt128, byte, bool, uint)>());
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>>([]);
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetUserSourcePrefixesAsync(string name, string url, string? community, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>>([]);
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Resolves to a configured peer carrying one custom prefix + one custom ASN.</summary>
    private sealed class ConfiguredPeerStore(List<string> customPrefixes, List<uint> customAsns) : IPeerStore
    {
        public Task<string> CreatePeerAsync(string ip, uint asn, string? description, CancellationToken ct = default) => Task.FromResult("id");
        public Task UpsertPeerAsync(string ip, uint asn, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateSessionStatusAsync(string ip, uint asn, bool active, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int?> GetPeerMaxPrefixAsync(string ip, uint asn, CancellationToken ct = default) => Task.FromResult<int?>(null);
        public Task<PeerRoutingView?> LoadPeerRoutingViewAsync(string ip, uint asn, CancellationToken ct = default)
            => Task.FromResult<PeerRoutingView?>(new("id", [], customPrefixes, customAsns, []));
    }
}
