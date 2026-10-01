using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Providers;
using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// D26 fail-closed REACHABILITY. <see cref="RouteAssemblerPolicyTests"/> pins the gate's logic
/// with stubs that THROW on failure — which is the only way <c>fetchFailures</c> can ever move.
/// These tests close that gap by driving the gate with the real
/// <see cref="PrefixService"/> / <see cref="PrefixSourceService"/> over a failing upstream, i.e.
/// the production call path, where a resolved failure is a value rather than an exception.
/// <para>
/// RED before the fix: the peer received the whole RU default set it never subscribed to.
/// </para>
/// </summary>
public sealed class RouteAssemblerFailClosedTests
{
    private static readonly UInt128 RuPrefix = 0x0A000000;
    private const int RuPrefixCount = 3000;

    private sealed class CapturingLogger : ILogger<RouteAssembler>
    {
        public List<string> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(formatter(state, ex));
        public bool FailedClosed => Entries.Any(m => m.Contains("NOT falling back to RU defaults"));
        public bool FellBackToRu => Entries.Any(m => m.Contains("resolved 0 prefixes, falling back to RU defaults"));
    }

    /// <summary>Every RIPEstat request fails — the outage / network-partition case.</summary>
    private sealed class AlwaysFailingHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>
    /// Serves a live RU/default set for the configured default source and fails for every other
    /// source, so the RU fallback has something real to leak.
    /// </summary>
    private sealed class RuOkOthersFailProvider : IPrefixSourceProvider
    {
        public string Kind => "file";
        public bool SupportsConditionalRequests => false;

        public Task<SourceLoadResult> LoadAsync(PrefixSourceConfig source, string? etag = null,
            DateTimeOffset? lastModified = null, CancellationToken ct = default)
        {
            if (source.Name != "ru")
                throw new HttpRequestException($"simulated outage for source '{source.Name}'");

            var list = new List<IpPrefix>(RuPrefixCount);
            for (uint i = 0; i < RuPrefixCount; i++)
                list.Add(new IpPrefix((UInt128)(i * 256), 24, true));
            return Task.FromResult(SourceLoadResult.Ok(list));
        }
    }

    private static AppConfig Config(params string[] prefixSourceNames)
    {
        var sources = new List<PrefixSourceConfig> { new() { Name = "ru", Kind = "file", Path = "nets.txt" } };
        foreach (var name in prefixSourceNames)
            sources.Add(new() { Name = name, Kind = "file", Path = $"{name}.txt" });

        return new AppConfig
        {
            Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" },
            DefaultPrefixSource = "ru",
            PrefixSources = sources,
            RipeStat = new RipeStatConfig
            {
                AsnLists = [new AsnList { Name = "tier1", Asns = [65010], Community = "65001:200" }]
            }
        };
    }

    private static PrefixSourceService SourceService(AppConfig config) =>
        new(config, new PrefixSourceProviderFactory([new RuOkOthersFailProvider()]),
            NullLogger<PrefixSourceService>.Instance);

    private static PrefixService RealPrefixService(AppConfig config, HttpMessageHandler handler, IPrefixSourceService sources)
    {
        var ripe = new RipeStatProvider(
            new StubHttpClientFactory(handler), NullLogger<RipeStatProvider>.Instance, config.RipeStat);
        var cache = new RipeStatPrefixCache(ripe, NullLogger<RipeStatPrefixCache>.Instance);
        return new PrefixService(config, cache, sources, null!, logger: NullLogger<PrefixService>.Instance);
    }

