using System.Net;
using System.Net.Sockets;
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
/// #253: ROUTE_REFRESH must be handled OFF the read loop. A refresh that blocks (cold TTL cache /
/// slow RIPEstat / slow store) used to starve the loop: the peer's KEEPALIVEs sat unread and a
/// completely live session was killed by a false Hold Timer Expired. This test hangs the refresh
/// mid-flight on a gated IPeerStore, keeps sending KEEPALIVEs, and asserts the session survives
/// past the negotiated hold time.
/// </summary>
public class RouteRefreshHoldTimerTests
{
    private sealed class GatedStore : IPeerStore
    {
        public volatile bool Armed;
        public readonly ManualResetEventSlim Release = new(false);
        /// <summary>Completes when a gated Load has actually ENTERED the block — the test waits
        /// for it before measuring liveness, so a delayed ROUTE_REFRESH cannot pass vacuously.</summary>
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string CreatePeer(string ip, uint asn, string? description) => "peer";
        public void UpsertPeer(string ip, uint asn) { }
        public void UpdateSessionStatus(string ip, uint asn, bool active) { }
        public void DeletePeer(string id) { }
        public PeerInfo? GetPeerByIp(string ip) => null;
        public PeerInfo? GetPeer(string ip, uint asn) => null;
        public PeerInfo? GetPeerById(string id) => null;
        public List<string> GetSubscriptions(string peerId) => [];
        public List<string> GetCustomPrefixes(string peerId) => [];
        public List<uint> GetCustomAsns(string peerId) => [];
        public HashSet<uint> GetCommunities(string peerId) => [];
        public HashSet<uint> GetCommunities(string ip, uint asn) => [];
        public void SetCommunities(string peerId, HashSet<uint> communities) { }
        public void ClearCommunities(string peerId) { }
        public void SetDescription(string id, string description) { }

        public PeerRoutingView? LoadPeerRoutingView(string ip, uint asn)
        {
            if (Armed)
            {
                Entered.TrySetResult();
                Release.Wait(); // the refresh hangs here, mid-flight
            }
            return new PeerRoutingView("peer", [], [], [], []);
        }
    }

    private sealed class EmptyPrefixService : IPrefixService
    {
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetPrefixesAsync(uint asn, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task<List<(uint Prefix, byte Length, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default) => Task.FromResult(new List<(uint, byte, uint)>());
        public Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default) => Task.FromResult(0);
        public Task<List<(uint Prefix, byte Length, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default) => Task.FromResult(new List<(uint, byte, uint)>());
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetUserSourcePrefixesAsync(string name, string url, string? community, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NopLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel l, EventId e, TState s, Exception? x, Func<TState, Exception?, string> f) { }
    }

    private static async Task ReadExactAsync(Socket s, byte[] buf, CancellationToken ct)
    {
        var total = 0;
        while (total < buf.Length)
        {
            var n = await s.ReceiveAsync(buf.AsMemory(total), ct);
            if (n == 0) throw new IOException("closed");
            total += n;
        }
    }

    [Fact]
    public async Task RouteRefresh_BlockedRefresh_DoesNotStarveHoldTimer()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(listener.LocalEndPoint!);
        using var serverSock = listener.Accept();

