using BGPLite.Configuration;

namespace BGPLite.Tests;

public class ConfigValidationTests
{
    // Factory helpers keep each test mutating exactly one field so the assertion isolates the rule
    // under test. Defaults match appsettings.Example.yml: a known-good baseline.
    private static BgpConfig Bgp(
        uint asn = 65001, string routerId = "10.0.0.1", int keepAlive = 60, int holdTime = 180,
        int openTimeoutSeconds = 30, int maxAcceptsPerIpPerMinute = 60, int maxPrefixesPerPeer = 0)
        => new()
        {
            Asn = asn,
            RouterId = routerId,
            KeepAlive = keepAlive,
            HoldTime = holdTime,
            OpenTimeoutSeconds = openTimeoutSeconds,
            MaxAcceptsPerIpPerMinute = maxAcceptsPerIpPerMinute,
            MaxPrefixesPerPeer = maxPrefixesPerPeer
        };

    private static AppConfig Config(BgpConfig? bgp = null, int apiPort = 5001, List<PeerConfig>? peers = null,
        List<PrefixSourceConfig>? sources = null, string? defaultSource = null)
        => new()
        {
            Bgp = bgp ?? Bgp(),
            ApiPort = apiPort,
            Peers = peers ?? [],
            PrefixSources = sources ?? [],
            DefaultPrefixSource = defaultSource
        };

    [Fact]
    public void Validate_AcceptsValidConfig()
    {
        var config = Config();

        var act = () => config.Validate();

        act();
    }

    // ---- PrefixSources (#327: fail loud at startup instead of a silent empty source at load) ----

    [Fact]
    public void Validate_AcceptsValidPrefixSources()
    {
        var config = Config(defaultSource: "nets", sources:
        [
            new PrefixSourceConfig { Name = "nets", Kind = "file", Path = "nets.txt" },
            new PrefixSourceConfig { Name = "ext", Kind = "http", Url = "https://example.net/list.txt", Community = "65000:100", Timeout = 30 },
            new PrefixSourceConfig { Name = "as65444", Kind = "asn", Asn = 65444 },
        ]);

        config.Validate();
    }

    [Fact]
    public void Validate_FileSourceWithoutPath_Throws()
    {
        var config = Config(sources: [new PrefixSourceConfig { Name = "nets", Kind = "file" }]);

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("Kind=file requires a Path", ex.Message);
    }

    [Fact]
    public void Validate_HttpSourceWithoutUrl_Throws()
    {
        var config = Config(sources: [new PrefixSourceConfig { Name = "ext", Kind = "http" }]);

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("Kind=http requires a Url", ex.Message);
    }

    [Theory]
    [InlineData("raw.githubusercontent.com/org/repo/main/ru.txt")] // no scheme
    [InlineData("ftp://example.net/list.txt")]                     // wrong scheme
    [InlineData("https://exa mple.net/list.txt")]                  // space — not a valid absolute URI
    public void Validate_HttpSourceWithNonHttpAbsoluteUrl_Throws(string url)
    {
        var config = Config(sources: [new PrefixSourceConfig { Name = "ext", Kind = "http", Url = url }]);

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("absolute http(s) URL", ex.Message);
    }

    [Fact]
    public void Validate_AsnSourceWithoutAsn_Throws()
    {
        var config = Config(sources: [new PrefixSourceConfig { Name = "as65444", Kind = "asn" }]);

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("Kind=asn requires an Asn", ex.Message);
    }

    [Fact]
    public void Validate_AsnSourceWithZeroAsn_Throws()
    {
        var config = Config(sources: [new PrefixSourceConfig { Name = "as0", Kind = "asn", Asn = 0 }]);

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("positive AS number", ex.Message);
    }

    [Theory]
    [InlineData("File")]
    [InlineData("HTTP")]
    public void Validate_SourceKindIsCaseSensitive_Throws(string kind)
    {
        var config = Config(sources: [new PrefixSourceConfig { Name = "x", Kind = kind, Path = "x.txt" }]);

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("unknown Kind", ex.Message);
    }

    [Fact]
    public void Validate_UnknownSourceKind_Throws()
    {
        var config = Config(sources: [new PrefixSourceConfig { Name = "x", Kind = "ftp", Path = "x.txt" }]);

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("unknown Kind 'ftp'", ex.Message);
    }

