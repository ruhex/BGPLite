using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace BGPLite.Tests;

/// <summary>
/// Proof-of-concept deterministic tests for the <c>IBgpConnection</c> + <c>TimeProvider</c> seam
/// (#96). These exercise the BGP session's hold-timer expiry path without a real loopback socket
/// and without waiting on wall-clock time — the patterns the 14 socket-driven tests in
/// <c>BgpSessionShutdownTests</c> cannot use.
/// <para>
/// <c>FakeBgpConnection</c> scripts inbound messages and captures outbound bytes (replacing
/// <c>ConnectedPair</c> + <c>DrainAsync</c>); <c>FakeTimeProvider</c> advances the clock instantly
/// (replacing <c>HoldTime=3</c> real-second waits). Together they make the FSM deterministic and
/// ~1000× faster than the socket scaffolding.
/// </para>
/// </summary>
public class BgpSessionHoldTimerTests
{
    /// <summary>
    /// A fake <see cref="IBgpConnection"/> that delivers scripted inbound messages and captures
    /// every outbound message for assertions. Reads block on a <see cref="Channel"/> until a message
    /// is enqueued (or the channel completes), so the read loop waits deterministically for the next
    /// scripted byte — no real socket, no <c>DrainAsync</c>.
    /// </summary>
    private sealed class FakeBgpConnection : IBgpConnection
    {
        // Inbound messages are enqueued as whole frames; reads pull from a running buffer so that
        // a ReadExactAsync(19-byte-header) + ReadExactAsync(payload) pair splits a single enqueued
        // message correctly (the leftover bytes after the header are retained for the next read).
        private readonly System.Threading.Channels.Channel<byte[]> _inbound =
            System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private readonly List<byte[]> _sent = new();
        private readonly Queue<byte> _readBuffer = new();
        public IReadOnlyList<byte[]> Sent => _sent;
        public bool Disposed { get; private set; }

        public void Enqueue(byte[] message) => _inbound.Writer.TryWrite(message);
        public void Complete() => _inbound.Writer.TryComplete();

        public async ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                // Drain the running buffer first (leftover bytes from a previous chunk).
                while (_readBuffer.Count > 0 && offset < buffer.Length)
                    buffer.Span[offset++] = _readBuffer.Dequeue();
                if (offset >= buffer.Length) break;

                // Need more bytes from the channel.
                byte[] chunk;
                try
                {
                    chunk = await _inbound.Reader.ReadAsync(cancellationToken);
                }
                catch (System.Threading.Channels.ChannelClosedException)
                {
                    throw new IOException("Connection closed by peer");
                }
                // Copy what fits into the requested buffer; queue the rest for the next read.
                var toCopy = Math.Min(chunk.Length, buffer.Length - offset);
                for (var i = 0; i < toCopy; i++)
                    buffer.Span[offset++] = chunk[i];
                for (var i = toCopy; i < chunk.Length; i++)
                    _readBuffer.Enqueue(chunk[i]);
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            // Capture the outbound bytes for assertions. A copy is needed because the caller
            // (SendMessageAsync) returns the ArrayPool buffer to the pool after this returns.
            _sent.Add(buffer.ToArray());
            return default;
        }

