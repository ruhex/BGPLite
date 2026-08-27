using System.Net;
using System.Net.Sockets;
using BGPLite.Contracts;
using BGPLite.Configuration;
using BGPLite.Providers;
using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// #254: RefreshRoutesAsync must (a) treat a default CancellationToken as the session token —
/// default(CancellationToken) is CancellationToken.None, NOT _cts.Token as the old comment claimed —
/// so token-less callers (management API, onSourceChanged) get their refresh cancelled at teardown;
/// and (b) coalesce stacked refresh triggers into one in-flight cycle plus at most one pending lap,
/// instead of N sequential full withdraw+re-announce dumps.
/// </summary>
public class RefreshDebounceTests
{
    private sealed class GatedCountingStore : IPeerStore
    {
        public volatile bool Armed;
        public int LoadCalls;
        public readonly ManualResetEventSlim Release = new(false);

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
            LoadCalls++;
            if (Armed)
                Release.Wait(); // block the refresh cycle mid-flight (sync contract)
            return new PeerRoutingView("peer", [], [], [], []);
        }
    }

    private static async Task<Task> EstablishAsync(BgpSession session, Socket client, BgpConfig cfg)
    {
        var runTask = Task.Run(() => session.RunAsync(CancellationToken.None));
        var open = new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = 0,
            RouterId = 0x7F000002,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)]
        };
        var openBuf = new byte[BgpMessageWriter.GetBufferSize(open)];
        var openLen = BgpMessageWriter.WriteMessage(open, openBuf);
        client.Send(openBuf, 0, openLen, SocketFlags.None);

        using var hsCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var header = new byte[BgpConstants.MessageHeaderSize];
        await ReadExactAsync(client, header, hsCts.Token); // session OPEN
        Assert.Equal(BgpMessageType.Open, (BgpMessageType)header[18]);
        var replyLen = BgpMessageReader.GetMessageLength(header);
        if (openLen > BgpConstants.MessageHeaderSize)
            await ReadExactAsync(client, new byte[replyLen - BgpConstants.MessageHeaderSize], hsCts.Token);
        await ReadExactAsync(client, header, hsCts.Token); // session KEEPALIVE
        Assert.Equal(BgpMessageType.Keepalive, (BgpMessageType)header[18]);

        var ka = new byte[BgpMessageWriter.GetBufferSize(BgpKeepaliveMessage.Instance)];
        BgpMessageWriter.WriteMessage(BgpKeepaliveMessage.Instance, ka);
        client.Send(ka, 0, ka.Length, SocketFlags.None);

        using var estCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!session.IsEstablished && !estCts.IsCancellationRequested)
            await Task.Delay(20, estCts.Token);
        Assert.True(session.IsEstablished);
        return runTask;
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

    private static (Socket server, Socket client) ConnectedPair()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(listener.LocalEndPoint!);
        return (listener.Accept(), client);
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

    private static async Task<(BgpSession session, GatedCountingStore store, Socket client, Socket server)> NewEstablishedSessionAsync()
    {
        var (server, client) = ConnectedPair();
        var store = new GatedCountingStore();
        var cfg = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        var session = new BgpSession(
            new SocketBgpConnection(server),
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
        _ = await EstablishAsync(session, client, cfg);
        return (session, store, client, server);
    }

    [Fact]
    public async Task ConcurrentRefreshes_CoalesceIntoOneCyclePlusOneLap()
    {
        var (session, store, client, server) = await NewEstablishedSessionAsync();
        using var clientSock = client;
        using var serverSock = server;
        using var sessionH = session;

        var baseline = store.LoadCalls; // initial dump already ran during establishment
        store.Armed = true;

        try
        {
            // Task.Run is essential: until the gated Load the refresh chain has no incomplete
            // await (locks free, loopback writes complete synchronously), so running it on the
            // test thread would self-deadlock inside the gate before anyone can release it.
            var tasks = Enumerable.Range(0, 5)
                .Select(_ => Task.Run(() => session.RefreshRoutesAsync(), CancellationToken.None))
                .ToArray();
            await Task.Delay(500); // let the runner block inside the gated Load and the rest coalesce

            Assert.Equal(baseline + 1, Volatile.Read(ref store.LoadCalls)); // exactly ONE in-flight cycle

            store.Armed = false;
            store.Release.Set();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

            var total = Volatile.Read(ref store.LoadCalls) - baseline;
            Assert.InRange(total, 1, 2); // one in-flight + at most one coalesced lap — never 5
        }
        finally
        {
            store.Armed = false;
            store.Release.Set(); // never leak a blocked pool thread on an assertion failure
        }
    }

    [Fact]
    public async Task DefaultToken_IsSessionToken_DisposedSessionCancelsRefresh()
    {
        var (session, store, client, server) = await NewEstablishedSessionAsync();
        using var clientSock = client;
        using var serverSock = server;

        var baseline = Volatile.Read(ref store.LoadCalls);
        session.Dispose(); // cancels the session _cts

        // Token-less call: with the #254 normalization this observes the cancelled session token
        // and no-ops instead of running a full cycle against a disposed session.
        await session.RefreshRoutesAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(baseline, Volatile.Read(ref store.LoadCalls));
    }
}
