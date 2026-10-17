using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// A route refresh is a withdraw + re-announce pair, so the order of the two halves decides what a
/// recoverable failure costs. With the route set resolved AFTER the withdrawal, a transient
/// peer-store failure inside the assembler — <c>LoadPeerRoutingViewAsync</c> issues a write, and
/// SQLite serializes writers, so an operator saving a peer through the management API while
/// <c>RefreshAllEstablishedAsync</c> fans out across every established session can push one past
/// the busy timeout — unwound to <c>RefreshCycleAsync</c>'s generic catch with the peer's whole
/// advertisement already withdrawn and the mirror cleared. The session stayed Established and
/// nothing re-announced: a blackhole for that peer until some later refresh happened to succeed,
/// i.e. never with auto-refresh off, which is the default. <c>SendAllRoutesAsync</c> already
/// guarded its <c>GetPeerMaxPrefixAsync</c> call for exactly this hazard; these tests pin the same
/// property for the route build itself.
/// </summary>
public class BgpSessionRefreshFailureTests
{
    private static Route TestRoute() => new()
    {
        Prefix = 0x0A000000,   // 10.0.0.0/8
        PrefixLength = 8,
        IsIpv4 = true,
        NextHop = 0x7F000001
    };

    private static async Task<(BgpSession Session, Task Run, ScriptedConnection Conn)> EstablishAsync(
        IRouteAssembler assembler)
    {
        var conn = new ScriptedConnection();
        var session = new BgpSession(
            conn,
            new PeerConfig { Address = "127.0.0.1" },
            new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 },
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            NullLogger<BgpSession>.Instance,
            routeAssembler: assembler);

        var run = session.RunAsync();
        conn.EnqueueMessage(new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = 0,
            RouterId = 0x0A000002,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)]
        });
        conn.EnqueueMessage(BgpKeepaliveMessage.Instance);

        // Established is not dump-complete: wait for the initial advertisement to reach the wire,
        // since the whole point of the test is what a LATER failure does to it.
        for (var i = 0; i < 300 && session.AdvertisedPrefixCount == 0; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        Assert.True(session.IsEstablished, "session must reach Established");
        Assert.Equal(1, session.AdvertisedPrefixCount);
        return (session, run, conn);
    }

    /// <summary>
    /// The regression: the assembler fails on the refresh build with a transient
    /// "database is locked". Nothing may be withdrawn, because nothing can be re-announced —
    /// the peer must keep the advertisement it already has.
    /// </summary>
    [Fact]
    public async Task RefreshRoutes_StoreFailureOnTheBuild_LeavesTheAdvertisementIntact()
    {
        var assembler = new FailAfterFirstBuildRouteAssembler([TestRoute()]);
        var (session, run, conn) = await EstablishAsync(assembler);

        await session.RefreshRoutesAsync();

        Assert.Equal(2, assembler.Calls);   // the failing refresh build really did run
        Assert.Empty(Withdrawals(conn));
        Assert.Equal(1, session.AdvertisedPrefixCount);
        Assert.True(session.IsEstablished, "a recoverable store failure must not tear the session down");

        await TeardownAsync(session, run);
    }

    /// <summary>
    /// Control for the reordering: a healthy refresh still withdraws before it re-announces, and
    /// both halves still reach the wire in that order.
    /// </summary>
    [Fact]
    public async Task RefreshRoutes_HealthyBuild_StillWithdrawsThenReannounces()
    {
        var (session, run, conn) = await EstablishAsync(new StubRouteAssembler([TestRoute()]));
        var beforeRefresh = conn.Sent.Count;

        await session.RefreshRoutesAsync();

        var updates = conn.Sent.Skip(beforeRefresh)
            .Select(f => BgpMessageReader.ReadMessage(f))
            .OfType<BgpUpdateMessage>()
            .ToList();
        var withdrawIndex = updates.FindIndex(u => u.WithdrawnRoutes.Count > 0);
        var announceIndex = updates.FindIndex(u => u.Nlri.Count > 0);
        Assert.True(withdrawIndex >= 0, "the refresh must withdraw the previous advertisement");
        Assert.True(announceIndex > withdrawIndex, "the re-announcement must follow the withdrawal on the wire");
        Assert.Equal(1, session.AdvertisedPrefixCount);

        await TeardownAsync(session, run);
    }

    private static List<BgpUpdateMessage> Withdrawals(ScriptedConnection conn) =>
        conn.Sent.Select(f => BgpMessageReader.ReadMessage(f))
            .OfType<BgpUpdateMessage>()
            .Where(u => u.WithdrawnRoutes.Count > 0 || u.MpUnreachV6 is not null)
            .ToList();

    private static async Task TeardownAsync(BgpSession session, Task run)
    {
        session.Dispose();
        try { await run.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
    }

    /// <summary>Serves a fixed route set — the healthy control.</summary>
    private sealed class StubRouteAssembler(List<Route> routes) : IRouteAssembler
    {
        public Task<List<Route>> BuildOutboundRoutesAsync(
            string peerIp, uint remoteAsn, PeerConfig filterPeerConfig, string peerLabel, CancellationToken ct)
            => Task.FromResult(new List<Route>(routes));
    }

    /// <summary>
    /// Serves the initial dump, then fails every later build with the real SQLITE_BUSY the
    /// peer-store write raises ("database is locked", primary code 5) — the measured trigger, not a
    /// stand-in exception type.
    /// </summary>
    private sealed class FailAfterFirstBuildRouteAssembler(List<Route> routes) : IRouteAssembler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<List<Route>> BuildOutboundRoutesAsync(
            string peerIp, uint remoteAsn, PeerConfig filterPeerConfig, string peerLabel, CancellationToken ct)
            => Interlocked.Increment(ref _calls) == 1
                ? Task.FromResult(new List<Route>(routes))
                : Task.FromException<List<Route>>(new SqliteException("SQLite Error 5: 'database is locked'.", 5));
    }
}
