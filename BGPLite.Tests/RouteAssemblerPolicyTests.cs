using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BGPLite.Protocol;

namespace BGPLite.Tests;

/// <summary>
/// #488 (D26): the outbound policy under failure. A CONFIGURED peer that resolves zero routes falls
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
            AsnLists = [new AsnList { Name = "tier1", Asns = [65010], Community = "65001:200" }]
        }
    };

    /// <summary>Every fetch succeeds with EMPTY lists except where a test overrides one member.</summary>
    private class StubPrefixService : IPrefixService
    {
        public Func<IEnumerable<uint>, CancellationToken, Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>>>? OnGetPrefixesForAsns { get; set; }
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetPrefixesAsync(uint asn, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>>([]);
        public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default)
            => OnGetPrefixesForAsns?.Invoke(asns, ct) ?? Task.FromResult(new List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>());
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
        public Task<string> CreatePeerAsync(string ip, uint asn, string? description, CancellationToken ct = default) => Task.FromResult("id");
        public Task UpsertPeerAsync(string ip, uint asn, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateSessionStatusAsync(string ip, uint asn, bool active, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int?> GetPeerMaxPrefixAsync(string ip, uint asn, CancellationToken ct = default) => Task.FromResult<int?>(null);
        public Task<PeerRoutingView?> LoadPeerRoutingViewAsync(string ip, uint asn, CancellationToken ct = default)
            => Task.FromResult<PeerRoutingView?>(new("id", Subscriptions, [], [], []));
    }

    [Fact]
    public async Task TotalSourceFailure_FailsClosed_NoRuDump()
    {
        // #488 (D26): the only configured fetch THROWS (outage) — the RU fallback must NOT fire;
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
    public async Task UnknownSubscriptionName_IsWarned_NotSilentlyIgnored()
    {
        // #488: a subscription matching no AsnLists entry and no PrefixSource is a config typo —
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

    private sealed class CapturingLogger : ILogger<RouteAssembler>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
