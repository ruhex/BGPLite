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

    // --- UserSource (#143 / #147) — auto-gen from a reserved 5XX range, override via Community ---

    [Fact]
    public void Resolve_UserSource_ExplicitCommunity_Wins()
    {
        var r = new ConfigCommunityResolver(new AppConfig(), new BgpConfig { Asn = 65444 }, null);
        Assert.Equal([CommunityCodec.Parse("65000:42")],
            r.Resolve(new CommunitySource(CommunitySourceKind.UserSource, "my-list", "65000:42")));
    }

    [Fact]
    public void Resolve_UserSource_ExplicitInvalid_FallsBackToAutoGen()
    {
        // Mirrors Custom/CustomAsn: an invalid explicit override logs and uses the auto-gen default
        // (NOT empty) so a typo doesn't silently untag the source's prefixes.
        var r = new ConfigCommunityResolver(new AppConfig(), new BgpConfig { Asn = 65444 }, null);
        var auto = r.Resolve(new CommunitySource(CommunitySourceKind.UserSource, "my-list"));
        var fallback = r.Resolve(new CommunitySource(CommunitySourceKind.UserSource, "my-list", "not-a-community"));
        Assert.Equal(auto, fallback);
        Assert.NotEmpty(fallback);
    }

    [Fact]
    public void Resolve_UserSource_AutoGen_DeterministicAcrossInstances()
    {
        // Auto-gen must be stable across resolver instances (and thus across restarts) —
        // string.GetHashCode() is randomized per-process and would fail this on the next launch.
        var r1 = new ConfigCommunityResolver(new AppConfig(), new BgpConfig { Asn = 65444 }, null);
        var r2 = new ConfigCommunityResolver(new AppConfig(), new BgpConfig { Asn = 65444 }, null);
        Assert.Equal(
            r1.Resolve(new CommunitySource(CommunitySourceKind.UserSource, "list-a")),
            r2.Resolve(new CommunitySource(CommunitySourceKind.UserSource, "list-a")));
    }

    [Fact]
    public void Resolve_UserSource_AutoGen_KnownExpected()
    {
        // Locks the formula: <LocalAsn>:(500 + Fnv1a(Name) % 100), value range 500-599. Inlining
        // FNV-1a means any future accidental switch to string.GetHashCode() fails this test.
        static uint Fnv1a(string s)
        {
            unchecked
            {
                uint hash = 2166136261u;
                foreach (var b in System.Text.Encoding.UTF8.GetBytes(s))
                {
                    hash ^= b;
                    hash *= 16777619u;
                }
                return hash;
            }
        }
        var expected = (65444u << 16) | (500u + Fnv1a("my-list") % 100);
        var r = new ConfigCommunityResolver(new AppConfig(), new BgpConfig { Asn = 65444 }, null);
        Assert.Equal([expected], r.Resolve(new CommunitySource(CommunitySourceKind.UserSource, "my-list")));
    }

    [Fact]
    public void Resolve_UserSource_FourByteAsn_AutoGen_Empty()
    {
        var r = new ConfigCommunityResolver(new AppConfig(), new BgpConfig { Asn = 200000 }, null);
        Assert.Empty(r.Resolve(new CommunitySource(CommunitySourceKind.UserSource, "list-a")));
    }

    [Fact]
    public void Resolve_UserSource_FourByteAsn_ExplicitCommunity_StillResolves()
    {
        // An explicit community parses even with a 4-byte local ASN — the asn>0xFFFF guard only
        // blocks auto-generation, mirroring Custom/CustomAsn override behavior.
        var r = new ConfigCommunityResolver(new AppConfig(), new BgpConfig { Asn = 200000 }, null);
        Assert.Equal([CommunityCodec.Parse("65000:42")],
            r.Resolve(new CommunitySource(CommunitySourceKind.UserSource, "list-a", "65000:42")));
    }

    /// <summary>
    /// Regression for #159: ConfigCommunityResolver is a DI singleton shared by every BgpSession,
    /// and Resolve() runs on every SendAllRoutesAsync. Under ≥2 concurrently-establishing peers,
    /// the cache must be thread-safe — a plain Dictionary.TryAdd/indexer-set races and can corrupt
    /// the bucket chain (IndexOutOfRange / NullReferenceException / torn reads). ConcurrentDictionary
    /// + GetOrAdd makes it safe.
    /// </summary>
    [Fact]
    public async Task Resolve_ConcurrentCallsAcrossManySessions_DoNotCorrupt()
    {
        // Many distinct communities + many concurrent callers — a stress that would trip a plain
        // Dictionary within milliseconds. The run completing without exception is the assertion.
        var cfg = ConfigWith(asnLists: Enumerable.Range(0, 32)
            .Select(i => new AsnList { Name = $"list-{i}", Asns = [], Community = $"65000:{100 + i}" }));
        var resolver = Resolver(cfg);

        var sources = Enumerable.Range(0, 32)
            .Select(i => new CommunitySource(CommunitySourceKind.AsnList, $"list-{i}"))
            .ToArray();

        // 200 concurrent Resolve calls across the 32 distinct communities, repeatedly, from
        // multiple parallel tasks. If the cache were a plain Dictionary, this would throw or hang.
        var tasks = Enumerable.Range(0, 200).Select(i =>
            Task.Run(() => resolver.Resolve(sources[i % sources.Length])));
        var results = await Task.WhenAll(tasks);

        // Every result must be the expected single community — no torn reads returned a wrong value.
        foreach (var (result, i) in results.Select((r, i) => (r, i)))
        {
            var expected = sources[i % sources.Length];
            var expectedComm = CommunityCodec.Parse($"65000:{100 + (i % sources.Length)}");
            Assert.Single(result);
            Assert.Equal(expectedComm, result[0]);
        }
    }
}
