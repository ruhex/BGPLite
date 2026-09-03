using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BGPLite.Protocol;

namespace BGPLite.Tests;

/// <summary>
/// #330 item 1: RouteAssembler's per-fetch catch-alls used to swallow OperationCanceledException,
/// so a session teardown mid-build logged a burst of ERROR "Failed to fetch ..." lines for what is
/// a normal shutdown. OCE must propagate — but ONLY caller-initiated cancellation: a per-source
/// timeout surfaces as OCE too (HttpPrefixProvider's linked CTS with a LIVE caller token) and must
/// stay a logged fetch failure, or one slow source tears down the whole session (#330 review).
/// </summary>
public sealed class RouteAssemblerCancellationTests
{
    [Fact]
    public async Task BuildOutboundRoutes_CallerCancelled_PropagatesOCE()
    {
        var assembler = NewAssembler(new CancelledRuFetchPrefixService());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            assembler.BuildOutboundRoutesAsync(
                "203.0.113.7", 65002, new PeerConfig { Address = "203.0.113.7" }, "203.0.113.7",
                new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task BuildOutboundRoutes_PerSourceTimeoutOCE_LoggedNotPropagated()
    {
        // Same OCE from the provider, but the caller token is LIVE — this is a per-source
        // timeout, not teardown: the build must continue (return an empty set here) and log a
        // fetch failure instead of unwinding into a session reset / withdraw-all.
        var logger = new CapturingLogger();
        var assembler = NewAssembler(new CancelledRuFetchPrefixService(), logger);

        var routes = await assembler.BuildOutboundRoutesAsync(
            "203.0.113.7", 65002, new PeerConfig { Address = "203.0.113.7" }, "203.0.113.7",
            CancellationToken.None);

        Assert.Empty(routes);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);   // a fetch failure, logged — not a silent swallow
    }

    private static RouteAssembler NewAssembler(IPrefixService prefixService, ILogger<RouteAssembler>? logger = null)
    {
        var config = new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } };
        return new RouteAssembler(
            prefixService,
            new UnconfiguredPeerStore(),
            new ConfigCommunityResolver(config, config.Bgp),
            AllowAllFilter.Instance,
            config,
            config.Bgp,
            logger ?? NullLogger<RouteAssembler>.Instance);
    }

    /// <summary>Every fetch succeeds except the RU list, which reports a cancelled task.</summary>
    private sealed class CancelledRuFetchPrefixService : IPrefixService
    {
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetPrefixesAsync(uint asn, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>>([]);
        public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default)
            => Task.FromResult(new List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>());
        public Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default) => Task.FromResult(0);
        public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default)
            => Task.FromCanceled<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>>(new CancellationToken(canceled: true));
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>>([]);
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetUserSourcePrefixesAsync(string name, string url, string? community, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>>([]);
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Resolves to an existing peer with no subscriptions — the unconfigured-peer branch.</summary>
    private sealed class UnconfiguredPeerStore : IPeerStore
    {
        public Task<string> CreatePeerAsync(string ip, uint asn, string? description, CancellationToken ct = default) => Task.FromResult("id");
        public Task UpsertPeerAsync(string ip, uint asn, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateSessionStatusAsync(string ip, uint asn, bool active, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int?> GetPeerMaxPrefixAsync(string ip, uint asn, CancellationToken ct = default) => Task.FromResult<int?>(null);
        public Task<PeerRoutingView?> LoadPeerRoutingViewAsync(string ip, uint asn, CancellationToken ct = default)
            => Task.FromResult<PeerRoutingView?>(new("id", [], [], [], []));
    }

    private sealed class CapturingLogger : ILogger<RouteAssembler>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
