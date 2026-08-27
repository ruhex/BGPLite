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
            Interlocked.Increment(ref LoadCalls);
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
            // Off the test thread: until the gated Load the refresh chain has no incomplete await
            // (locks free, loopback writes complete synchronously), so calling it here would
            // self-deadlock inside the gate before anyone could release it.
            //
            // #302: LongRunning rather than Task.Run. The caller that wins the CAS parks its thread
            // inside the gated Load, and the thread pool then grows by roughly one thread per 500 ms
            // — so on a contended 2-core CI runner the remaining callers did not all reach the CAS
            // within the old 500 ms wall-clock wait. Each straggler arrived after the gate had been
            // released and _refreshRunning reset, won its own CAS, and ran a FULL cycle: the observed
            // failures were 3 and 4 loads against an expected 1-2. A dedicated thread per caller
            // removes the dependency on pool growth entirely.
            var tasks = Enumerable.Range(0, 5)
                .Select(_ => Task.Factory.StartNew(
                    () => session.RefreshRoutesAsync(),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap())
                .ToArray();

            // Wait for the state the assertions are about instead of guessing how long it takes.
            // Both halves are facts the test can observe rather than hope for:
            //   - LoadCalls == baseline + 1: the CAS winner is inside the gated Load, mid-cycle;
            //   - four tasks completed: the other four lost the CAS, set _refreshPending and
            //     returned — which is precisely "they have been coalesced", the thing the old
            //     Task.Delay was standing in for.
            await WaitForAsync(
                () => Volatile.Read(ref store.LoadCalls) == baseline + 1 && CompletedCount(tasks) == 4,
                () => $"loads={Volatile.Read(ref store.LoadCalls) - baseline} (want 1), " +
                      $"callers returned={CompletedCount(tasks)} (want 4)");

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

    private static int CompletedCount(Task[] tasks)
    {
        var completed = 0;
        foreach (var task in tasks)
            if (task.IsCompleted) completed++;
        return completed;
    }

    /// <summary>
    /// Polls until <paramref name="condition"/> holds, failing with what was actually observed once
    /// the deadline passes. A generous deadline is safe here precisely because it is never waited
    /// out on a healthy run — unlike a fixed delay, which is waited out every time and is still too
    /// short on the one run that matters (#302).
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, Func<string> observed,
        int timeoutMilliseconds = 30_000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail($"timed out after {timeoutMilliseconds} ms waiting for the refresh " +
                            $"debounce to settle — {observed()}");
            await Task.Delay(5);
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
