using System.Threading.Channels;
using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BGPLite.Tests;

/// <summary>
/// RFC 4271 §4.4: KEEPALIVE messages are exchanged between peers often enough not to cause the
/// Hold Timer to expire. The initial route dump can wait on cold sources for longer than the whole
/// negotiated hold time, so reading and KEEPALIVEs must run beside the dump, not after it. These
/// tests park the first route build — a dump waiting on a source — and drive the clock through a
/// fake <see cref="TimeProvider"/>.
/// </summary>
public class BgpSessionInitialDumpKeepAliveTests
{
    private const ushort HoldTime = 9;
    private const ushort KeepAlive = 3;

    private static Route TestRoute() => new()
    {
        Prefix = 0x0A000000,   // 10.0.0.0/8
        PrefixLength = 8,
        IsIpv4 = true,
        NextHop = 0x7F000001
    };

    /// <summary>250 isolated /24s (10.A.1.0/24) — three UPDATE batches of at most 100 NLRI, nothing to merge.</summary>
    private static List<Route> ManyRoutes() =>
        Enumerable.Range(0, 250)
            .Select(a => new Route { Prefix = 0x0A000100u | ((uint)a << 16), PrefixLength = 24, IsIpv4 = true, NextHop = 0x7F000001 })
            .ToList();

    /// <summary>
    /// Parks the first build — the dump waiting on a cold source — until released. Honors the
    /// session token like a real load does: a teardown cancels the wait.
    /// </summary>
    private sealed class ParkedRouteAssembler : IRouteAssembler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task Entered => _entered.Task;
        public bool Parked => !_release.Task.IsCompleted;
        public void Release() => _release.TrySetResult();

