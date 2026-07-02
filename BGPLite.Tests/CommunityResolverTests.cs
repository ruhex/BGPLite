using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Xunit;

namespace BGPLite.Tests;

public class CommunityResolverTests
{
    private static ConfigCommunityResolver Resolver(AppConfig config) =>
        new(config, new BgpConfig(), logger: null);

    private static AppConfig ConfigWith(
        IEnumerable<AsnList>? asnLists = null,
        IEnumerable<PrefixSourceConfig>? sources = null,
        string? defaultSource = null) => new()
        {
            RipeStat = asnLists is null ? null : new RipeStatConfig { AsnLists = asnLists.ToList() },
            PrefixSources = (sources ?? []).ToList(),
            DefaultPrefixSource = defaultSource
        };

    [Fact]
    public void Resolve_AsnList_ReturnsParsedCommunity()
    {
        var cfg = ConfigWith(asnLists: [new AsnList { Name = "cloudflare", Community = "65000:100" }]);
        var comms = Resolver(cfg).Resolve(new CommunitySource(CommunitySourceKind.AsnList, "cloudflare"));
        Assert.Equal([CommunityCodec.Parse("65000:100")], comms);
    }

    [Fact]
    public void Resolve_AsnList_MissingCommunity_ReturnsEmpty()
    {
        var cfg = ConfigWith(asnLists: [new AsnList { Name = "cloudflare" }]);
        Assert.Empty(Resolver(cfg).Resolve(new CommunitySource(CommunitySourceKind.AsnList, "cloudflare")));
    }

    [Fact]
    public void Resolve_AsnList_UnknownName_ReturnsEmpty()
    {
        var cfg = ConfigWith(asnLists: [new AsnList { Name = "cloudflare", Community = "65000:100" }]);
        Assert.Empty(Resolver(cfg).Resolve(new CommunitySource(CommunitySourceKind.AsnList, "nope")));
    }

    [Fact]
    public void Resolve_PrefixSource_ReturnsParsedCommunity()
    {
        var cfg = ConfigWith(sources: [new PrefixSourceConfig { Name = "aws", Community = "65000:200" }]);
        var comms = Resolver(cfg).Resolve(new CommunitySource(CommunitySourceKind.PrefixSource, "aws"));
        Assert.Equal([CommunityCodec.Parse("65000:200")], comms);
    }

    [Fact]
    public void Resolve_Country_UsesAsnListCommunity()
    {
        var cfg = ConfigWith(asnLists: [new AsnList { Name = "ru", Country = "RU", Community = "65000:300" }]);
        var comms = Resolver(cfg).Resolve(new CommunitySource(CommunitySourceKind.Country, "ru"));
        Assert.Equal([CommunityCodec.Parse("65000:300")], comms);
    }

    [Fact]
    public void Resolve_PrefixSource_FallsBackToDefaultSource_WhenListNameNull()
    {
        var cfg = ConfigWith(
            sources: [new PrefixSourceConfig { Name = "ru", Community = "65000:1" }],
            defaultSource: "ru");
        var comms = Resolver(cfg).Resolve(new CommunitySource(CommunitySourceKind.PrefixSource, null));
        Assert.Equal([CommunityCodec.Parse("65000:1")], comms);
    }

    [Fact]
    public void Resolve_Default_ReturnsEmpty()
    {
        var r = Resolver(ConfigWith());
        Assert.Empty(r.Resolve(new CommunitySource(CommunitySourceKind.Default)));
    }

    [Fact]
    public void Resolve_InvalidCommunity_ReturnsEmpty_NoThrow()
    {
        var cfg = ConfigWith(asnLists: [new AsnList { Name = "bad", Community = "not-a-community" }]);
        Assert.Empty(Resolver(cfg).Resolve(new CommunitySource(CommunitySourceKind.AsnList, "bad")));
    }

    [Fact]
    public void Resolve_CachesParsedValue_SameInstance()
    {
        var cfg = ConfigWith(asnLists: [new AsnList { Name = "x", Community = "65000:42" }]);
        var r = Resolver(cfg);
        var a = r.Resolve(new CommunitySource(CommunitySourceKind.AsnList, "x"));
        var b = r.Resolve(new CommunitySource(CommunitySourceKind.AsnList, "x"));
        Assert.Same(a, b);
    }

    [Fact]
    public void MakeRoute_StampsCommunitiesAndAsPath()
    {
        var route = BgpSession.MakeRoute(0xC0A80000, 24, 0x01020304, [65000u], [CommunityCodec.Parse("65000:100")]);
        Assert.Equal(0xC0A80000u, route.Prefix);
        Assert.Equal((byte)24, route.PrefixLength);
        Assert.Equal(0x01020304u, route.NextHop);
        Assert.Equal([65000u], route.AsPath);
        Assert.Equal([CommunityCodec.Parse("65000:100")], route.Communities);
    }

    [Fact]
    public void MakeRoute_NullAsPath_DefaultsEmpty()
    {
        var route = BgpSession.MakeRoute(0x0A000000, 8, 0, null, []);
        Assert.Empty(route.AsPath);
        Assert.Empty(route.Communities);
    }

    [Fact]
    public void Resolve_Custom_DefaultsToAsn100()
    {
        var r = new ConfigCommunityResolver(new AppConfig(), new BgpConfig { Asn = 65444 }, null);
        Assert.Equal([CommunityCodec.Parse("65444:100")], r.Resolve(new CommunitySource(CommunitySourceKind.Custom)));
    }

    [Fact]
    public void Resolve_CustomAsn_DefaultsToAsn200()
    {
        var r = new ConfigCommunityResolver(new AppConfig(), new BgpConfig { Asn = 65444 }, null);
        Assert.Equal([CommunityCodec.Parse("65444:200")], r.Resolve(new CommunitySource(CommunitySourceKind.CustomAsn)));
    }

    [Fact]
    public void Resolve_Custom_ConfigOverrideWins()
    {
        var cfg = new AppConfig { CustomPrefixCommunity = "65000:999" };
        var r = new ConfigCommunityResolver(cfg, new BgpConfig { Asn = 65444 }, null);
        Assert.Equal([CommunityCodec.Parse("65000:999")], r.Resolve(new CommunitySource(CommunitySourceKind.Custom)));
    }

    [Fact]
    public void Resolve_CustomAsn_ConfigOverrideWins()
    {
        var cfg = new AppConfig { CustomAsnCommunity = "65000:888" };
        var r = new ConfigCommunityResolver(cfg, new BgpConfig { Asn = 65444 }, null);
        Assert.Equal([CommunityCodec.Parse("65000:888")], r.Resolve(new CommunitySource(CommunitySourceKind.CustomAsn)));
    }

    [Fact]
    public void Resolve_Custom_InvalidOverride_FallsBackToDefault()
    {
        var cfg = new AppConfig { CustomPrefixCommunity = "not-a-community" };
        var r = new ConfigCommunityResolver(cfg, new BgpConfig { Asn = 65444 }, null);
        Assert.Equal([CommunityCodec.Parse("65444:100")], r.Resolve(new CommunitySource(CommunitySourceKind.Custom)));
    }

    [Fact]
    public void Resolve_Custom_FourByteAsn_DefaultUnformed_Empty()
    {
        // ASN > 65535 cannot form an "ASN:VALUE" community → untagged.
        var r = new ConfigCommunityResolver(new AppConfig(), new BgpConfig { Asn = 200000 }, null);
        Assert.Empty(r.Resolve(new CommunitySource(CommunitySourceKind.Custom)));
    }
}