    private sealed class SubscribedPeerStore(List<string> subscriptions) : IPeerStore
    {
        public Task<string> CreatePeerAsync(string ip, uint asn, string? description, CancellationToken ct = default)
            => Task.FromResult("id");
        public Task UpsertPeerAsync(string ip, uint asn, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateSessionStatusAsync(string ip, uint asn, bool active, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<int?> GetPeerMaxPrefixAsync(string ip, uint asn, CancellationToken ct = default)
            => Task.FromResult<int?>(null);
        public Task<PeerRoutingView?> LoadPeerRoutingViewAsync(string ip, uint asn, CancellationToken ct = default)
            => Task.FromResult<PeerRoutingView?>(new("id", subscriptions, [], [], []));
    }

    private static RouteAssembler Assembler(IPrefixService prefixService, AppConfig config,
        List<string> subscriptions, CapturingLogger logger)
        => new(prefixService, new SubscribedPeerStore(subscriptions),
            new ConfigCommunityResolver(config, config.Bgp), AllowAllFilter.Instance,
            config, config.Bgp, logger);

    private static Task<List<Route>> BuildAsync(RouteAssembler assembler) =>
        assembler.BuildOutboundRoutesAsync("203.0.113.7", 65002,
            new PeerConfig { Address = "203.0.113.7" }, "203.0.113.7", CancellationToken.None);

    [Fact]
    public async Task AsnSubscription_RipestatOutage_FailsClosed_NoRuDump()
    {
        // The peer asked for ONE ASN. RIPEstat is down. It must not be handed the RU table.
        var config = Config();
        var handler = new AlwaysFailingHandler();
        var log = new CapturingLogger();
        var assembler = Assembler(RealPrefixService(config, handler, SourceService(config)), config, ["tier1"], log);

        var routes = await BuildAsync(assembler);

        Assert.Empty(routes);                       // RED pre-fix: RuPrefixCount prefixes
        Assert.True(handler.Calls > 0, "the upstream was never actually contacted");
        Assert.True(log.FailedClosed, "the D26 fail-closed branch did not run");
        Assert.False(log.FellBackToRu, "the RU fallback fired on a total source failure");
    }

    [Fact]
    public async Task PrefixSourceSubscription_SourceOutage_FailsClosed_NoRuDump()
    {
        // The peer asked for ONE prefix source. That source is down; the RU default is healthy.
        // It must not be handed the RU table either.
        var config = Config("broken");
        var log = new CapturingLogger();
        var assembler = Assembler(
            RealPrefixService(config, new AlwaysFailingHandler(), SourceService(config)),
            config, ["broken"], log);

        var routes = await BuildAsync(assembler);

        Assert.Empty(routes);                        // RED pre-fix: RuPrefixCount prefixes
        Assert.True(log.FailedClosed, "the D26 fail-closed branch did not run");
        Assert.False(log.FellBackToRu, "the RU fallback fired on a total source failure");
    }

    [Fact]
    public async Task PrefixSourceSubscription_SourceOutage_StaysClosedWithinNegativeBackoffWindow()
    {
        // A repeat fetch inside the negative-cache backoff returns the negative entry as a fresh
        // EMPTY list (no exception), so the failure would look like "resolved to nothing" on every
        // refresh cycle for the whole backoff window — not just the first.
        var config = Config("broken");
        var sources = SourceService(config);
        var log = new CapturingLogger();
        var assembler = Assembler(
            RealPrefixService(config, new AlwaysFailingHandler(), sources), config, ["broken"], log);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var routes = await BuildAsync(assembler);
            Assert.True(routes.Count == 0, $"cycle {cycle}: peer was handed {routes.Count} prefixes");
            Assert.False(log.FellBackToRu, $"cycle {cycle}: the RU fallback fired");
        }
    }

    [Fact]
    public async Task AsnsThatLegitimatelyHaveNoPrefixes_StillFallBackToRu()
    {
        // Control for the new throw: an ASN that RESOLVES to zero prefixes is not a failure, so the
        // documented "configured peer resolved 0 prefixes" fallback must still fire. A stub upstream
        // serving a well-formed empty ris-prefixes payload is the shape RIPEstat actually returns.
        var config = Config();
        var log = new CapturingLogger();
        var emptyRipestat = new EmptyRipestatHandler();
        var assembler = Assembler(
            RealPrefixService(config, emptyRipestat, SourceService(config)), config, ["tier1"], log);

        var routes = await BuildAsync(assembler);

        Assert.Equal(RuPrefixCount, routes.Count);
        Assert.True(log.FellBackToRu, "the documented RU fallback did not fire for a resolved-empty source");
        Assert.False(log.FailedClosed, "a resolved-empty source must not be treated as a failure");
    }

    [Fact]
    public async Task UnconfiguredPeer_IsUnaffected()
    {
        // A peer with no subscriptions/customs/user-sources gets RU defaults by design (D11
        // auto-registration). The fail-closed gate must not touch that branch.
        var config = Config();
        var log = new CapturingLogger();
        var assembler = Assembler(
            RealPrefixService(config, new AlwaysFailingHandler(), SourceService(config)),
            config, [], log);

        var routes = await BuildAsync(assembler);

        Assert.Equal(RuPrefixCount, routes.Count);
        Assert.False(log.FailedClosed, "the configured-peer gate leaked into the unconfigured branch");
    }

    /// <summary>A well-formed RIPEstat response that simply contains no prefixes.</summary>
    private sealed class EmptyRipestatHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            const string json = """{"status":"ok","data":{"prefixes":{"ipv4":[],"ipv6":[]}}}""";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
