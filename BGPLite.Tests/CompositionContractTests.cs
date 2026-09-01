using System.Reflection;
using BGPLite.Api;
using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Protocol;
using BGPLite.Providers;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// #263: the send path's dependencies were nullable-optional the whole way down —
/// <c>BgpServer</c> → <c>BgpSession</c> → <c>RouteAssembler</c> — with each layer supplying a silent
/// fallback for the missing one. Dropping a single DI registration therefore produced no error at
/// all: peers kept their sessions and simply received the shared table's seeded routes instead of
/// the prefixes their operator had selected, which is a wrong route set rather than a disabled
/// feature.
/// <para>
/// These tests pin the two properties that make that unreachable: production collaborators cannot
/// be constructed without their dependencies, and the degraded behavior is a separate named type a
/// caller has to choose on purpose.
/// </para>
/// </summary>
public class CompositionContractTests
{
    /// <summary>
    /// Every dependency #263 lists as "nullable-optional" must now be required — no default value
    /// and not a nullable reference type — so an incomplete composition fails to compile, or fails
    /// at container build with the service named, instead of degrading at the first route send.
    /// </summary>
    [Theory]
    // RouteAssembler.cs:22-24 — the three whose absence silently switched the whole peer over to
    // the shared table.
    [InlineData(typeof(RouteAssembler), "prefixService")]
    [InlineData(typeof(RouteAssembler), "peerStore")]
    [InlineData(typeof(RouteAssembler), "appConfig")]
    // The collaborators BgpServer used to forward, now owned by the factory.
    [InlineData(typeof(BgpSessionFactory), "peerStore")]
    [InlineData(typeof(BgpSessionFactory), "prefixAggregator")]
    [InlineData(typeof(BgpSessionFactory), "routeAssembler")]
    // ManagementApi.cs:57-59 — without sessionManager a peer edited in the UI was persisted and
    // never pushed to its live session.
    [InlineData(typeof(ManagementApi), "prefixService")]
    [InlineData(typeof(ManagementApi), "prefixSources")]
    [InlineData(typeof(ManagementApi), "sessionManager")]
    // PrefixService.cs:14-15 — a null http provider made every per-peer user URL source resolve to
    // zero prefixes, and a null RIPEstat did the same for custom ASNs.
    [InlineData(typeof(PrefixService), "ripeStatCache")] // #267 item 5: renamed when the per-ASN cache became a shared component
    [InlineData(typeof(PrefixService), "httpProvider")]
    public void ProductionDependency_IsRequired(Type type, string parameterName)
    {
        var ctor = Assert.Single(type.GetConstructors());
        var parameter = ctor.GetParameters().SingleOrDefault(p => p.Name == parameterName);

        Assert.True(parameter is not null,
            $"{type.Name} has no constructor parameter '{parameterName}' — if it was renamed, update this list; " +
            "if it was dropped, #263 needs re-checking rather than the test deleting.");
        Assert.False(parameter!.HasDefaultValue,
            $"{type.Name}.{parameterName} has a default value again — an omitted dependency is silent by construction (#263)");
        Assert.Equal(NullabilityState.NotNull, new NullabilityInfoContext().Create(parameter).WriteState);
    }

    /// <summary>
    /// The other half of #263's acceptance: the accept loop does not build sessions itself, so it
    /// no longer carries — or can silently drop — any of the session's dependencies.
    /// </summary>
    [Fact]
    public void BgpServer_DoesNotCarryTheSessionsDependencies()
    {
        var parameters = Assert.Single(typeof(BgpServer).GetConstructors()).GetParameters();

        Assert.Contains(parameters, p => p.ParameterType == typeof(IBgpSessionFactory));
        foreach (var forwarded in new[]
                 {
                     typeof(IPeerStore), typeof(IPrefixService), typeof(ICommunityResolver),
                     typeof(IPrefixAggregator), typeof(Action<string, uint>)
                 })
        {
            Assert.DoesNotContain(parameters, p => p.ParameterType == forwarded);
        }
    }

    // ---- the degraded assembler is explicit, and behaves as #289/#307 established ----