        public void Dispose()
        {
            Disposed = true;
            _inbound.Writer.TryComplete();
        }
    }

    private sealed class NopLogger<T> : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NopDisposable.Instance;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
        private sealed class NopDisposable : IDisposable
        {
            public static readonly NopDisposable Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// Drives the full OPEN/KEEPALIVE handshake against the session through the fake connection,
    /// returning the background RunAsync task. Mirrors <c>EstablishSessionAsync</c> from the socket
    /// tests, but without a real TCP pair — the inbound OPEN/KEEPALIVE are scripted in response to
    /// the session's own OPEN/KEEPALIVE (observed via the captured outbound list).
    /// </summary>
    private static async Task<Task> EstablishAsync(
        BgpSession session, FakeBgpConnection conn, BgpConfig bgpConfig, FakeTimeProvider time)
    {
        var runTask = Task.Run(() => session.RunAsync(CancellationToken.None));

        // 1) Script the peer's OPEN — the session is waiting for it (with an OPEN timeout).
        var peerOpen = new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = bgpConfig.Asn == 65001 ? (ushort)65002 : (ushort)65001,
            HoldTime = (ushort)bgpConfig.HoldTime,
            RouterId = 0x7F000002,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)]
        };
        conn.Enqueue(Serialize(peerOpen));

        // 2) Wait for the session to send its OPEN (then it's in OpenConfirm, waiting for KEEPALIVE).
        int openSent;
        for (openSent = 0; openSent < 100 && conn.Sent.Count == 0; openSent++)
            await Task.Delay(5);
        Assert.True(conn.Sent.Count > 0, "session must send its OPEN");

        // 3) Script the peer's KEEPALIVE — the session needs it to transition OpenConfirm → Established.
        conn.Enqueue(Serialize(BgpKeepaliveMessage.Instance));

        // 4) Wait for the session to reach Established. Advance the fake clock slightly so timers tick.
        for (var i = 0; i < 100 && !session.IsEstablished; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(5);
        }

        return runTask;
    }

    private static byte[] Serialize(BgpMessage message)
    {
        var buf = new byte[BgpMessageWriter.GetBufferSize(message)];
        BgpMessageWriter.WriteMessage(message, buf);
        return buf;
    }

    /// <summary>
    /// Proof of concept: the hold timer fires NOTIFICATION(HoldTimerExpired) when the peer stops
    /// sending and the clock advances past the hold window. No real socket, no multi-second wait —
    /// the fake clock advances instantly. This is the test the socket suite's
    /// <c>HoldTimer_Expiry_Emits_Notification_HoldTimerExpired</c> (~4s) cannot be.
    /// </summary>
    [Fact]
    public async Task HoldTimer_FiresNotification_WhenClockAdvancesPastWindow()
    {
        var time = new FakeTimeProvider();
        var conn = new FakeBgpConnection();
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 9, KeepAlive = 3 };
        using var session = new BgpSession(
            conn,
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            new NopLogger<BgpSession>(),
            timeProvider: time);

        // Establish the session.
        var runTask = await EstablishAsync(session, conn, bgpConfig, time);
        Assert.True(session.IsEstablished, "session must reach Established");

        // The peer goes silent (no more messages enqueued). Advance the fake clock past the hold
        // window — the keepalive/hold-timer loop ticks on the PeriodicTimer and fires NOTIFICATION.
        var sentBeforeExpiry = conn.Sent.Count;
        // Advance in keepalive-interval steps so the PeriodicTimer ticks and the hold check runs.
        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(3));
            await Task.Delay(5); // let the timer loop observe the tick
        }

        // A HoldTimerExpired NOTIFICATION must have been captured among the outbound messages.
        var notifs = conn.Sent.Skip(sentBeforeExpiry)
            .Select(b => BgpMessageReader.ReadMessage(b.AsSpan()))
            .OfType<BgpNotificationMessage>()
            .ToList();
        Assert.Contains(notifs, n => n.ErrorCode == BgpConstants.Error.HoldTimerExpired);

        // Clean up.
        conn.Complete();
        session.MarkSilentClose();
        try { await runTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
    }

    /// <summary>
    /// Proof of concept: a peer that connects but never sends OPEN is dropped when the OPEN timeout
    /// fires — the timer CTS uses the TimeProvider, so the fake clock advances instantly instead of
    /// waiting wall-clock seconds. No real socket, no multi-second wait.
    /// </summary>
    [Fact]
    public async Task OpenTimeout_DropsPeer_WhenClockAdvancesPastWindow()
    {
        var time = new FakeTimeProvider();
        var conn = new FakeBgpConnection();
        // OpenTimeoutSeconds=5 — small but realistic. FakeTimeProvider advances instantly.
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 9, KeepAlive = 3, OpenTimeoutSeconds = 5 };
        using var session = new BgpSession(
            conn,
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            new NopLogger<BgpSession>(),
            timeProvider: time);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var runTask = Task.Run(() => session.RunAsync(CancellationToken.None));

        // Give the session time to enter the OPEN-receive (it builds the linked CTS + timer CTS,
        // then awaits ReceiveMessageAsync which blocks on the fake connection's channel). Poll
        // until the connection sees its first read attempt, then advance the clock — avoids a
        // fixed delay that's too short on slow CI runners.
        for (var i = 0; i < 100 && !conn.Disposed; i++)
        {
            await Task.Delay(10);
            // The session has entered OPEN-receive when it's not Established (handshake started
            // but not completed since we sent no OPEN).
            if (!session.IsEstablished && session.State != BgpFsmState.Idle)
                break;
        }

        // DO NOT send OPEN — the peer is silent (Slowloris). Advance the fake clock past the timeout.
        // The session's OPEN receive loop is cancelled by the timer CTS; the FSM unwinds to Idle.
        time.Advance(TimeSpan.FromSeconds(6));
        try { await runTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
        sw.Stop();

        Assert.False(session.IsEstablished, "a silent peer must not reach Established");
        // The session ran and exited the handshake — it took milliseconds, not the configured 5s.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"OpenTimeout test took {sw.ElapsedMilliseconds}ms — FakeTimeProvider not honored");

        conn.Dispose();
    }
}
