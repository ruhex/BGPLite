using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BGPLite.Protocol;

namespace BGPLite.Tests;

/// <summary>
/// D26: the outbound policy under failure. A CONFIGURED peer that resolves zero routes falls
/// back to the RU default list only when its sources legitimately resolved to nothing — a TOTAL
/// fetch failure (RIPEstat outage / network partition) fails CLOSED, because substituting the full
/// RU dump would advertise hundreds of thousands of prefixes the peer never asked for. An unknown
/// subscription name is a config typo and must be logged, not silently ignored.
/// </summary>
public sealed class RouteAssemblerPolicyTests
{
    private static readonly UInt128 RuPrefix = 0x0A000000;

    private static RouteAssembler NewAssembler(IPrefixService prefixService, AppConfig config, ILogger<RouteAssembler>? logger = null)
        => new(
            prefixService,
            new ConfiguredPeerStore(),
            new ConfigCommunityResolver(config, config.Bgp),
            AllowAllFilter.Instance,
            config,
            config.Bgp,
            logger ?? NullLogger<RouteAssembler>.Instance);

    private static AppConfig Config() => new()
    {
        Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" },
        RipeStat = new RipeStatConfig
        {
            AsnLists =
            [
                new AsnList { Name = "tier1", Asns = [65010], Community = "65001:200" },
                new AsnList { Name = "tier2", Asns = [65020], Community = "65001:201" }
            ]
        }
    };