    /// <summary>
    /// Tenant isolation, carried over from #307 into the type that now owns the behavior: the
    /// shared table also holds every NLRI peers announced inbound, and handing those to a different
    /// peer leaks one tenant's routes to another.
    /// </summary>
    [Fact]
    public async Task SharedTableAssembler_ServesTheSeedButNotRoutesOwnedByAPeer()
    {
        var table = new RouteTable();
        table.AddOrUpdate(new Route { Prefix = 0xC0000200, PrefixLength = 24, NextHop = 0x7F000001 });
        var otherPeer = new object();
        table.AddOrUpdate(new Route { Prefix = 0x0A000000, PrefixLength = 8, NextHop = 0x0A0A0A0A }, owner: otherPeer);

        var routes = await NewSharedTableAssembler(table, NullLogger.Instance)
            .BuildOutboundRoutesAsync("127.0.0.1", 65002, new PeerConfig { Address = "127.0.0.1" }, "127.0.0.1", default);

        Assert.Equal(0xC0000200u, Assert.Single(routes).Prefix);
    }

    /// <summary>
    /// Reaching this assembler in production means a wiring error, so it says so — once, not on
    /// every refresh, which is what a per-send warning would have done.
    /// </summary>
    [Fact]
    public async Task SharedTableAssembler_AnnouncesItselfExactlyOnce()
    {
        var log = new CountingLogger();
        var assembler = NewSharedTableAssembler(new RouteTable(), log);
        var peer = new PeerConfig { Address = "127.0.0.1" };

        for (var i = 0; i < 3; i++)
            await assembler.BuildOutboundRoutesAsync("127.0.0.1", 65002, peer, "127.0.0.1", default);

        Assert.Equal(1, log.Warnings);
    }

    /// <summary>The outgoing community filter still applies — the fallback is degraded, not unfiltered.</summary>
    [Fact]
    public async Task SharedTableAssembler_AppliesTheOutgoingFilter()
    {
        var table = new RouteTable();
        table.AddOrUpdate(new Route { Prefix = 0xC0000200, PrefixLength = 24, NextHop = 0x7F000001 });

        var routes = await new SharedTableRouteAssembler(table, new RejectAllOutgoingFilter(), NullLogger.Instance)
            .BuildOutboundRoutesAsync("127.0.0.1", 65002, new PeerConfig { Address = "127.0.0.1" }, "127.0.0.1", default);

        Assert.Empty(routes);
    }

    // ---- the factory actually wires what it was given ----

