using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// The initial route dump is the session's first advertisement build and owns
/// <c>_advertisedPrefixesLock</c> for its whole duration. A refresh requested while that dump is
/// running can come from the dump itself: a source load that changed content awaits
/// <c>onSourceChanged</c>, which fans out to every established session — this one included. Such a
/// request must not start a cycle, because the cycle would wait for the lock its own caller holds
/// forever. It has to return promptly and be served once the dump has released the lock.
/// </summary>
public class BgpSessionInitialDumpRefreshTests
{
    private static Route TestRoute() => new()
    {
        Prefix = 0x0A000000,   // 10.0.0.0/8
        PrefixLength = 8,
        IsIpv4 = true,
        NextHop = 0x7F000001
    };

    /// <summary>
    /// On the first build — the initial dump — asks the session to refresh <paramref name="requests"/>
    /// times and awaits each call, exactly as an awaited convergence callback would. A call that
    /// does not return in time is recorded as stuck and abandoned, so a regression fails on an
    /// assertion instead of hanging the runner.
    /// </summary>
    private sealed class SelfRefreshingRouteAssembler(int requests) : IRouteAssembler
    {
        private int _calls;
        private int _returned;

        public BgpSession? Session { get; set; }
        public int Calls => Volatile.Read(ref _calls);
        public int ReturnedRequests => Volatile.Read(ref _returned);

        public async Task<List<Route>> BuildOutboundRoutesAsync(
            string peerIp, uint remoteAsn, PeerConfig filterPeerConfig, string peerLabel, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1 && Session is not null)
            {
                for (var i = 0; i < requests; i++)
                {
                    try
                    {
                        await Session.RefreshRoutesAsync().WaitAsync(TimeSpan.FromSeconds(3), ct);
                        Interlocked.Increment(ref _returned);
                    }
                    catch (TimeoutException)
                    {
                        // The request is waiting for the lock this build still holds.
                    }
                }
            }
            return [TestRoute()];
        }
    }

    /// <summary>Serves a fixed route set — the control.</summary>
    private sealed class StubRouteAssembler : IRouteAssembler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<List<Route>> BuildOutboundRoutesAsync(
            string peerIp, uint remoteAsn, PeerConfig filterPeerConfig, string peerLabel, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new List<Route> { TestRoute() });
        }
    }

    /// <summary>Starts the handshake on a scripted connection; returns before the initial dump is awaited by anyone.
    /// <paramref name="beforeRun"/> runs after the session exists and before the first word of the
    /// handshake — a worker can enter the first build the moment RunAsync starts.</summary>
    private static (BgpSession Session, Task Run, ScriptedConnection Conn) StartSession(
        IRouteAssembler assembler, Action<BgpSession>? beforeRun = null)
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
        beforeRun?.Invoke(session);

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
        return (session, run, conn);
    }

    private static List<BgpUpdateMessage> Updates(ScriptedConnection conn) =>
        conn.Sent.Select(f => BgpMessageReader.ReadMessage(f))
            .OfType<BgpUpdateMessage>()
            .ToList();

    private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMilliseconds = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail($"timed out after {timeoutMilliseconds} ms waiting for {what}");
            await Task.Delay(10);
        }
    }

    private static async Task TeardownAsync(BgpSession session, Task run)
    {
        session.Dispose();
        try { await run.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
    }

    [Fact]
    public async Task RefreshRequestedFromInsideTheInitialDump_ReturnsPromptly_AndIsServedAfterTheDump()
    {
        // The assembler learns the session before the handshake starts: the first build must not
        // be able to begin while Session is still unset.
        var assembler = new SelfRefreshingRouteAssembler(requests: 1);
        var (session, run, conn) = StartSession(assembler, s => assembler.Session = s);
        try
        {
            await WaitForAsync(() => session.AdvertisedPrefixCount == 1, "the initial dump's routes on the wire");

            // The request was answered while the dump still owned the lock — it did not wait for
            // the dump to finish, and the dump did not wait for the request.
            Assert.True(session.IsEstablished, "the session must stay Established");
            Assert.Equal(1, assembler.ReturnedRequests);

            // ... and it was deferred, not dropped: exactly one lap runs after the dump.
            await WaitForAsync(
                () => Updates(conn).Count(u => u.Nlri.Count > 0) == 2 && Updates(conn).Count(u => u.WithdrawnRoutes.Count > 0) == 1,
                "the deferred lap's withdraw + re-announce");
            await WaitForAsync(() => assembler.Calls == 2, "the deferred lap's build");
            // Nothing more is served: once the lap has left the refresh slot idle, no later lap
            // can start on its own.
            await WaitForAsync(() => !session.RefreshInProgress, "the deferred lap to finish");
            Assert.Equal(2, assembler.Calls);
            Assert.Equal(1, session.AdvertisedPrefixCount);
            Assert.True(session.IsEstablished, "a deferred refresh must not tear the session down");

            // The lap cannot overtake the dump: the initial announcement is on the wire first.
            var updates = Updates(conn);
            var firstAnnounce = updates.FindIndex(u => u.Nlri.Count > 0);
            var withdraw = updates.FindIndex(u => u.WithdrawnRoutes.Count > 0);
            Assert.True(firstAnnounce >= 0, "the initial dump must announce");
            Assert.True(withdraw > firstAnnounce, "the deferred lap withdraws only after the dump announced");
        }
        finally
        {
            await TeardownAsync(session, run);
        }
    }

    [Fact]
    public async Task RefreshesRequestedDuringTheInitialDump_CoalesceIntoOneDeferredLap()
    {
        var assembler = new SelfRefreshingRouteAssembler(requests: 3);
        var (session, run, conn) = StartSession(assembler, s => assembler.Session = s);
        try
        {
            await WaitForAsync(() => assembler.Calls >= 2 && Updates(conn).Count(u => u.Nlri.Count > 0) == 2,
                "the dump and its deferred lap");
            await WaitForAsync(() => !session.RefreshInProgress, "the deferred lap to finish");

            Assert.Equal(3, assembler.ReturnedRequests);
            Assert.Equal(2, assembler.Calls);   // the dump + ONE lap for all three requests
            Assert.Equal(1, Updates(conn).Count(u => u.WithdrawnRoutes.Count > 0));
            Assert.True(session.IsEstablished);
        }
        finally
        {
            await TeardownAsync(session, run);
        }
    }

    /// <summary>
    /// Control: once the dump is done the slot is free again — a later request runs its own cycle
    /// before returning, as it always did.
    /// </summary>
    [Fact]
    public async Task RefreshAfterTheInitialDump_StillRunsItsCycleBeforeReturning()
    {
        var assembler = new StubRouteAssembler();
        var (session, run, conn) = StartSession(assembler);
        try
        {
            await WaitForAsync(() => session.AdvertisedPrefixCount == 1, "the initial dump's routes on the wire");
            await session.InitialDumpCompleted.WaitAsync(TimeSpan.FromSeconds(5));   // slot released — a request now runs inline

            await session.RefreshRoutesAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, assembler.Calls);
            Assert.Equal(1, Updates(conn).Count(u => u.WithdrawnRoutes.Count > 0));
            Assert.Equal(1, session.AdvertisedPrefixCount);
        }
        finally
        {
            await TeardownAsync(session, run);
        }
    }
}
