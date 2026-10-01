using System.Collections;
using System.Reflection;
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

    // --- resilience/auto-refresh tunables fail loud --------------------------------------

    [Theory]
    [InlineData("TimeoutSeconds", -1)]
    [InlineData("RetryAttempts", -1)]
    [InlineData("RetryDelaySeconds", -5)]
    public void Validate_RejectsNegativeRipeStatTunables(string field, int value)
    {
        var ripe = new RipeStatConfig();
        typeof(RipeStatConfig).GetProperty(field)!.SetValue(ripe, value);
        var config = new AppConfig { Bgp = Bgp(), RipeStat = ripe };

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains($"RipeStat.{field}", ex.Message);
    }

    [Theory]
    [InlineData("IntervalSeconds", 0)]
    [InlineData("NoEtagIntervalSeconds", 0)]
    [InlineData("MaxJitterMs", -1)]
    public void Validate_RejectsBadAutoRefreshTunables(string field, int value)
    {
        var auto = new AutoRefreshConfig();
        typeof(AutoRefreshConfig).GetProperty(field)!.SetValue(auto, value);
        var config = new AppConfig { Bgp = Bgp(), AutoRefresh = auto };

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains($"AutoRefresh.{field}", ex.Message);
    }

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

    // ---- PrefixSources (fail loud at startup instead of a silent empty source at load) ----

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
        // "65000:70000" is the case the codec used to mask silently (VALUE half) — now it must
        // surface as a FormatException.
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
        // never with a NullReferenceException.
        var config = ConfigLoader.LoadFromText(
            "Bgp:\n  Asn: 65001\n  RouterId: 10.0.0.1\nPrefixSources:\nRipeStat:\n  AsnLists:\n");

        config.Validate();
    }

    [Fact]
    public void Validate_NullSourceElement_ThrowsWithIndex()
    {
        // An empty YAML list item ("- ") deserializes as a null element — message, not NRE.
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

    // ---- structural choke point: null list elements & AS 0 — one rule, every surface --------

    [Fact]
    public void Validate_NullPeerElement_ThrowsWithIndex()
    {
        // The Peers loop had no element guard (its siblings did) — a bare NRE instead of a message.
        var config = Config(peers: [null!]);

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("Peers[0] is empty", ex.Message);
    }

    [Fact]
    public void Validate_YamlNullPeersCollection_IsTreatedAsEmpty()
    {
        // "Peers:" with no value deserializes as a null collection — same contract as
        // PrefixSources/AsnLists: it means "none", and Validate must pass, not NRE at Peers.Count.
        var config = ConfigLoader.LoadFromText("Bgp:\n  Asn: 65001\n  RouterId: 10.0.0.1\nPeers:\n");

        config.Validate();
    }

    [Fact]
    public void Validate_YamlNullPeerElement_ThrowsWithIndexNotNre()
    {
        // An empty YAML list item ("- ") deserializes as a null element: a message naming the
        // index, not the bare NullReferenceException the Peers loop used to throw.
        var config = ConfigLoader.LoadFromText("Bgp:\n  Asn: 65001\n  RouterId: 10.0.0.1\nPeers:\n  -\n");

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("Peers[0] is empty", ex.Message);
    }

    [Fact]
    public void Validate_YamlNullAsnsCollection_ThrowsWithPathNotNre()
    {
        // Explicit YAML null ("Asns:" with no value) deserializes to a null collection. The
        // structural walker correctly skips it (null = none), but every runtime consumer
        // dereferences Asns unconditionally — validation must reject it with the full path
        // instead of letting the first route build NRE later.
        var config = ConfigLoader.LoadFromText(
            "Bgp:\n  Asn: 65001\n  RouterId: 10.0.0.1\nRipeStat:\n  AsnLists:\n    - Name: ru\n      Asns:\n");

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("RipeStat.AsnLists[0].Asns is null", ex.Message);
    }

    [Fact]
    public void Validate_AsnListWithZeroAsn_Throws()
    {
        // AsnLists.Asns was the last config surface that accepted AS 0 — the value went
        // straight to ris-prefixes?resource=AS0 and back out as re-originated NLRI.
        var config = new AppConfig
        {
            Bgp = Bgp(),
            RipeStat = new RipeStatConfig { AsnLists = [new AsnList { Name = "bad", Asns = [0] }] }
        };

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("RipeStat.AsnLists[0].Asns[0]", ex.Message);
        Assert.Contains("positive AS number", ex.Message);
    }

    [Fact]
    public void Validate_PeerRemoteAsnZero_Throws()
    {
        // A configured peer with RemoteAsn 0 can never match an OPEN (RFC 7607 rejects AS 0
        // there), so the row would be dead weight from startup — same rule as Bgp.Asn.
        var config = Config(peers: [new PeerConfig { Address = "10.0.0.2", RemoteAsn = 0 }]);

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("Peers[0].RemoteAsn", ex.Message);
        Assert.Contains("positive AS number", ex.Message);
    }

    [Fact]
    public void Validate_NullElement_InEveryReferenceTypeListProperty_ThrowsWithPathNotNre()
    {
        // Choke-point acceptance: discovery is reflection-driven, so a NEW collection property
        // added to any config type is covered by this test and by AppConfig.ValidateListElements
        // alike — the invariant no longer depends on remembering a per-surface guard.
        var paths = DiscoverReferenceListPaths(typeof(AppConfig));
        Assert.Contains("Peers", paths);
        Assert.Contains("PrefixSources", paths);
        Assert.Contains("RipeStat.AsnLists", paths);
        Assert.Contains("TrustedProxies", paths);

        foreach (var path in paths)
        {
            var config = BuildConfigWithNullListElement(path.Split('.'));
            var thrown = Record.Exception(config.Validate);
            Assert.True(thrown?.GetType() == typeof(InvalidOperationException),
                $"{path}: expected InvalidOperationException with a message, got " +
                (thrown is null ? "no exception" : thrown.GetType().FullName));
            Assert.Contains(path + "[0]", thrown!.Message);
        }
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
    /// Hold Time is a 2-octet OPEN field — a value above 65535 cannot be carried on
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

    /// <summary>The per-peer prefix cap validates like the other 0=unlimited knobs.</summary>
    [Fact]
    public void ShippedMaxPrefixesPerPeerDefault_IsBounded()
    {
        // The RFC 4486 defense must be on out of the box — the shipped default is a
        // generous bound (1M prefixes, above any legitimate provisioning peer, far below memory
        // exhaustion), not unlimited; 0 remains available as an explicit opt-out.
        Assert.Equal(1_000_000, new BgpConfig().MaxPrefixesPerPeer);
    }

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

    // --- ApiListen — secure-by-default loopback bind ---

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
    [InlineData("0.0.0.0")]   // the all-zeros placeholder is never a valid peer address
    [InlineData("not-an-ip")]
    [InlineData("::1")]
    public void Validate_RejectsBadPeerAddress(string address)
    {
        var config = Config(peers: [new PeerConfig { Address = address }]);

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("Peers[0].Address", ex.Message);
    }

    [Fact]
    public void Validate_RequiresPeerAddress()
    {
        // PeerConfig.Address now defaults to "" so an omitted Address trips validation
        // instead of silently configuring the all-zeros placeholder.
        var config = Config(peers: [new PeerConfig { RemoteAsn = 65002 }]);

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("Peers[0].Address is required", ex.Message);
    }

    [Fact]
    public void Validate_RequiresPeerRemoteAsn()
    {
        // A configured peer without a remote ASN can never match an OPEN — fail loud.
        var config = Config(peers: [new PeerConfig { Address = "10.0.0.2" }]);

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("Peers[0].RemoteAsn is required", ex.Message);
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

    // Reflection helpers for the choke-point acceptance test: discover every list property whose
    // elements CAN be null (reference types), then plant a null at exactly that path.
    private static List<string> DiscoverReferenceListPaths(Type type, string prefix = "")
    {
        var found = new List<string>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0)
                continue;
            var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
            var elementType = FindEnumerableElementType(property.PropertyType);
            // Value-type elements (List<uint>) can never hold null — nothing to guard.
            if (elementType is not null && !elementType.IsValueType)
                found.Add(path);

            // Recurse into nested config records — plain objects and list elements alike.
            var recurseInto = property.PropertyType.Namespace == typeof(AppConfig).Namespace
                ? property.PropertyType
                : elementType is not null && elementType.Namespace == typeof(AppConfig).Namespace
                    ? elementType
                    : null;
            if (recurseInto is not null)
                found.AddRange(DiscoverReferenceListPaths(recurseInto, path));
        }
        return found.Distinct().ToList();
    }

    private static Type? FindEnumerableElementType(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            return type.GetGenericArguments()[0];
        return type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    private static AppConfig BuildConfigWithNullListElement(string[] segments)
    {
        var root = new AppConfig();
        object node = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var property = node.GetType().GetProperty(segments[i])!;
            // A null intermediate (e.g. RipeStat:) is instantiated so the leaf can be planted;
            // init-only setters accept reflection writes.
            var child = property.GetValue(node) ?? Activator.CreateInstance(property.PropertyType)!;
            property.SetValue(node, child);
            node = child;
        }
        var leaf = node.GetType().GetProperty(segments[^1])!;
        var list = leaf.GetValue(node) ?? Activator.CreateInstance(leaf.PropertyType)!;
        leaf.SetValue(node, list);
        ((IList)list).Add(null!);
        return root;
    }

    // ---------------- ApiRateLimit / TrustedProxies / CorsAllowedOrigins (#536) ----------------
    // These three sections had no semantic validation: the runtime clamped them silently, so a
    // typo produced different behaviour with a clean startup. Worst case — a negative
    // PeriodSeconds clamped to 1 s turns 120 tokens/60 s into 120 requests/SECOND, disabling the
    // flood protection the section exists to configure.

    [Theory]
    [InlineData(-5, 120, 60, 0, "TokenLimit")]
    [InlineData(120, 0, 60, 0, "TokensPerPeriod")]
    [InlineData(120, 120, 0, 0, "PeriodSeconds")]
    [InlineData(120, 120, 60, -1, "MaxConcurrentRequests")]
    public void Validate_RejectsOutOfRangeApiRateLimit(int tokenLimit, int perPeriod, int period, int maxConcurrent, string expectedField)
    {
        var config = new AppConfig
        {
            Bgp = Bgp(),
            ApiRateLimit = new ApiRateLimitConfig
            {
                Enabled = true,
                TokenLimit = tokenLimit,
                TokensPerPeriod = perPeriod,
                PeriodSeconds = period,
                MaxConcurrentRequests = maxConcurrent
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("ApiRateLimit." + expectedField, ex.Message);
    }

    [Fact]
    public void Validate_ApiRateLimitDisabled_StillValidates()
    {
        // Enabled = false is a valid configuration; the RANGES are still wrong, and a bad range
        // that only bites when the operator later flips Enabled = true is exactly the trap.
        var config = new AppConfig
        {
            Bgp = Bgp(),
            ApiRateLimit = new ApiRateLimitConfig { Enabled = false, PeriodSeconds = -30 }
        };

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("ApiRateLimit.PeriodSeconds", ex.Message);
    }

    [Fact]
    public void Validate_AcceptsValidApiRateLimitIncludingZeroConcurrencyCap()
    {
        // MaxConcurrentRequests = 0 is the documented "no cap" value and must stay valid.
        var config = new AppConfig
        {
            Bgp = Bgp(),
            ApiRateLimit = new ApiRateLimitConfig
            {
                Enabled = true,
                TokenLimit = 120,
                TokensPerPeriod = 120,
                PeriodSeconds = 60,
                MaxConcurrentRequests = 0
            }
        };

        var act = () => config.Validate();
        act();
    }

    [Fact]
    public void Validate_AcceptsNoApiRateLimitSection()
    {
        // The section is optional (absent = opt-out). A null section must not trip validation.
        var config = new AppConfig { Bgp = Bgp(), ApiRateLimit = null };
        var act = () => config.Validate();
        act();
    }

    [Theory]
    [InlineData("10.0.0.0/8x")]      // typo in the prefix length
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.0/33")]      // out of range for IPv4
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsMalformedTrustedProxiesEntry(string entry)
    {
        // A malformed entry used to be dropped at parse time with a warning, which collapses every
        // client behind the proxy into ONE rate-limit bucket and ONE /api/me identity.
        var config = new AppConfig { Bgp = Bgp(), TrustedProxies = ["127.0.0.0/8", entry] };

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("TrustedProxies[1]", ex.Message);
    }

    [Fact]
    public void Validate_AcceptsIpAndCidrTrustedProxies()
    {
        // Both documented forms must keep validating: a bare IP (no prefix) becomes a /32 or /128,
        // which is what ParseTrustedProxies does at runtime.
        var config = new AppConfig
        {
            Bgp = Bgp(),
            TrustedProxies = ["10.0.0.0/8", "192.168.1.1", "fd00::/8", "2001:db8::1"]
        };

        var act = () => config.Validate();
        act();
    }

    [Fact]
    public void Validate_AcceptsNoTrustedProxies()
    {
        // Null and empty both mean "never trust forwarding headers" — the secure default.
        foreach (var proxies in new List<string>?[] { null, [] })
        {
            var config = new AppConfig { Bgp = Bgp(), TrustedProxies = proxies };
            var act = () => config.Validate();
            act();
        }
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("example.com")]        // no scheme
    [InlineData("ftp://example.com")]  // wrong scheme
    [InlineData("/relative")]
    public void Validate_RejectsMalformedCorsOrigin(string entry)
    {
        // The allowlist is compared literally against the request Origin, so a typo'd string was
        // accepted at startup and then never matched anything — a silent allowlist that allows none.
        var config = new AppConfig { Bgp = Bgp(), CorsAllowedOrigins = [entry] };

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("CorsAllowedOrigins[0]", ex.Message);
    }

    [Fact]
    public void Validate_AcceptsHttpAndHttpsCorsOrigins()
    {
        var config = new AppConfig
        {
            Bgp = Bgp(),
            CorsAllowedOrigins = ["https://operator.example.com", "http://localhost:3000"]
        };

        var act = () => config.Validate();
        act();
    }

    [Fact]
    public void Validate_AcceptsNoCorsOrigins()
    {
        foreach (var origins in new List<string>?[] { null, [] })
        {
            var config = new AppConfig { Bgp = Bgp(), CorsAllowedOrigins = origins };
            var act = () => config.Validate();
            act();
        }
    }

    [Fact]
    public void Validate_RejectsAutoRefreshBelowTheEnforcedInterval()
    {
        // The timer clamps IntervalSeconds to >= 60, so a validated-but-overridden value is a
        // config the operator believes is applied and is not.
        var config = new AppConfig { Bgp = Bgp(), AutoRefresh = new AutoRefreshConfig { Enabled = true, IntervalSeconds = 30 } };

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("AutoRefresh.IntervalSeconds", ex.Message);
        Assert.Contains(AppConfig.MinAutoRefreshIntervalSeconds.ToString(), ex.Message);
    }

    [Fact]
    public void Validate_RejectsAutoRefreshJitterAboveTheEnforcedCeiling()
    {
        var config = new AppConfig { Bgp = Bgp(), AutoRefresh = new AutoRefreshConfig { Enabled = true, MaxJitterMs = 120_000 } };

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("AutoRefresh.MaxJitterMs", ex.Message);
    }

    [Fact]
    public void Validate_RejectsRipeStatTimeoutBelowTheEnforcedFloor()
    {
        // The Polly timeout is Math.Max(10, TimeoutSeconds) and the XML doc tells operators to
        // LOWER it for small ASes to fail fast — which silently did nothing below 10.
        var config = new AppConfig { Bgp = Bgp(), RipeStat = new RipeStatConfig { TimeoutSeconds = 5 } };

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("RipeStat.TimeoutSeconds", ex.Message);
    }

    [Fact]
    public void Validate_AcceptsDocumentedDefaults()
    {
        // Guard against an accidentally-too-tight range: the shipped defaults must all validate.
        var config = new AppConfig
        {
            Bgp = Bgp(),
            AutoRefresh = new AutoRefreshConfig
            {
                Enabled = true,
                IntervalSeconds = 600,
                NoEtagIntervalSeconds = 604800,
                MaxJitterMs = 2000
            },
            RipeStat = new RipeStatConfig { TimeoutSeconds = 180 }
        };

        var act = () => config.Validate();
        act();
    }
}