    /// <summary>
    /// The session built by the factory asks the INJECTED assembler for its route set, with the
    /// peer identity resolved from the OPEN. Before #263 the session constructed its own assembler
    /// from arguments threaded through BgpServer, so this wiring had no seam to assert on.
    /// </summary>
    [Fact]
    public async Task Factory_BuildsSessionsThatUseTheInjectedAssembler()
    {
        var assembler = new RecordingAssembler();
        var store = new RecordingPeerStore();
        var (session, run, conn) = await EstablishThroughFactoryAsync(assembler, store);

        // IsEstablished flips BEFORE the establish dump finishes, so give the (async) build a
        // moment to reach the assembler on a loaded CI runner instead of asserting immediately.
        for (var i = 0; i < 250 && assembler.Asked == default; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        Assert.Equal(("127.0.0.1", 65002u), assembler.Asked);
        // The label is the peer's display form, which is what every assembler log line carries.
        Assert.Contains("127.0.0.1", assembler.Label);

        await TeardownAsync(session, run, conn);
    }

    /// <summary>
    /// The peer row is upserted as soon as OPEN identifies the peer. That used to be a lambda in
    /// Program.cs threaded through BgpServer as an optional <c>Action</c>; the factory owns it now,
    /// and dropping it would otherwise be invisible until a peer failed to appear in the UI.
    /// </summary>
    [Fact]
    public async Task Factory_BuildsSessionsThatRegisterThePeerOnOpen()
    {
        var store = new RecordingPeerStore();
        var (session, run, conn) = await EstablishThroughFactoryAsync(new RecordingAssembler(), store);

        for (var i = 0; i < 250 && store.Upserted == default; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        Assert.Equal(("127.0.0.1", 65002u), store.Upserted);

        await TeardownAsync(session, run, conn);
    }

    // ---- harness ----

    /// <summary>The degraded assembler over <paramref name="table"/> with no outgoing filtering.</summary>
    private static SharedTableRouteAssembler NewSharedTableAssembler(RouteTable table, ILogger logger) =>
        new(table, AllowAllFilter.Instance, logger);

    /// <summary>
    /// Drives a factory-built session to Established over the <c>IBgpConnection</c> seam, so the
    /// assertions observe what the factory actually wired rather than a hand-assembled session.
    /// </summary>
    private static async Task<(BgpSession Session, Task Run, ScriptedConnection Conn)> EstablishThroughFactoryAsync(
        IRouteAssembler assembler, IPeerStore store)
    {
        var factory = new BgpSessionFactory(
            new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 },
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            NullLogger<BgpSession>.Instance,
            store,
            new ExactUnionPrefixAggregator(),
            assembler,
            TimeProvider.System);

        var conn = new ScriptedConnection();
        var session = factory.Create(conn, new PeerConfig { Address = "127.0.0.1" });
        var run = session.RunAsync();

        conn.EnqueueMessage(new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = 0,
            RouterId = 0x0A000002,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)],
        });
        conn.EnqueueMessage(BgpKeepaliveMessage.Instance);

        for (var i = 0; i < 200 && !session.IsEstablished; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        Assert.True(session.IsEstablished, "session must reach Established");
        return (session, run, conn);
    }

    /// <summary>Closes the session without emitting a NOTIFICATION and waits for its loops to unwind.</summary>
    private static async Task TeardownAsync(BgpSession session, Task run, ScriptedConnection conn)
    {
        session.MarkSilentClose();
        try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { /* expected */ }
        catch (IOException) { /* expected */ }
        session.Dispose();
        conn.Dispose();
    }

    /// <summary>Records the one call the session makes and returns nothing to send.</summary>
    private sealed class RecordingAssembler : IRouteAssembler
    {
        public (string Ip, uint Asn) Asked { get; private set; }
        public string Label { get; private set; } = "";

        public Task<List<Route>> BuildOutboundRoutesAsync(
            string peerIp, uint remoteAsn, PeerConfig filterPeerConfig, string peerLabel, CancellationToken ct)
        {
            Asked = (peerIp, remoteAsn);
            Label = peerLabel;
            return Task.FromResult(new List<Route>());
        }
    }

    /// <summary>Records the identity callback; every other member throws to catch unexpected use.</summary>
    private sealed class RecordingPeerStore : IPeerStore
    {
        public (string Ip, uint Asn) Upserted { get; private set; }

        public Task UpsertPeerAsync(string ip, uint asn, CancellationToken ct = default)
        {
            Upserted = (ip, asn);
            return Task.CompletedTask;
        }
        public Task UpdateSessionStatusAsync(string ip, uint asn, bool active, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int?> GetPeerMaxPrefixAsync(string ip, uint asn, CancellationToken ct = default) => Task.FromResult<int?>(null);
        public Task<string> CreatePeerAsync(string ip, uint asn, string? description, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PeerRoutingView?> LoadPeerRoutingViewAsync(string ip, uint asn, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Drops every outbound route, so the filter's effect is unambiguous.</summary>
    private sealed class RejectAllOutgoingFilter : IRouteFilter
    {
        private static readonly IReadOnlySet<uint> Empty = new HashSet<uint>();
        public bool AcceptIncoming(Route route, PeerConfig peer) => true;
        public Task<IReadOnlySet<uint>> ResolveOutgoingAllowSetAsync(PeerConfig peer, CancellationToken ct = default) => Task.FromResult(Empty);
        public bool AcceptOutgoing(Route route, PeerConfig peer, IReadOnlySet<uint> allowSet) => false;
    }

    /// <summary>Counts Warning-level entries.</summary>
    private sealed class CountingLogger : ILogger
    {
        private int _warnings;
        public int Warnings => Volatile.Read(ref _warnings);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Interlocked.Increment(ref _warnings);
        }
    }
}