        var store = new GatedStore();
        var cfg = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 3, KeepAlive = 1 };
        using var session = new BgpSession(
            new SocketBgpConnection(serverSock),
            new PeerConfig { Address = "127.0.0.1" },
            cfg,
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            new NopLogger<BgpSession>(),
            peerStore: store,
            // #263: the assembler is injected now, so the test supplies the same store it asserts on.
            routeAssembler: new RouteAssembler(
                new EmptyPrefixService(), store, NullCommunityResolver.Instance,
                AllowAllFilter.Instance, new AppConfig(), cfg,
                NullLogger<RouteAssembler>.Instance));
        var runTask = Task.Run(() => session.RunAsync(CancellationToken.None));

        // Handshake with the RouteRefresh capability negotiated (otherwise the message is ignored).
        var open = new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = 3,
            RouterId = 0x7F000002,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002), BgpCapabilityInfo.RouteRefresh()]
        };
        var openBuf = new byte[BgpMessageWriter.GetBufferSize(open)];
        var openLen = BgpMessageWriter.WriteMessage(open, openBuf);
        client.Send(openBuf, 0, openLen, SocketFlags.None);

        using var hsCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var header = new byte[BgpConstants.MessageHeaderSize];
        await ReadExactAsync(client, header, hsCts.Token);
        Assert.Equal(BgpMessageType.Open, (BgpMessageType)header[18]);
        var replyLen = BgpMessageReader.GetMessageLength(header);
        if (replyLen > BgpConstants.MessageHeaderSize)
            await ReadExactAsync(client, new byte[replyLen - BgpConstants.MessageHeaderSize], hsCts.Token);
        await ReadExactAsync(client, header, hsCts.Token);
        Assert.Equal(BgpMessageType.Keepalive, (BgpMessageType)header[18]);

        var ka = new byte[BgpMessageWriter.GetBufferSize(BgpKeepaliveMessage.Instance)];
        BgpMessageWriter.WriteMessage(BgpKeepaliveMessage.Instance, ka);
        client.Send(ka, 0, ka.Length, SocketFlags.None);

        using var estCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!session.IsEstablished && !estCts.IsCancellationRequested)
            await Task.Delay(20, estCts.Token);
        Assert.True(session.IsEstablished);

        // Wait for the initial dump to FINISH: with an empty routing view it emits no UPDATEs but
        // does send the End-of-RIB marker. Reading it guarantees the read/keepalive loops are
        // running BEFORE the gate is armed — otherwise the gate would block the initial dump
        // itself (before the loops start) and the test would pass vacuously.
        using var eorCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ReadExactAsync(client, header, eorCts.Token);
        Assert.Equal(BgpMessageType.Update, (BgpMessageType)header[18]);
        var eorLen = BgpMessageReader.GetMessageLength(header);
        if (eorLen > BgpConstants.MessageHeaderSize)
            await ReadExactAsync(client, new byte[eorLen - BgpConstants.MessageHeaderSize], eorCts.Token);

        // Arm the gate: the next LoadPeerRoutingView (the refresh's) blocks until released.
        store.Armed = true;

        var rr = new BgpRouteRefreshMessage { Afi = BgpConstants.Afi.IPv4, Reserved = 0, Safi = BgpConstants.Safi.Unicast };
        var rrBuf = new byte[BgpMessageWriter.GetBufferSize(rr)];
        var rrLen = BgpMessageWriter.WriteMessage(rr, rrBuf);
        client.Send(rrBuf, 0, rrLen, SocketFlags.None);

        // The refresh must actually reach the gate before liveness is measured (CodeRabbit #281):
        // if ROUTE_REFRESH processing were delayed, the gate would never engage and the KEEPALIVE
        // assertions would pass without exercising the blocked-refresh path.
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Keep the session's hold timer fed with KEEPALIVEs while the refresh hangs, and collect
        // everything the session sends back. Hold time is 3s — surviving to 5.5s without a Hold
        // Timer Expired NOTIFICATION proves the read loop kept reading. (IsEstablished alone is
        // NOT a valid observable: the FSM flag survives a hold expiry — the wire is.)
        var received = new System.Collections.Concurrent.ConcurrentQueue<BgpMessage>();
        var collector = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var hdr = new byte[BgpConstants.MessageHeaderSize];
                    var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await ReadExactAsync(client, hdr, readCts.Token);
                    var len = BgpMessageReader.GetMessageLength(hdr);
                    var frame = new byte[len];
                    hdr.CopyTo(frame, 0);
                    if (len > BgpConstants.MessageHeaderSize)
                        await ReadExactAsync(client, frame[BgpConstants.MessageHeaderSize..], readCts.Token);
                    if (frame[18] == (byte)BgpMessageType.Notification)
                        Console.WriteLine("RAW: " + Convert.ToHexString(frame));
                    received.Enqueue(BgpMessageReader.ReadMessage(frame));
                }
            }
            catch (Exception) { /* socket closed at teardown — collector ends */ }
        });

        // Feed KEEPALIVEs across an 8s window (hold time is 3s, session keepalive interval 1s).
        // Discriminator: a starved read loop (the bug) freezes _lastReceivedTicks, the hold
        // expires at ~3s and the keepalive loop emits at most ~4; a live read loop emits ~7.
        // (The FSM flag and notification codes are NOT usable observables here — wire volume is.)
        for (var t = 0; t < 8000; t += 500)
        {
            client.Send(ka, 0, ka.Length, SocketFlags.None);
            await Task.Delay(500);
        }
        await Task.Delay(1200); // let the last session keepalive (1s interval) land

        Assert.True(session.IsEstablished, "session died while the refresh hung");
        var sessionKeepalives = received.Count(m => m is BgpKeepaliveMessage);
        Assert.True(sessionKeepalives >= 5,
            $"only {sessionKeepalives} session KEEPALIVEs in 8s — the read loop was starved by the refresh (hold time 3s caps the keepalive loop at ~4; a live loop emits ~7)");

        // Release the refresh and wind down cleanly.
        store.Armed = false;
        store.Release.Set();
        session.Dispose();
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