        public async Task<List<Route>> BuildOutboundRoutesAsync(
            string peerIp, uint remoteAsn, PeerConfig filterPeerConfig, string peerLabel, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(ct);
            }
            return [TestRoute()];
        }
    }

    /// <summary>Serves a fixed multi-batch route set.</summary>
    private sealed class FixedRouteAssembler(List<Route> routes) : IRouteAssembler
    {
        public Task<List<Route>> BuildOutboundRoutesAsync(
            string peerIp, uint remoteAsn, PeerConfig filterPeerConfig, string peerLabel, CancellationToken ct)
            => Task.FromResult(routes);
    }

    /// <summary>
    /// A scripted connection that records outbound frames, can fail a number of writes, and can park
    /// chosen writes (1-based ordinal) on a gate — a slow peer that accepts one UPDATE and then
    /// stops reading, which leaves the dump holding the session's send lock.
    /// </summary>
    private sealed class ScriptedGateConnection : IBgpConnection
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private readonly Queue<byte> _readBuffer = new();
        private readonly List<byte[]> _sent = [];
        private readonly Dictionary<int, TaskCompletionSource> _gates = new();
        private readonly Dictionary<int, TaskCompletionSource> _parked = new();
        private int _writes;

        public ScriptedGateConnection(params int[] parkedWrites)
        {
            foreach (var ordinal in parkedWrites)
            {
                _gates[ordinal] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _parked[ordinal] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public int FailNextWrites { get; set; }
        public IReadOnlyList<byte[]> Sent { get { lock (_sent) return [.. _sent]; } }
        public Task Parked(int ordinal) => _parked[ordinal].Task;
        public void Release(int ordinal) => _gates[ordinal].TrySetResult();

        public void EnqueueMessage(BgpMessage message)
        {
            var buf = new byte[BgpMessageWriter.GetBufferSize(message)];
            var n = BgpMessageWriter.WriteMessage(message, buf);
            _inbound.Writer.TryWrite(buf[..n]);
        }

        public async ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                while (_readBuffer.Count > 0 && offset < buffer.Length)
                    buffer.Span[offset++] = _readBuffer.Dequeue();
                if (offset >= buffer.Length) break;
                byte[] chunk;
                try { chunk = await _inbound.Reader.ReadAsync(cancellationToken); }
                catch (ChannelClosedException) { throw new IOException("Connection closed by peer"); }
                for (var i = 0; i < chunk.Length; i++)
                {
                    if (offset < buffer.Length)
                        buffer.Span[offset++] = chunk[i];
                    else
                        _readBuffer.Enqueue(chunk[i]);
                }
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            var ordinal = Interlocked.Increment(ref _writes);
            lock (_sent) _sent.Add(buffer.ToArray());
            if (FailNextWrites > 0)
            {
                FailNextWrites--;
                return ValueTask.FromException(new IOException("Connection reset by peer"));
            }
            if (_gates.TryGetValue(ordinal, out var gate))
            {
                _parked[ordinal].TrySetResult();
                return new ValueTask(gate.Task);
            }
            return default;
        }

        public bool IsPeerClosed => false;
        public void Dispose()
        {
            _inbound.Writer.TryComplete();
            foreach (var gate in _gates.Values)
                gate.TrySetCanceled();   // like the real transport: Dispose aborts a pending write
        }
    }

    private sealed record Harness(BgpSession Session, Task Run, ScriptedConnection Conn, FakeTimeProvider Time);

    private static BgpSession NewSession(IRouteAssembler assembler, IBgpConnection conn, TimeProvider time, ushort holdTime = HoldTime) => new(
        conn,
        new PeerConfig { Address = "127.0.0.1" },
        new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = holdTime, KeepAlive = KeepAlive },
        new RouteTable(),
        AllowAllFilter.Instance,
        new BgpMetrics(),
        NullLogger<BgpSession>.Instance,
        routeAssembler: assembler,
        timeProvider: time);

    private static BgpOpenMessage PeerOpen(ushort holdTime = HoldTime) => new()
    {
        Version = BgpConstants.BgpVersion,
        Asn = 65002,
        HoldTime = holdTime,
        RouterId = 0x0A000002,
        Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)]
    };

    private static Harness Start(IRouteAssembler assembler)
    {
        var time = new FakeTimeProvider();
        var conn = new ScriptedConnection();
        var session = NewSession(assembler, conn, time);
        var run = session.RunAsync();
        conn.EnqueueMessage(PeerOpen());
        conn.EnqueueMessage(BgpKeepaliveMessage.Instance);
        return new Harness(session, run, conn, time);
    }

    /// <summary>
    /// Advances the fake clock by one keepalive interval at a time until <paramref name="condition"/>
    /// holds — after each step the timer loop gets real time to observe the tick. Condition-based
    /// rather than a fixed delay: on a loaded runner a tick's continuation is not scheduled within
    /// any particular number of milliseconds.
    /// </summary>
    private static async Task TickUntilAsync(FakeTimeProvider time, Func<bool> condition, string what,
        Action? beforeTick = null, int maxTicks = 12)
    {
        for (var tick = 1; tick <= maxTicks; tick++)
        {
            beforeTick?.Invoke();
            time.Advance(TimeSpan.FromSeconds(KeepAlive));
            var deadline = Environment.TickCount64 + 2000;
            while (!condition() && Environment.TickCount64 < deadline)
                await Task.Delay(10);
            if (condition())
                return;
        }
        Assert.Fail($"condition not met after {maxTicks} keepalive intervals: {what}");
    }

    private static IEnumerable<BgpMessage> MessagesAfter(ScriptedConnection conn, int from) =>
        conn.Sent.Skip(from).Select(f => BgpMessageReader.ReadMessage(f));

    private static int KeepAlivesAfter(ScriptedConnection conn, int from) =>
        MessagesAfter(conn, from).OfType<BgpKeepaliveMessage>().Count();

    private static bool HasHoldTimerExpiredAfter(ScriptedConnection conn, int from) =>
        MessagesAfter(conn, from).OfType<BgpNotificationMessage>()
            .Any(n => n.ErrorCode == BgpConstants.Error.HoldTimerExpired);

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
        session.MarkSilentClose();
        session.Dispose();
        try { await run.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
    }

    private static async Task<(Harness h, int handshake, ParkedRouteAssembler assembler)> ParkedAsync()
    {
        var assembler = new ParkedRouteAssembler();
        var h = Start(assembler);
        await WaitForAsync(() => h.Session.IsEstablished, "Established");
        await WaitForAsync(() => assembler.Entered.IsCompleted, "the initial build to start");
        return (h, h.Conn.Sent.Count, assembler);
    }

    [Fact]
    public async Task InitialDump_WaitingOnSources_KeepalivesKeepFlowing()
    {
        var (h, handshake, assembler) = await ParkedAsync();
        try
        {
            // Two keepalive intervals pass while the build is still parked on its source. The peer
            // keeps the session alive meanwhile, so extra ticks on a slow runner cannot expire it.
            void peerKeepsAlive() => h.Conn.EnqueueMessage(BgpKeepaliveMessage.Instance);
            await TickUntilAsync(h.Time, () => KeepAlivesAfter(h.Conn, handshake) >= 1, "the first KEEPALIVE", peerKeepsAlive);
            await TickUntilAsync(h.Time, () => KeepAlivesAfter(h.Conn, handshake) >= 2, "the second KEEPALIVE", peerKeepsAlive);

            Assert.True(assembler.Parked, "the build must still be waiting on the source");
            Assert.True(h.Session.IsEstablished);
            Assert.DoesNotContain(MessagesAfter(h.Conn, handshake), m => m is BgpUpdateMessage);

            // The source answers: the dump is delivered as usual.
            assembler.Release();
            await WaitForAsync(() => h.Session.AdvertisedPrefixCount == 1, "the dump's routes on the wire");
            Assert.Contains(MessagesAfter(h.Conn, handshake), m => m is BgpUpdateMessage);
        }
        finally
        {
            assembler.Release();
            await TeardownAsync(h.Session, h.Run);
        }
    }

    [Fact]
    public async Task InitialDump_WaitingOnSources_SilentPeer_HoldTimerExpiresDuringTheDump()
    {
        var (h, handshake, assembler) = await ParkedAsync();
        try
        {
            // The peer goes silent: past the hold time the session must not survive on the dump's account.
            await TickUntilAsync(h.Time, () => HasHoldTimerExpiredAfter(h.Conn, handshake),
                "the Hold Timer Expired NOTIFICATION (RFC 4271 §6.5)");

            await h.Run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(BgpFsmState.Established, h.Session.State);
        }
        finally
        {
            assembler.Release();
            await TeardownAsync(h.Session, h.Run);
        }
    }

    [Fact]
    public async Task InitialDump_WaitingOnSources_PeerKeepalivesPreventFalseExpiry()
    {
        var (h, handshake, assembler) = await ParkedAsync();
        try
        {
            // Twice the hold time with the peer keeping the session alive: no false expiry from
            // KEEPALIVEs sitting unread behind the dump.
            for (var i = 0; i < 6; i++)
            {
                h.Conn.EnqueueMessage(BgpKeepaliveMessage.Instance);
                h.Time.Advance(TimeSpan.FromSeconds(KeepAlive));
                await Task.Delay(20);
            }
            await Task.Delay(50);   // let the last tick's hold check run before asserting on it

            Assert.False(HasHoldTimerExpiredAfter(h.Conn, handshake));
            Assert.True(h.Session.IsEstablished);

            assembler.Release();
            await WaitForAsync(() => h.Session.AdvertisedPrefixCount == 1, "the dump's routes on the wire");
        }
        finally
        {
            assembler.Release();
            await TeardownAsync(h.Session, h.Run);
        }
    }

    [Fact]
    public async Task InitialDump_WaitingOnSources_PeerNotificationEndsTheSessionPromptly()
    {
        var (h, handshake, assembler) = await ParkedAsync();
        try
        {
            // RFC 4271 §6.3: a NOTIFICATION ends the session at once — it must not wait behind the dump.
            h.Conn.EnqueueMessage(new BgpNotificationMessage
            {
                ErrorCode = BgpConstants.Error.Cease,
                SubErrorCode = BgpConstants.SubError.Unspecific
            });

            await h.Run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(BgpFsmState.Idle, h.Session.State);
            Assert.DoesNotContain(MessagesAfter(h.Conn, handshake), m => m is BgpNotificationMessage); // no reply to a NOTIFICATION (§8.1)
            Assert.DoesNotContain(MessagesAfter(h.Conn, handshake), m => m is BgpUpdateMessage);      // the cancelled dump announced nothing
        }
        finally
        {
            assembler.Release();
            await TeardownAsync(h.Session, h.Run);
        }
    }

    /// <summary>
    /// Hold time 0 disables KEEPALIVEs (RFC 4271 §4.2) but not reading: a peer NOTIFICATION still
    /// ends the session while the dump waits.
    /// </summary>
    [Fact]
    public async Task InitialDump_WaitingOnSources_HoldTimeZero_PeerNotificationEndsTheSession()
    {
        var assembler = new ParkedRouteAssembler();
        var conn = new ScriptedConnection();
        var session = NewSession(assembler, conn, new FakeTimeProvider(), holdTime: 0);
        var run = session.RunAsync();
        conn.EnqueueMessage(PeerOpen(holdTime: 0));
        conn.EnqueueMessage(BgpKeepaliveMessage.Instance);

        await WaitForAsync(() => session.IsEstablished, "Established");
        await WaitForAsync(() => assembler.Entered.IsCompleted, "the initial build to start");
        var handshake = conn.Sent.Count;

        conn.EnqueueMessage(new BgpNotificationMessage
        {
            ErrorCode = BgpConstants.Error.Cease,
            SubErrorCode = BgpConstants.SubError.Unspecific
        });

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BgpFsmState.Idle, session.State);
        Assert.DoesNotContain(MessagesAfter(conn, handshake), m => m is BgpKeepaliveMessage or BgpUpdateMessage or BgpNotificationMessage);
        assembler.Release();
        session.Dispose();
    }

    /// <summary>
    /// A dump that fails on the wire while its loops are already running ends the session through
    /// the usual teardown: the loops are stopped, exactly one Cease follows, and nothing is sent after.
    /// </summary>
    [Fact]
    public async Task InitialDump_SendFault_StopsTheLoops_AndSendsOneCease()
    {
        var assembler = new ParkedRouteAssembler();
        var time = new FakeTimeProvider();
        var conn = new ScriptedGateConnection();
        var session = NewSession(assembler, conn, time);
        var run = session.RunAsync();
        conn.EnqueueMessage(PeerOpen());
        conn.EnqueueMessage(BgpKeepaliveMessage.Instance);

        await WaitForAsync(() => session.IsEstablished, "Established");
        await WaitForAsync(() => assembler.Entered.IsCompleted, "the initial build to start");
        var handshake = conn.Sent.Count;

        conn.FailNextWrites = 1;   // the peer went away while the dump waited on its source
        assembler.Release();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        var after = conn.Sent.Skip(handshake).Select(f => BgpMessageReader.ReadMessage(f)).ToList();
        Assert.NotEqual(BgpFsmState.Established, session.State);
        var notification = Assert.Single(after.OfType<BgpNotificationMessage>());
        Assert.Equal(BgpConstants.Error.Cease, notification.ErrorCode);
        Assert.Equal(handshake + 2, conn.Sent.Count);   // the failed UPDATE attempt + the Cease, nothing else

        // The keepalive loop is gone: later ticks send nothing.
        time.Advance(TimeSpan.FromSeconds(KeepAlive * 4));
        await Task.Delay(50);
        Assert.Equal(handshake + 2, conn.Sent.Count);
        session.Dispose();
    }

    /// <summary>
    /// KEEPALIVEs interleave with a dump that is still sending: one lands between two UPDATE
    /// batches, and every frame on the wire is intact.
    /// </summary>
    [Fact]
    public async Task InitialDump_SendingInBatches_KeepaliveInterleavesWithoutCorruptingFrames()
    {
        // Writes 1-2 are the handshake (OPEN, KEEPALIVE); 3-5 are the dump's three UPDATEs.
        var conn = new ScriptedGateConnection(3, 4);
        var time = new FakeTimeProvider();
        var session = NewSession(new FixedRouteAssembler(ManyRoutes()), conn, time);
        var run = session.RunAsync();
        conn.EnqueueMessage(PeerOpen());
        conn.EnqueueMessage(BgpKeepaliveMessage.Instance);

        await WaitForAsync(() => session.IsEstablished, "Established");
        await conn.Parked(3).WaitAsync(TimeSpan.FromSeconds(5));   // the dump holds the send lock inside its first UPDATE

        // A tick while the lock is held: the KEEPALIVE queues on the send lock behind the dump.
        time.Advance(TimeSpan.FromSeconds(KeepAlive));
        await Task.Delay(50);
        conn.Release(3);
        await conn.Parked(4).WaitAsync(TimeSpan.FromSeconds(5));   // the dump is inside its second UPDATE
        // A second tick: the loop is still parked on the first KEEPALIVE or queues another —
        // either way a KEEPALIVE is waiting for the lock when the dump releases it.
        time.Advance(TimeSpan.FromSeconds(KeepAlive));
        await Task.Delay(50);
        conn.Release(4);

        await WaitForAsync(() => session.AdvertisedPrefixCount == 250, "the whole dump on the wire");
        var frames = conn.Sent.Select(f => BgpMessageReader.ReadMessage(f)).ToList();   // every frame decodes
        var types = frames.Select(m => m.Type).ToList();
        // The three NLRI batches; a trailing empty UPDATE is the End-of-RIB marker.
        var updates = Enumerable.Range(0, types.Count)
            .Where(i => frames[i] is BgpUpdateMessage { Nlri.Count: > 0 })
            .ToList();
        Assert.Equal(3, updates.Count);
        // Whichever batch the KEEPALIVE queued behind, it lands before the dump is finished —
        // it never waits for the whole dump.
        var interleaved = Enumerable.Range(updates[0] + 1, updates[^1] - updates[0] - 1)
            .Count(i => types[i] == BgpMessageType.Keepalive);
        Assert.True(interleaved >= 1, "a KEEPALIVE must land between the dump's UPDATE batches");

        await TeardownAsync(session, run);
    }

    /// <summary>
    /// The read loop enforces the per-peer prefix ceiling (RFC 4271 §6.7, RFC 4486 §2) on every
    /// inbound UPDATE. The dump refreshes that ceiling too, but it runs beside the loops: an
    /// UPDATE read before the lookup returned was checked against the global default instead.
    /// </summary>
    [Fact]
    public async Task InboundUpdateDuringTheInitialDump_IsCheckedAgainstThePerPeerLimit()
    {
        // The per-peer limit is still being read from the store while the session comes up.
        var perPeerLimit = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var conn = new ScriptedConnection();
        var routeTable = new RouteTable();
        var assembler = new ParkedRouteAssembler();
        var session = new BgpSession(
            conn,
            new PeerConfig { Address = "127.0.0.1" },
            new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0, MaxPrefixesPerPeer = 100 },
            routeTable,
            AllowAllFilter.Instance,
            new BgpMetrics(),
            NullLogger<BgpSession>.Instance,
            peerStore: new LimitStore(perPeerLimit.Task),
            routeAssembler: assembler);
        var run = session.RunAsync();
        conn.EnqueueMessage(PeerOpen(holdTime: 0));
        conn.EnqueueMessage(BgpKeepaliveMessage.Instance);
        EnqueueAnnouncement(conn, "10.1.0.0/24", "10.2.0.0/24");
        await WaitForAsync(() => session.IsEstablished, "Established");

        // A read loop that started before the limit is known takes the UPDATE under the global
        // default of 100 and installs both prefixes. Give it every chance to, then answer: 1.
        var settle = Environment.TickCount64 + 1000;
        while (routeTable.Count < 2 && Environment.TickCount64 < settle)
            await Task.Delay(10);
        perPeerLimit.SetResult(1);

        // The second prefix exceeds the limit: the session is reset with Cease/MaxPrefixesExceeded
        // (RFC 4486 §2), exactly as it would be by an UPDATE after the dump.
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        var reset = Assert.Single(conn.Sent.Select(f => BgpMessageReader.ReadMessage(f)).OfType<BgpNotificationMessage>());
        Assert.Equal(BgpConstants.Error.Cease, reset.ErrorCode);
        Assert.Equal(BgpConstants.SubError.CeaseMaxPrefixes, reset.SubErrorCode);
        assembler.Release();
        session.Dispose();
    }

    /// <summary>
    /// RFC 4271 §4.5: a NOTIFICATION ends the session — no UPDATE may follow it on the wire. A
    /// loop can decide to send one while the initial dump is still sending batches.
    /// </summary>
    [Fact]
    public async Task LoopNotificationDuringTheInitialDump_NoUpdateFollowsIt()
    {
        // Writes 1-2 are the handshake (OPEN, KEEPALIVE); 3-5 are the dump's three UPDATEs.
        var conn = new ScriptedGateConnection(3, 4, 5);
        var session = NewSession(new FixedRouteAssembler(ManyRoutes()), conn, new FakeTimeProvider());
        var run = session.RunAsync();
        conn.EnqueueMessage(PeerOpen());
        conn.EnqueueMessage(BgpKeepaliveMessage.Instance);
        await WaitForAsync(() => session.IsEstablished, "Established");
        await conn.Parked(3).WaitAsync(TimeSpan.FromSeconds(5));   // the dump is inside its first UPDATE
        try
        {
            // An OPEN in Established is an FSM error: the read loop answers with NOTIFICATION 5/0
            // while the dump still holds two batches.
            conn.EnqueueMessage(PeerOpen());

            // The loop cancels the dump before it queues its NOTIFICATION — the probe says so, and
            // the parked batch is released right there. Nothing else releases it in time: a loop
            // that skips the stop has to be kicked by hand, and its remaining UPDATEs then land
            // after the NOTIFICATION on their own.
            var stopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.InitialDumpStopRequestedProbe = () => { stopRequested.TrySetResult(); conn.Release(3); };
            await Task.WhenAny(stopRequested.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            conn.Release(3);

            var notification = WaitForAsync(() => Notifications(conn).Count == 1, "the NOTIFICATION");
            await Task.WhenAny(notification, conn.Parked(4));   // a batch that slipped in before the cancel
            conn.Release(4);
            conn.Release(5);
            await notification;
            await run.WaitAsync(TimeSpan.FromSeconds(5));

            // The NOTIFICATION is the last frame on the wire.
            var last = Assert.IsType<BgpNotificationMessage>(BgpMessageReader.ReadMessage(conn.Sent[^1]));
            Assert.Equal(BgpConstants.Error.FiniteStateMachineError, last.ErrorCode);
        }
        finally
        {
            await TeardownAsync(session, run);
        }
    }

    private static List<BgpNotificationMessage> Notifications(IBgpConnection conn) =>
        ((ScriptedGateConnection)conn).Sent.Select(f => BgpMessageReader.ReadMessage(f)).OfType<BgpNotificationMessage>().ToList();

    /// <summary>Every member inert except the per-peer limit, which the test answers when it chooses.</summary>
    private sealed class LimitStore(Task<int?> limit) : IPeerStore
    {
        public Task<string> CreatePeerAsync(string ip, uint asn, string? description, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task UpsertPeerAsync(string ip, uint asn, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateSessionStatusAsync(string ip, uint asn, bool active, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PeerRoutingView?> LoadPeerRoutingViewAsync(string ip, uint asn, CancellationToken ct = default) => Task.FromResult<PeerRoutingView?>(null);
        public Task<int?> GetPeerMaxPrefixAsync(string ip, uint asn, CancellationToken ct = default) => limit;
    }

    /// <summary>A valid inbound IPv4 announcement — ORIGIN, AS_PATH and NEXT_HOP included.</summary>
    private static void EnqueueAnnouncement(ScriptedConnection conn, params string[] cidrs)
    {
        var nlri = cidrs.Select(cidr => PrefixCidr.TryParse(cidr, out var prefix) ? prefix : throw new FormatException(cidr)).ToList();
        conn.EnqueueMessage(new BgpUpdateMessage
        {
            Nlri = nlri,
            PathAttributes = UpdateCodec.BuildUpdateAttributes(65002, localFourByteAsn: true, nextHop: 0x0A000001, [])
        });
    }
}
