using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using BGPLite.Contracts;

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

        // EOF signal for IsPeerClosed: the channel writer completed and nothing is left in the
        // running read buffer — equivalent to the kernel having delivered the FIN.
        // _finReceived: a FIN has "arrived at the kernel" (IsPeerClosed reports true) WITHOUT the
        // channel writer being completed. This lets a test reproduce the EOF↔cancel race (#217,
        // dotnet/runtime #16025): the pending reader stays blocked until the token is cancelled,
        // then throws OperationCanceledException — exactly the non-deterministic .NET timing where
        // cancel can win over an already-arrived FIN. Channel otherwise resolves completion
        // synchronously and this path can't be exercised.
        private volatile bool _finReceived;
        public void SimulateFinReceived() => _finReceived = true;

        public bool IsPeerClosed => Disposed
            || _finReceived
            || (_inbound.Reader.Completion.IsCompleted && _readBuffer.Count == 0);

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

    /// <summary>A minimal <see cref="ILogger{TCategoryName}"/> that records entries for assertions (#216).</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        // Retain the Exception? alongside level/message so Debug stack-trace logging is assertable.
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
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
    /// #286: a peer that sends a well-formed OPEN and then goes silent must NOT pin the session.
    /// OpenTimeoutSeconds (#115) bounds only the read that receives the OPEN; the KEEPALIVE read
    /// that follows was unbounded, and the keepalive/hold loop does not start until
    /// RunEstablishedAsync — so the session sat in OpenConfirm forever, holding a socket FD and a
    /// task. RFC 4271 §8.2.2 runs the Hold Timer in OpenConfirm; on expiry it must send
    /// NOTIFICATION(Hold Timer Expired) and go to Idle.
    /// </summary>
    [Fact]
    public async Task OpenConfirm_PeerNeverSendsKeepalive_TearsDownOnHoldTimer()
    {
        var time = new FakeTimeProvider();
        var conn = new FakeBgpConnection();
        var bgpConfig = new BgpConfig
        {
            Asn = 65001,
            RouterId = "127.0.0.1",
            HoldTime = 9,
            KeepAlive = 3,
            OpenTimeoutSeconds = 30, // deliberately longer than HoldTime: it must not be what saves us
        };
        using var session = new BgpSession(
            conn,
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            new NopLogger<BgpSession>(),
            timeProvider: time);

        var runTask = Task.Run(() => session.RunAsync(CancellationToken.None));

        // The peer sends a valid OPEN...
        conn.Enqueue(Serialize(new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = (ushort)bgpConfig.HoldTime,
            RouterId = 0x7F000002,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)]
        }));

        // ...the session answers with OPEN + KEEPALIVE and enters OpenConfirm...
        for (var i = 0; i < 200 && session.State != BgpFsmState.OpenConfirm; i++)
            await Task.Delay(5);
        Assert.Equal(BgpFsmState.OpenConfirm, session.State);

        // ...and then the peer never sends its KEEPALIVE. Advance past the negotiated hold time.
        var sentBefore = conn.Sent.Count;
        for (var i = 0; i < 20 && !runTask.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(5);
        }

        await runTask.WaitAsync(TimeSpan.FromSeconds(5)); // must not hang
        Assert.Equal(BgpFsmState.Idle, session.State);

        var notifs = conn.Sent.Skip(sentBefore)
            .Select(b => BgpMessageReader.ReadMessage(b.AsSpan()))
            .OfType<BgpNotificationMessage>()
            .ToList();
        var notif = Assert.Single(notifs);
        Assert.Equal(BgpConstants.Error.HoldTimerExpired, notif.ErrorCode);
        Assert.Equal(BgpConstants.SubError.Unspecific, notif.SubErrorCode);
    }

    /// <summary>
    /// #286, hold time 0: RFC 4271 §4.2 disables the Hold Timer at 0, but that is a rule for an
    /// ESTABLISHED session. A handshake that never completes must still be bounded, so OpenConfirm
    /// falls back to the §8.2.2 initial Hold Time (4 minutes) rather than waiting forever.
    /// </summary>
    [Fact]
    public async Task OpenConfirm_HoldTimeZero_StillBoundedByFallback()
    {
        var time = new FakeTimeProvider();
        var conn = new FakeBgpConnection();
        var bgpConfig = new BgpConfig
        {
            Asn = 65001,
            RouterId = "127.0.0.1",
            HoldTime = 0,
            KeepAlive = 0,
            OpenTimeoutSeconds = 30,
        };
        using var session = new BgpSession(
            conn,
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            new NopLogger<BgpSession>(),
            timeProvider: time);

        var runTask = Task.Run(() => session.RunAsync(CancellationToken.None));
        conn.Enqueue(Serialize(new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = 0,
            RouterId = 0x7F000002,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)]
        }));

        for (var i = 0; i < 200 && session.State != BgpFsmState.OpenConfirm; i++)
            await Task.Delay(5);
        Assert.Equal(BgpFsmState.OpenConfirm, session.State);

        // Well inside the 4-minute fallback: still waiting, exactly as before this change.
        time.Advance(TimeSpan.FromMinutes(3));
        await Task.Delay(20);
        Assert.False(runTask.IsCompleted, "must still be waiting inside the fallback window");
        Assert.Equal(BgpFsmState.OpenConfirm, session.State);

        // Past it: bounded.
        for (var i = 0; i < 20 && !runTask.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(5);
        }

        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BgpFsmState.Idle, session.State);
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

    /// <summary>
    /// #216: a peer that closes the TCP connection before sending OPEN produces an explicit
    /// "closed the TCP connection before sending OPEN" Warning — NOT the generic "Session error"
    /// Error + stack trace. FakeBgpConnection.Complete() makes the read return EOF (throws
    /// IOException "Connection closed by peer"), exactly mirroring SocketBgpConnection.ReadExactAsync.
    /// </summary>
    [Fact]
    public async Task PeerCloseBeforeOpen_LogsExplicitCause_NotGenericSessionError()
    {
        var time = new FakeTimeProvider();
        var conn = new FakeBgpConnection();
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 9, KeepAlive = 3, OpenTimeoutSeconds = 5 };
        var logger = new CapturingLogger<BgpSession>();
        using var session = new BgpSession(
            conn,
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            logger,
            timeProvider: time);

        var runTask = Task.Run(() => session.RunAsync(CancellationToken.None));

        // Wait until the session has entered the OPEN-receive (blocks on the connection's channel),
        // then close the channel — the next read throws IOException "Connection closed by peer",
        // mirroring a peer that drops the socket immediately after connect.
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(10);
            if (!session.IsEstablished && session.State != BgpFsmState.Idle)
                break;
        }
        conn.Complete(); // EOF → IOException "Connection closed by peer"

        try { await runTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }

        Assert.False(session.IsEstablished, "a peer that closed pre-OPEN must not reach Established");

        string[] messages;
        lock (logger.Entries) messages = logger.Entries.Select(e => e.Message).ToArray();

        // The explicit cause is logged at Warning…
        Assert.Contains(messages, m => m.Contains("closed the TCP connection") && m.Contains("before sending OPEN"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("closed the TCP connection"));
        // …and the generic Error + stack trace is NOT emitted for a plain TCP close.
        Assert.DoesNotContain(messages, m => m.Contains("Session error"));

        conn.Dispose();
    }

    /// <summary>
    /// #216: a peer that closes the TCP connection mid-session (in Established) produces the explicit
    /// "closed the TCP connection during Established" Warning + a Debug stack-trace entry — NOT the
    /// generic "read loop faulted" Warning with a stack trace. Establishes the session via
    /// EstablishAsync, then Complete()s the connection to drive the read-loop EOF.
    /// </summary>
    [Fact]
    public async Task PeerCloseInEstablished_LogsExplicitCause_NotGenericLoopFault()
    {
        var time = new FakeTimeProvider();
        var conn = new FakeBgpConnection();
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 9, KeepAlive = 3 };
        var logger = new CapturingLogger<BgpSession>();
        using var session = new BgpSession(
            conn,
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            logger,
            timeProvider: time);

        var runTask = await EstablishAsync(session, conn, bgpConfig, time);
        Assert.True(session.IsEstablished, "session must reach Established before the close");

        // Peer drops the socket mid-session: the read loop's next ReceiveMessageAsync throws
        // IOException "Connection closed by peer", routed through AwaitLoopTaskAsync.
        conn.Complete();
        try { await runTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }

        List<(LogLevel Level, string Message, Exception? Exception)> snapshot;
        lock (logger.Entries) snapshot = logger.Entries.ToList();
        var messages = snapshot.Select(e => e.Message).ToArray();

        // The explicit Established-phase cause is logged at Warning…
        Assert.Contains(messages, m => m.Contains("closed the TCP connection") && m.Contains("during Established"));
        // …with a Debug-level IOException stack-trace entry (symmetry with the handshake path).
        Assert.Contains(snapshot, e => e.Level == LogLevel.Debug && e.Exception is IOException);
        // …and the generic "read loop faulted" Warning is NOT emitted for a plain TCP close.
        Assert.DoesNotContain(messages, m => m.Contains("loop faulted"));

        conn.Dispose();
    }

    /// <summary>
    /// #217 regression: the EOF↔cancel race in Established. The peer closed the TCP connection
    /// (FIN delivered to the kernel), but .NET surfaced OperationCanceledException instead of
    /// IOException because the hold-timer-expiry cancellation raced ahead of the FIN completion
    /// (dotnet/runtime #16025, non-deterministic in production). Without the transport probe,
    /// read-loop's <c>catch (OperationCanceledException)</c> would swallow this without the
    /// explicit Established-phase diagnostic — the operator would see only "Hold timer expired",
    /// not "peer closed the TCP connection during Established".
    /// <para>
    /// Reproduction: <c>SimulateFinReceived()</c> marks the FIN as kernel-delivered (so
    /// <see cref="IBgpConnection.IsPeerClosed"/> returns true) WITHOUT completing the channel — the
    /// reader stays blocked until the hold-timer cancels the token. The resulting OCE is then
    /// disambiguated by the transport probe, asserting the explicit cause is logged.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PeerCloseInEstablished_RaceWithHoldTimer_LogsExplicitCause()
    {
        var time = new FakeTimeProvider();
        var conn = new FakeBgpConnection();
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 9, KeepAlive = 3 };
        var logger = new CapturingLogger<BgpSession>();
        using var session = new BgpSession(
            conn,
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            logger,
            timeProvider: time);

        var runTask = await EstablishAsync(session, conn, bgpConfig, time);
        Assert.True(session.IsEstablished, "session must reach Established before the race");

        // The read loop is now blocked on ReceiveMessageAsync (no inbound messages). Mark the FIN
        // as kernel-delivered WITHOUT completing the channel — the reader stays pending, but the
        // transport probe now reports the peer as closed.
        conn.SimulateFinReceived();
        Assert.True(conn.IsPeerClosed, "test setup: FIN received but channel still pending");

        // Advance the fake clock past the hold window — the hold timer fires NOTIFICATION and the
        // session cancels its token (RunEstablishedAsync: Task.WhenAny → _cts.CancelAsync). The
        // blocked reader throws OperationCanceledException, masking the FIN. Read-loop's catch(OCE)
        // must probe IsPeerClosed and log the explicit Established-phase cause.
        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(3));
            await Task.Delay(5); // let the timer loop tick
        }

        try { await runTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }

        List<(LogLevel Level, string Message, Exception? Exception)> snapshot;
        lock (logger.Entries) snapshot = logger.Entries.ToList();
        var messages = snapshot.Select(e => e.Message).ToArray();

        // The race must surface the explicit TCP-close cause (NOT just the hold-timer line):
        Assert.Contains(messages, m => m.Contains("closed the TCP connection") && m.Contains("during Established"));
        Assert.Contains(snapshot, e => e.Level == LogLevel.Warning && e.Message.Contains("closed the TCP connection"));
        // The generic "read loop faulted" and "Session error" are NOT emitted for this path.
        Assert.DoesNotContain(messages, m => m.Contains("loop faulted"));
        Assert.DoesNotContain(messages, m => m.Contains("Session error"));

        conn.Dispose();
    }

    /// <summary>
    /// #217 regression: HoldTime=0 (RFC 4271 §4.2/§6.5 — KEEPALIVE/Hold-Timer disabled) routes
    /// <c>ReadLoopAsync</c> directly through <c>RunEstablishedAsync</c> WITHOUT
    /// <c>AwaitLoopTaskAsync</c>. A re-thrown IOException would propagate to <c>RunAsync</c>'s
    /// <c>catch(IOException)</c> and log a SECOND, generic "in state Established" line — duplicating
    /// the explicit "during Established" diagnostic emitted inside the read loop. This test pins the
    /// exactly-once invariant: one explicit Established-phase line, no generic duplicate.
    /// </summary>
    [Fact]
    public async Task PeerCloseInEstablished_HoldTimeZero_LogsExplicitCause_Once()
    {
        var time = new FakeTimeProvider();
        var conn = new FakeBgpConnection();
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        var logger = new CapturingLogger<BgpSession>();
        using var session = new BgpSession(
            conn,
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            logger,
            timeProvider: time);

        var runTask = await EstablishAsync(session, conn, bgpConfig, time);
        Assert.True(session.IsEstablished, "session must reach Established before the close");

        // Peer drops the socket mid-session. With HoldTime=0 this runs ReadLoopAsync directly.
        conn.Complete();
        try { await runTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }

        List<(LogLevel Level, string Message, Exception? Exception)> snapshot;
        lock (logger.Entries) snapshot = logger.Entries.ToList();
        var messages = snapshot.Select(e => e.Message).ToArray();

        // Exactly-once: the explicit "during Established" cause is present…
        var explicitCount = messages.Count(m => m.Contains("closed the TCP connection") && m.Contains("during Established"));
        Assert.Equal(1, explicitCount);
        // …and the generic RunAsync-catch "in state Established" line is NOT emitted (no duplication).
        Assert.DoesNotContain(messages, m => m.Contains("in state Established"));
        Assert.DoesNotContain(messages, m => m.Contains("Session error"));

        conn.Dispose();
    }
}