    [Fact]
    public void Validate_BadSourceCommunity_Throws()
    {
        // "65000:70000" is exactly the case #328 made a FormatException (VALUE masked before).
        var config = Config(sources:
        [
            new PrefixSourceConfig { Name = "ext", Kind = "http", Url = "https://example.net/l.txt", Community = "65000:70000" },
        ]);

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("Community", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void Validate_NonPositiveSourceTimeout_Throws()
    {
        var config = Config(sources:
        [
            new PrefixSourceConfig { Name = "ext", Kind = "http", Url = "https://example.net/l.txt", Timeout = 0 },
        ]);

        Assert.Contains("Timeout must be a positive",
            Assert.Throws<InvalidOperationException>(config.Validate).Message);

        var negative = Config(sources:
        [
            new PrefixSourceConfig { Name = "ext", Kind = "http", Url = "https://example.net/l.txt", Timeout = -1 },
        ]);

        Assert.Contains("Timeout must be a positive",
            Assert.Throws<InvalidOperationException>(negative.Validate).Message);
    }

    [Fact]
    public void Validate_EmptySourceName_Throws()
    {
        var config = Config(sources: [new PrefixSourceConfig { Name = "", Kind = "file", Path = "nets.txt" }]);

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("requires a Name", ex.Message);
    }

    [Fact]
    public void Validate_DuplicateSourceNames_Throws()
    {
        var config = Config(sources:
        [
            new PrefixSourceConfig { Name = "dup", Kind = "file", Path = "a.txt" },
            new PrefixSourceConfig { Name = "dup", Kind = "file", Path = "b.txt" },
        ]);

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("duplicate prefix source name 'dup'", ex.Message);
    }

    [Fact]
    public void Validate_DefaultPrefixSourceWithoutMatchingSource_Throws()
    {
        var config = Config(defaultSource: "ruu", sources:
        [
            new PrefixSourceConfig { Name = "ru", Kind = "file", Path = "nets.txt" },
        ]);

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("DefaultPrefixSource 'ruu'", ex.Message);
    }

    [Fact]
    public void Validate_CustomPrefixCommunity_Throws()
    {
        var config = new AppConfig { Bgp = Bgp(), CustomPrefixCommunity = "65000:70000" };

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("CustomPrefixCommunity", ex.Message);
    }

    [Fact]
    public void Validate_RipeStatAsnListCommunity_Throws()
    {
        var config = new AppConfig
        {
            Bgp = Bgp(),
            RipeStat = new RipeStatConfig
            {
                AsnLists = [new AsnList { Name = "ru", Country = "RU", Community = "65000:abc" }]
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("RipeStat.AsnLists[0]", ex.Message);
    }

    [Fact]
    public void Validate_ExplicitYamlNullCollections_AreTreatedAsEmpty()
    {
        // "PrefixSources:" / "AsnLists:" with no value deserialize as null collections — every
        // runtime consumer treats them as "none", and Validate must reject config with a message,
        // never with a NullReferenceException (#327 review).
        var config = ConfigLoader.LoadFromText(
            "Bgp:\n  Asn: 65001\n  RouterId: 10.0.0.1\nPrefixSources:\nRipeStat:\n  AsnLists:\n");

        config.Validate();
    }

    [Fact]
    public void Validate_NullSourceElement_ThrowsWithIndex()
    {
        // An empty YAML list item ("- ") deserializes as a null element — message, not NRE
        // (CodeRabbit review of #336).
        var config = Config(sources: [null!]);

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("PrefixSources[0] is empty", ex.Message);
    }

    [Fact]
    public void Validate_NullAsnListElement_ThrowsWithIndex()
    {
        var config = new AppConfig
        {
            Bgp = Bgp(),
            RipeStat = new RipeStatConfig { AsnLists = [null!] }
        };

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("RipeStat.AsnLists[0] is empty", ex.Message);
    }

    [Fact]
    public void Validate_AcceptsZeroHoldTime_KeepAliveSkipped()
    {
        // RFC 4271 §4.2: HoldTime=0 disables keepalive processing; KeepAlive is then irrelevant.
        var config = Config(Bgp(holdTime: 0, keepAlive: 0));

        var act = () => config.Validate();

        act();
    }

    [Fact]
    public void Validate_RejectsAsnZero()
    {
        var config = Config(Bgp(asn: 0));

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("Bgp.Asn", ex.Message);
    }

    [Theory]
    [InlineData("0.0.0.0")]   // RFC 4271 §6.8 forbids an all-zero BGP Identifier
    [InlineData("not-an-ip")]
    [InlineData("::1")]        // IPv6 must be rejected
    public void Validate_RejectsBadRouterId(string routerId)
    {
        var config = Config(Bgp(routerId: routerId));

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("Bgp.RouterId", ex.Message);
    }

    [Theory]
    [InlineData(2)]   // below the RFC 4271 §4.2 minimum of 3s
    [InlineData(1)]
    public void Validate_RejectsHoldTimeBelowThree(int holdTime)
    {
        var config = Config(Bgp(holdTime: holdTime, keepAlive: 1));

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("Bgp.HoldTime", ex.Message);
    }

    /// <summary>
    /// #265 item 2: Hold Time is a 2-octet OPEN field — a value above 65535 cannot be carried on
    /// the wire, and the write path used to truncate it silently ((ushort)70000 -> 4464).
    /// </summary>
    [Theory]
    [InlineData(65536)]
    [InlineData(70000)]
    public void Validate_RejectsHoldTimeAboveUshortRange(int holdTime)
    {
        var config = Config(Bgp(holdTime: holdTime, keepAlive: 60));

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("Bgp.HoldTime", ex.Message);
        Assert.Contains("65535", ex.Message);
    }

    /// <summary>#304: the per-peer prefix cap validates like the other 0=unlimited knobs.</summary>
    [Fact]
    public void Validate_RejectsNegativeMaxPrefixesPerPeer()
    {
        var config = Config(Bgp(maxPrefixesPerPeer: -1));

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("MaxPrefixesPerPeer", ex.Message);
    }

    [Fact]
    public void Validate_AcceptsHoldTimeAtUshortMax()
    {
        // The boundary itself is representable and must pass (KeepAlive within max(65535/3,1)).
        Config(Bgp(holdTime: 65535, keepAlive: 60)).Validate();
    }

    [Fact]
    public void Validate_RejectsKeepAliveAboveHoldTimeThird()
    {
        // HoldTime=3 → max keepalive = max(3/3, 1) = 1; KeepAlive=2 exceeds it.
        var config = Config(Bgp(holdTime: 3, keepAlive: 2));

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("Bgp.KeepAlive", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void Validate_RejectsBadApiPort(int apiPort)
    {
        var config = Config(apiPort: apiPort);

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("ApiPort", ex.Message);
    }

    [Theory]
    [InlineData(0)]            // nonsensical — rejects every body
    [InlineData(-1)]
    [InlineData(512)]          // below the 1 KiB floor — too small for a minimal peer payload
    [InlineData(64 * 1024 * 1024 + 1)]  // above the 64 MiB ceiling — weakens the DoS cap to nothing
    public void Validate_RejectsBadMaxRequestBodyBytes(long bytes)
    {
        var config = new AppConfig { Bgp = Bgp(), MaxRequestBodyBytes = bytes };

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("MaxRequestBodyBytes", ex.Message);
    }

    // --- #90: ApiListen — secure-by-default loopback bind ---

    [Fact]
    public void ApiListen_DefaultsToNull_Loopback()
    {
        // Default (unset) → null → ManagementApi binds to 127.0.0.1 (secure-by-default).
        var config = new AppConfig { Bgp = Bgp() };
        Assert.Null(config.ApiListen);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("localhost")]
    [InlineData("::1")]
    public void Validate_AcceptsAnyApiListen(string listen)
    {
        // ApiListen is a free-form bind address — any valid string is accepted (HttpListener will
        // fail at runtime if it can't bind). Validate does not restrict it; the secure-by-default
        // is the null → loopback mapping, not a validation constraint.
        var config = new AppConfig { Bgp = Bgp(), ApiListen = listen };
        config.Validate();
    }

    [Fact]
    public void Validate_AcceptsDefaultMaxRequestBodyBytes()
    {
        // The default (1 MiB) must pass validation — guards against an accidentally-too-tight range.
        var config = new AppConfig { Bgp = Bgp() };
        config.Validate();
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("::1")]
    public void Validate_RejectsBadPeerAddress(string address)
    {
        var config = Config(peers: [new PeerConfig { Address = address }]);

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("Peers[0].Address", ex.Message);
    }

    [Fact]
    public void Validate_AcceptsValidPeerAddress()
    {
        var config = Config(peers: [new PeerConfig { Address = "10.0.0.2", RemoteAsn = 65002 }]);

        var act = () => config.Validate();

        act();
    }

    [Fact]
    public void Validate_BgpConfigDirectly_AcceptsValid()
    {
        var bgp = Bgp();

        var act = () => bgp.Validate();

        act();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Validate_RejectsNegativeOpenTimeoutSeconds(int openTimeoutSeconds)
    {
        var config = Config(Bgp(openTimeoutSeconds: openTimeoutSeconds));

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("Bgp.OpenTimeoutSeconds", ex.Message);
    }

    [Fact]
    public void Validate_AcceptsZeroOpenTimeoutSeconds_Disabled()
    {
        // 0 = disabled (legacy behavior) — valid.
        var config = Config(Bgp(openTimeoutSeconds: 0));

        var act = () => config.Validate();

        act();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Validate_RejectsNegativeMaxAcceptsPerIpPerMinute(int maxPerMinute)
    {
        var config = Config(Bgp(maxAcceptsPerIpPerMinute: maxPerMinute));

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("Bgp.MaxAcceptsPerIpPerMinute", ex.Message);
    }

    [Fact]
    public void Validate_AcceptsZeroMaxAcceptsPerIpPerMinute_Disabled()
    {
        // 0 = disabled (legacy behavior) — valid.
        var config = Config(Bgp(maxAcceptsPerIpPerMinute: 0));

        var act = () => config.Validate();

        act();
    }
}