    /// <summary>Every fetch succeeds with EMPTY lists except where a test overrides one member.</summary>
    private class StubPrefixService : IPrefixService
    {
        public Func<IEnumerable<uint>, CancellationToken, Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>>>? OnGetPrefixesForAsns { get; set; }
        public Func<IEnumerable<uint>, CancellationToken, Task<AsnPrefixResolution>>? OnGetPrefixesForAsnsResult { get; set; }
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetPrefixesAsync(uint asn, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>>([]);
        public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default)
            => OnGetPrefixesForAsns?.Invoke(asns, ct) ?? Task.FromResult(new List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>());
        public async Task<AsnPrefixResolution> GetPrefixesForAsnsResultAsync(IEnumerable<uint> asns, CancellationToken ct = default)
            => OnGetPrefixesForAsnsResult is not null
                ? await OnGetPrefixesForAsnsResult(asns, ct)
                : new AsnPrefixResolution(await GetPrefixesForAsns(asns, ct), []);
        public Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default) => Task.FromResult(0);
        public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default)
            => Task.FromResult(new List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)> { (RuPrefix, 8, true, 0u) });
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>>([]);
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetUserSourcePrefixesAsync(string name, string url, string? community, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>>([]);
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>An existing peer subscribed to "tier1" — the configured-peer branch.</summary>
    private sealed class ConfiguredPeerStore : IPeerStore
    {
        public List<string> Subscriptions { get; set; } = ["tier1"];
        public List<CustomSourceView> UserSources { get; set; } = [];
        public Task<string> CreatePeerAsync(string ip, uint asn, string? description, CancellationToken ct = default) => Task.FromResult("id");
        public Task UpsertPeerAsync(string ip, uint asn, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateSessionStatusAsync(string ip, uint asn, bool active, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int?> GetPeerMaxPrefixAsync(string ip, uint asn, CancellationToken ct = default) => Task.FromResult<int?>(null);
        public Task<PeerRoutingView?> LoadPeerRoutingViewAsync(string ip, uint asn, CancellationToken ct = default)
            => Task.FromResult<PeerRoutingView?>(new("id", Subscriptions, [], [], UserSources));
    }

    [Fact]
    public async Task TotalSourceFailure_FailsClosed_NoRuDump()
    {
        // D26: the only configured fetch THROWS (outage) — the RU fallback must NOT fire;
        // the peer keeps an empty set instead of the whole RU table.
        var logger = new CapturingLogger();
        var service = new StubPrefixService
        {
            OnGetPrefixesForAsns = (_, _) => throw new InvalidOperationException("simulated outage")
        };
        var assembler = NewAssembler(service, Config(), logger);

        var routes = await assembler.BuildOutboundRoutesAsync(
            "203.0.113.7", 65002, new PeerConfig { Address = "203.0.113.7" }, "203.0.113.7", CancellationToken.None);

        Assert.Empty(routes);   // RED pre-fix: the RU fallback fired and added the RU prefix
        Assert.Contains(logger.Entries, e => e.Message.Contains("NOT falling back"));
    }

    [Fact]
    public async Task LegitimatelyEmptySources_StillFallBackToRu()
    {
        // Control for the same gate: the sources RESOLVED (to an empty list — the operator emptied
        // them) — the documented "configured peer resolved 0 prefixes" fallback still applies.
        var assembler = NewAssembler(new StubPrefixService(), Config());

        var routes = await assembler.BuildOutboundRoutesAsync(
            "203.0.113.7", 65002, new PeerConfig { Address = "203.0.113.7" }, "203.0.113.7", CancellationToken.None);

        var route = Assert.Single(routes);
        Assert.Equal((RuPrefix, (byte)8), (route.Prefix, route.PrefixLength));
    }

    [Fact]
    /// <summary>Verifies that mixed failure plus successful empty resolution retains the documented RU fallback.</summary>
    public async Task MixedFailureAndEmptyResolution_StillFallsBackToRu()
    {
        // D26 refinement: fail-closed is for TOTAL failure only. tier1 fails
        // (outage), tier2 RESOLVES to an empty list — a source answered, so the build is not a
        // total failure and the documented "configured peer resolved 0 prefixes" fallback applies.
        var store = new ConfiguredPeerStore { Subscriptions = ["tier1", "tier2"] };
        var config = Config();
        var service = new StubPrefixService
        {
            OnGetPrefixesForAsns = (asns, _) => asns.Contains(65010u)
                ? throw new InvalidOperationException("simulated outage")
                : Task.FromResult(new List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>())
        };
        var assembler = new RouteAssembler(
            service, store,
            new ConfigCommunityResolver(config, config.Bgp),
            AllowAllFilter.Instance, config, config.Bgp, NullLogger<RouteAssembler>.Instance);

        var routes = await assembler.BuildOutboundRoutesAsync(
            "203.0.113.7", 65002, new PeerConfig { Address = "203.0.113.7" }, "203.0.113.7", CancellationToken.None);

        var route = Assert.Single(routes);
        Assert.Equal((RuPrefix, (byte)8), (route.Prefix, route.PrefixLength));   // the RU fallback fired
    }

    [Fact]
    public async Task PartialAsnFailure_MarksResultIncomplete_ButKeepsBgpSurvivingRoutes()
    {
        var service = new StubPrefixService
        {
            OnGetPrefixesForAsnsResult = (_, _) => Task.FromResult(new AsnPrefixResolution(
                [(0xC0000200, 24, true, 65020u)], [65010u]))
        };
        var assembler = NewAssembler(service, Config());

        var result = await assembler.BuildOutboundRouteResultAsync(
            "203.0.113.7", 65002, new PeerConfig { Address = "203.0.113.7" }, "203.0.113.7", CancellationToken.None);
        var bgpRoutes = await assembler.BuildOutboundRoutesAsync(
            "203.0.113.7", 65002, new PeerConfig { Address = "203.0.113.7" }, "203.0.113.7", CancellationToken.None);

        Assert.False(result.Complete);
        var assembledRoute = Assert.Single(result.Routes);
        var bgpRoute = Assert.Single(bgpRoutes);
        Assert.Equal((0xC0000200, (byte)24), (assembledRoute.Prefix, assembledRoute.PrefixLength));
        Assert.Equal((0xC0000200, (byte)24), (bgpRoute.Prefix, bgpRoute.PrefixLength));
    }

    [Fact]
    /// <summary>Verifies that an unknown peer subscription produces an actionable warning instead of silent zero routes.</summary>
    public async Task UnknownSubscriptionName_IsWarned_NotSilentlyIgnored()
    {
        // A subscription matching no AsnLists entry and no PrefixSource is a config typo —
        // it was silently ignored on every build. Name it in the log.
        var logger = new CapturingLogger();
        var store = new ConfiguredPeerStore { Subscriptions = ["no-such-list"] };
        var config = Config();
        var assembler = new RouteAssembler(
            new StubPrefixService(), store,
            new ConfigCommunityResolver(config, config.Bgp),
            AllowAllFilter.Instance, config, config.Bgp, logger);

        await assembler.BuildOutboundRoutesAsync(
            "203.0.113.7", 65002, new PeerConfig { Address = "203.0.113.7" }, "203.0.113.7", CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("no-such-list"));
    }

    [Fact]
    public async Task MultipleCountrySubscriptions_KeepEachListsCommunity()
    {
        var config = new AppConfig
        {
            Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" },
            RipeStat = new RipeStatConfig
            {
                AsnLists =
                [
                    new AsnList { Name = "ru-a", Country = "RU", Community = "65001:200" },
                    new AsnList { Name = "ru-b", Country = "RU", Community = "65001:201" }
                ]
            }
        };
        var store = new ConfiguredPeerStore { Subscriptions = ["ru-a", "ru-b"] };
        var assembler = new RouteAssembler(
            new StubPrefixService(), store,
            new ConfigCommunityResolver(config, config.Bgp),
            AllowAllFilter.Instance, config, config.Bgp, NullLogger<RouteAssembler>.Instance);

        var routes = await assembler.BuildOutboundRoutesAsync(
            "203.0.113.7", 65002, new PeerConfig { Address = "203.0.113.7" }, "203.0.113.7", CancellationToken.None);

        Assert.Equal(2, routes.Count);
        var communities = routes.SelectMany(r => r.Communities).ToArray();
        Assert.Contains(CommunityCodec.Parse("65001:200"), communities);
        Assert.Contains(CommunityCodec.Parse("65001:201"), communities);
    }

    [Fact]
    /// <summary>Verifies that control characters in a user-source name cannot forge additional log lines.</summary>
    public async Task UserSourceName_ControlCharacters_CannotForgeLogLines()
    {
        var logger = new CapturingLogger();
        var store = new ConfiguredPeerStore
        {
            Subscriptions = [],
            UserSources = [new CustomSourceView("evil\nFORGED", "https://example.com/nets.txt", null)]
        };
        var config = Config();
        var assembler = new RouteAssembler(
            new StubPrefixService(), store,
            new ConfigCommunityResolver(config, config.Bgp),
            AllowAllFilter.Instance, config, config.Bgp, logger);

        await assembler.BuildOutboundRoutesAsync(
            "203.0.113.7", 65002, new PeerConfig { Address = "203.0.113.7" }, "203.0.113.7", CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Message.Contains("evil FORGED", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains('\n') || e.Message.Contains('\r'));
    }

    [Fact]
    public async Task NullAsnListsCollection_IsTreatedAsEmpty_NotNre()
    {
        // Explicit YAML null ("AsnLists:") passes Validate as "none", so the build must treat it
        // the same way: the old expression only guarded a null RipeStat section and dereferenced
        // the null collection itself — the build crashed before subscription resolution ran.
        var logger = new CapturingLogger();
        var config = new AppConfig
        {
            Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" },
            RipeStat = new RipeStatConfig { AsnLists = null! }
        };
        var store = new ConfiguredPeerStore { Subscriptions = ["tier1"] };
        var assembler = new RouteAssembler(
            new StubPrefixService(), store,
            new ConfigCommunityResolver(config, config.Bgp),
            AllowAllFilter.Instance, config, config.Bgp, logger);

        var routes = await assembler.BuildOutboundRoutesAsync(
            "203.0.113.7", 65002, new PeerConfig { Address = "203.0.113.7" }, "203.0.113.7", CancellationToken.None);

        // No lists = the subscription resolves to nothing (warned as unknown), no fetch is
        // attempted, and the documented zero-routes fallback still applies.
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("tier1"));
        var route = Assert.Single(routes);
        Assert.Equal((RuPrefix, (byte)8), (route.Prefix, route.PrefixLength));
    }

    private sealed class CapturingLogger : ILogger<RouteAssembler>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
