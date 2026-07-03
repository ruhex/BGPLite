using System.Net;
using System.Net.Sockets;
using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging;

namespace BGPLite.Tests;

public class BgpSessionShutdownTests
{
    private static void ReadExact(Socket s, byte[] buf, int offset, int count)
    {
        var got = 0;
        while (got < count)
        {
            var n = s.Receive(buf, offset + got, count - got, SocketFlags.None);
            if (n == 0) throw new IOException("socket closed");
            got += n;
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

    private static BgpSession NewSession(Socket server) => new(
        server,
        new PeerConfig { Address = "127.0.0.1" },
        new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" },
        new RouteTable(),
        AllowAllFilter.Instance,
        new BgpMetrics(),
        new NopLogger<BgpSession>());

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

    [Fact]
    public async Task NotifyCeaseAsync_Writes_Cease_Notification_To_Wire()
    {
        var (server, client) = ConnectedPair();
        using var clientSock = client;
        using var session = NewSession(server);

        await session.NotifyCeaseAsync();

        var buf = new byte[64];
        ReadExact(client, buf, 0, BgpConstants.MessageHeaderSize);
        var len = BgpMessageReader.GetMessageLength(buf);
        ReadExact(client, buf, BgpConstants.MessageHeaderSize, len - BgpConstants.MessageHeaderSize);

        var msg = BgpMessageReader.ReadMessage(buf.AsSpan(0, len));
        var notif = Assert.IsType<BgpNotificationMessage>(msg);
        Assert.Equal(BgpConstants.Error.Cease, notif.ErrorCode);
        Assert.Equal(BgpConstants.SubError.CeaseAdministrativeReset, notif.SubErrorCode);
    }

    [Fact]
    public async Task NotifyCeaseAsync_Swallows_Error_When_Socket_Closed()
    {
        // If the socket is already gone, NotifyCeaseAsync must not throw (best-effort on shutdown).
        var (server, client) = ConnectedPair();
        using var clientSock = client;
        using var session = NewSession(server);

        server.Close(); // kill the server side before notifying

        // Should complete without throwing despite the closed socket.
        await session.NotifyCeaseAsync();
    }

    /// <summary>
    /// Regression: NotifyCeaseAsync must NOT send a Cease when MarkSilentClose was already called.
    /// The old code sent Cease on the wire BEFORE CAS-latching LocalCease, so a concurrent
    /// MarkSilentClose (GR-aware shutdown / session replacement) that already latched SilentClose
    /// would still see the Cease go out — violating RFC 4724 §4 (silent close = no NOTIFICATION)
    /// and RFC 4271 §8.1 (exactly one NOTIFICATION per teardown). The fix moves the CAS before
    /// SendNotificationAsync and returns early when the reason is no longer None.
    /// </summary>
    [Fact]
    public async Task NotifyCeaseAsync_Does_Not_Send_When_SilentClose_Already_Latched()
    {
        var (server, client) = ConnectedPair();
        using var clientSock = client;
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        using var session = new BgpSession(
            server,
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            new NopLogger<BgpSession>());

        var runTask = await EstablishSessionAsync(session, client, bgpConfig);

        // MarkSilentClose latches SilentClose and cancels CTS — RunAsync will unwind.
        session.MarkSilentClose();

        // Wait for RunAsync to complete so the session is fully torn down.
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.IsEstablished, "session must leave Established after MarkSilentClose");

        // NotifyCeaseAsync must see SilentClose already latched and return without sending.
        await session.NotifyCeaseAsync();

        // Drain the wire: no NOTIFICATION should appear (silent close).
        var sent = await DrainAsync(client, TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(sent, m => m is BgpNotificationMessage);
    }

    /// <summary>
    /// RFC 4271 §6.6 / §8.1: when the negotiated hold timer expires (no message received within
    /// HoldTime seconds), the session MUST emit NOTIFICATION(Hold Timer Expired, subcode=0) and
    /// transition to Idle. Drives the OPEN/OpenConfirm exchange with HoldTime=3, then goes silent
    /// for 4s and verifies the peer-side NOTIFICATION.
    /// </summary>
    [Fact]
    public async Task HoldTimer_Expiry_Emits_Notification_HoldTimerExpired()
    {
        var (server, client) = ConnectedPair();
        using var clientSock = client;
        using var session = new BgpSession(
            server,
            new PeerConfig { Address = "127.0.0.1" },
            // HoldTime/KeepAlive: minimum allowed by RFC 4271 (≥3 for HoldTime). keepalive=1s,
            // hold=3s — checks expiry every 1s; expect NOTIFICATION within ~4s.
            new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 3, KeepAlive = 1 },
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            new NopLogger<BgpSession>());

        // RunAsync on a background task — it will be cancelled by the hold-timer expiry.
        var runTask = Task.Run(() => session.RunAsync(CancellationToken.None));

        // 1) Send OPEN (peer-side). HoldTime=3 matches our session's negotiated hold.
        var open = new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = 3,
            RouterId = 0x7F000002,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)]
        };
        var openBuf = new byte[BgpMessageWriter.GetBufferSize(open)];
        var openLen = BgpMessageWriter.WriteMessage(open, openBuf);
        client.Send(openBuf, 0, openLen, SocketFlags.None);

        // 2) Read session's OPEN + KEEPALIVE (OpenSent → KEEPALIVE → OpenConfirm).
        // The session sends OPEN then a KEEPALIVE — read both.
        using var hsCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var header = new byte[BgpConstants.MessageHeaderSize];
        await ReadExactAsync(client, header, hsCts.Token);
        var openLenFromWire = BgpMessageReader.GetMessageLength(header);
        Assert.Equal(BgpMessageType.Open, (BgpMessageType)header[18]);
        var openPayload = new byte[openLenFromWire - BgpConstants.MessageHeaderSize];
        await ReadExactAsync(client, openPayload, hsCts.Token);

        // KEEPALIVE (19 bytes)
        await ReadExactAsync(client, header, hsCts.Token);
        Assert.Equal(BgpMessageType.Keepalive, (BgpMessageType)header[18]);

        // 3) Send KEEPALIVE → Established. Then send no more traffic; hold timer should expire.
        var keepaliveBuf = new byte[BgpMessageWriter.GetBufferSize(BgpKeepaliveMessage.Instance)];
        BgpMessageWriter.WriteMessage(BgpKeepaliveMessage.Instance, keepaliveBuf);
        client.Send(keepaliveBuf, 0, keepaliveBuf.Length, SocketFlags.None);

        // 4) Drain any messages the session sent (initial UPDATE, end-of-RIB, keepalives). Then
        //    wait for the hold-timer NOTIFICATION. Use a single message-framing loop to stay aligned.
        //    Collect EVERY NOTIFICATION (don't break on the first) so we can assert exactly one —
        //    the HoldTimerExpired — and no double Cease from the RunAsync finally-block (RFC 4271 §8.1).
        using var notifCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var allNotifications = new List<BgpNotificationMessage>();
        while (!notifCts.IsCancellationRequested)
        {
            var drainHeader = new byte[BgpConstants.MessageHeaderSize];
            try { await ReadExactAsync(client, drainHeader, notifCts.Token); }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; } // socket closed by peer after NOTIFICATION — normal EOF
            catch (SocketException) { break; } // TCP RST or socket error — treat as EOF

            var totalLen = BgpMessageReader.GetMessageLength(drainHeader);
            var payload = new byte[totalLen - BgpConstants.MessageHeaderSize];
            if (payload.Length > 0)
            {
                try { await ReadExactAsync(client, payload, notifCts.Token); }
                catch (OperationCanceledException) { break; }
                catch (IOException) { break; } // socket closed mid-payload — normal EOF
                catch (SocketException) { break; } // TCP RST or socket error — treat as EOF
            }
            var msg = BgpMessageReader.ReadMessage(Concat(drainHeader, payload));
            if (msg is BgpNotificationMessage n)
                allNotifications.Add(n);
            // otherwise: UPDATE / KEEPALIVE — keep draining.
        }

        Assert.NotEmpty(allNotifications);
        var holdNotif = Assert.Single(allNotifications,
            n => n.ErrorCode == BgpConstants.Error.HoldTimerExpired);
        Assert.Equal(BgpConstants.SubError.Unspecific, holdNotif.SubErrorCode);
        Assert.Single(allNotifications); // exactly one NOTIFICATION total (RFC 4271 §8.1)
        Assert.DoesNotContain(allNotifications, n => n.ErrorCode == BgpConstants.Error.Cease);

        // RunAsync should complete (it self-cancels after HoldTimerLoopAsync returns).
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.IsEstablished, "session must transition out of Established after hold expiry");
    }

    /// <summary>
    /// Drives a session through the full OPEN/KEEPALIVE handshake to Established. Returns the
    /// background RunAsync task. The session ends up Established and the client socket is positioned
    /// just past the session's OPEN + KEEPALIVE + any initial UPDATE/End-of-RIB burst is NOT drained
    /// here (callers drain as needed). Use HoldTime=0 to keep the established read loop simple.
    /// </summary>
    private static async Task<Task> EstablishSessionAsync(BgpSession session, Socket client, BgpConfig bgpConfig, CancellationToken? runToken = null)
    {
        var runTask = Task.Run(() => session.RunAsync(runToken ?? CancellationToken.None));

        // 1) Peer sends OPEN.
        var open = new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = (ushort)bgpConfig.HoldTime,
            RouterId = 0x7F000002,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)]
        };
        var openBuf = new byte[BgpMessageWriter.GetBufferSize(open)];
        var openLen = BgpMessageWriter.WriteMessage(open, openBuf);
        client.Send(openBuf, 0, openLen, SocketFlags.None);

        // 2) Read session's OPEN, then KEEPALIVE (OpenSent → OpenConfirm).
        // Use a bounded timeout so the test fails fast if the session fails to send
        // the expected messages (e.g. if it sends a NOTIFICATION or encounters an error).
        using var hsCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var header = new byte[BgpConstants.MessageHeaderSize];
        await ReadExactAsync(client, header, hsCts.Token);
        Assert.Equal(BgpMessageType.Open, (BgpMessageType)header[18]);
        var totalLen = BgpMessageReader.GetMessageLength(header);
        if (totalLen > BgpConstants.MessageHeaderSize)
            await ReadExactAsync(client, new byte[totalLen - BgpConstants.MessageHeaderSize], hsCts.Token);

        await ReadExactAsync(client, header, hsCts.Token);
        Assert.Equal(BgpMessageType.Keepalive, (BgpMessageType)header[18]);

        // 3) Peer sends KEEPALIVE → Established.
        var keepaliveBuf = new byte[BgpMessageWriter.GetBufferSize(BgpKeepaliveMessage.Instance)];
        BgpMessageWriter.WriteMessage(BgpKeepaliveMessage.Instance, keepaliveBuf);
        client.Send(keepaliveBuf, 0, keepaliveBuf.Length, SocketFlags.None);

        // Give the FSM a moment to cross into Established.
        using var estCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!session.IsEstablished && !estCts.IsCancellationRequested)
            await Task.Delay(20, estCts.Token);
        Assert.True(session.IsEstablished, "session should be Established after OPEN/KEEPALIVE handshake");
        return runTask;
    }

    /// <summary>
    /// Drains every BGP message currently buffered on the client socket within the timeout, without
    /// blocking once the socket is closed. Returns the messages read. Used to assert what the session
    /// did (or did not) send after a teardown trigger.
    /// </summary>
    private static async Task<List<BgpMessage>> DrainAsync(Socket client, TimeSpan timeout)
    {
        var msgs = new List<BgpMessage>();
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            var header = new byte[BgpConstants.MessageHeaderSize];
            try { await ReadExactAsync(client, header, cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; } // socket closed — normal EOF after teardown
            catch (SocketException) { break; } // TCP RST or socket error — treat as EOF

            var totalLen = BgpMessageReader.GetMessageLength(header);
            var payload = new byte[totalLen - BgpConstants.MessageHeaderSize];
            if (payload.Length > 0)
            {
                try { await ReadExactAsync(client, payload, cts.Token); }
                catch (OperationCanceledException) { break; }
                catch (IOException) { break; }
                catch (SocketException) { break; }
            }
            msgs.Add(BgpMessageReader.ReadMessage(Concat(header, payload)));
        }
        return msgs;
    }

    /// <summary>
    /// RFC 4271 §6.3/§8.1: on receiving a NOTIFICATION from the peer, the session MUST release
    /// resources, drop the TCP connection and move to Idle — and MUST NOT send a NOTIFICATION back.
    /// Regression for the bug where ReadLoopAsync returned from a peer NOTIFICATION without latching
    /// a teardown reason, so the RunAsync finally-block saw Established + reason=None and emitted a
    /// best-effort Cease back to the peer (a protocol violation).
    /// </summary>
    [Fact]
    public async Task Remote_Notification_Does_Not_Reply_With_Cease()
    {
        var (server, client) = ConnectedPair();
        using var clientSock = client;
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        using var session = new BgpSession(
            server,
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            new NopLogger<BgpSession>());

        var runTask = await EstablishSessionAsync(session, client, bgpConfig);

        // Send NOTIFICATION(Cease) from the peer.
        var notif = new BgpNotificationMessage
        {
            ErrorCode = BgpConstants.Error.Cease,
            SubErrorCode = BgpConstants.SubError.Unspecific
        };
        var notifBuf = new byte[BgpMessageWriter.GetBufferSize(notif)];
        var notifLen = BgpMessageWriter.WriteMessage(notif, notifBuf);
        client.Send(notifBuf, 0, notifLen, SocketFlags.None);

        // RunAsync must complete and the session must leave Established.
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.IsEstablished, "session must leave Established after receiving a NOTIFICATION");

        // The session MUST NOT have replied with a NOTIFICATION of its own. The only acceptable
        // outcome on the wire after the peer's NOTIFICATION is a clean TCP close (EOF) — no Cease,
        // no Hold Timer Expired, nothing. Drain whatever is left.
        var sent = await DrainAsync(client, TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(sent, m => m is BgpNotificationMessage);
    }

    /// <summary>
    /// #94 / RFC 7606: a single malformed inbound UPDATE (here: NLRI present but the mandatory ORIGIN
    /// attribute is missing → MissingWellKnownAttribute) must NOT tear down the session. The read loop
    /// catches BgpNotificationException(UpdateMessageError), logs + counts it, and keeps going — a
    /// route-server should not lose a long-lived session over one bad/adversarial UPDATE. Stream-level
    /// errors (FSM / message header) still propagate and tear down.
    /// </summary>
    [Fact]
    public async Task MalformedUpdate_Does_Not_Tear_Down_Session()
    {
        var (server, client) = ConnectedPair();
        using var clientSock = client;
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        var metrics = new BgpMetrics();
        using var session = new BgpSession(
            server,
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            new RouteTable(),
            AllowAllFilter.Instance,
            metrics,
            new NopLogger<BgpSession>());

        var runTask = await EstablishSessionAsync(session, client, bgpConfig);

        // Malformed UPDATE: announces NLRI but omits the mandatory ORIGIN attribute.
        var malformed = new BgpUpdateMessage
        {
            Nlri = [new IpPrefix(0xC0A80100u, 24)],
            PathAttributes = []
        };
        var buf = new byte[BgpMessageWriter.GetBufferSize(malformed)];
        var len = BgpMessageWriter.WriteMessage(malformed, buf);
        client.Send(buf, 0, len, SocketFlags.None);

        // Let the read loop receive and reject the bad UPDATE.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.True(session.IsEstablished, "session must survive a single malformed UPDATE (#94)");
        Assert.True(metrics.UpdatesRejected >= 1, "the malformed UPDATE must be counted as rejected");

        // No NOTIFICATION on the wire — we keep the session instead of notifying/tearing down.
        var sent = await DrainAsync(client, TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(sent, m => m is BgpNotificationMessage);

        session.MarkSilentClose();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// RFC 4724 §4 / §8.1: a Graceful-Restart-aware silent close (MarkSilentClose) must not emit a
    /// NOTIFICATION — the TCP connection is dropped so the peer retains routes across the restart.
    /// Regression for the bug where StopAsync with GR enabled skipped NotifyCeaseAsync but then
    /// cancelled sessions, and the RunAsync finally-block (reason still None) emitted a Cease anyway.
    /// </summary>
    [Fact]
    public async Task MarkSilentClose_Does_Not_Send_Cease_And_Stops_Session()
    {
        var (server, client) = ConnectedPair();
        using var clientSock = client;
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        using var session = new BgpSession(
            server,
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            new NopLogger<BgpSession>());

        var runTask = await EstablishSessionAsync(session, client, bgpConfig);

        // Silent close — what StopAsync does on GR-enabled shutdown and what AcceptLoopAsync does on
        // session replacement. Latches SilentClose and cancels the session's own CTS.
        session.MarkSilentClose();

        // RunAsync must unwind promptly (its CTS was cancelled), not linger on the read loop.
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.IsEstablished, "session must leave Established after MarkSilentClose");

        // No NOTIFICATION may appear on the wire — only the TCP close.
        var sent = await DrainAsync(client, TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(sent, m => m is BgpNotificationMessage);
    }

    /// <summary>
    /// RFC 4271 §8.1: the positive path for the TeardownReason enum — an unexpected teardown from
    /// Established where no NOTIFICATION was already emitted and no silent close was latched (reason
    /// still None) MUST emit exactly one best-effort Cease before close. Triggered by cancelling the
    /// external token passed to RunAsync: OperationCanceledException is caught by its own catch
    /// (which does NOT latch a reason), so the finally sees None and sends exactly one Cease. The
    /// finally CAS then transitions None→LocalCease, so no second NOTIFICATION is emitted. This is
    /// the same path StopAsync would have hit before the GR-aware MarkSilentClose fix.
    /// </summary>
    [Fact]
    public async Task Unexpected_Teardown_From_Established_Emits_Exactly_One_Cease()
    {
        var (server, client) = ConnectedPair();
        using var clientSock = client;
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        using var session = new BgpSession(
            server,
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            new NopLogger<BgpSession>());

        using var extCts = new CancellationTokenSource();
        var runTask = await EstablishSessionAsync(session, client, bgpConfig, extCts.Token);

        // Cancel the external token. OperationCanceledException is caught by its dedicated catch
        // (no reason latched) → finally sees None → exactly one Cease via the finally CAS.
        await extCts.CancelAsync();

        var sent = await DrainAsync(client, TimeSpan.FromSeconds(3));
        var ceaseNotif = Assert.Single(sent.OfType<BgpNotificationMessage>());
        Assert.Equal(BgpConstants.Error.Cease, ceaseNotif.ErrorCode);
        Assert.Equal(BgpConstants.SubError.Unspecific, ceaseNotif.SubErrorCode);

        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.IsEstablished, "session must leave Established after the cancellation");
    }

    /// <summary>
    /// Atomic session removal: when a RunSessionAsync finally-block runs for an OLD session, it must
    /// not erase a NEWER session that was re-accepted for the same peer in the meantime. This is the
    /// regression for the TryGetValue+TryRemove race (a newer session installed between the two calls
    /// would be removed by the old finally). Reproduces the same atomic compare-and-remove that
    /// BgpServer.RemoveSessionIfOwner uses against a real ConcurrentDictionary, with the old and new
    /// session standing in for the racing finally vs. re-accept.
    /// </summary>
    [Fact]
    public void Atomic_Removal_Does_Not_Erase_Replaced_Session()
    {
        var dict = new System.Collections.Concurrent.ConcurrentDictionary<string, BgpSession>();

        // Simulate: old session registered, then replaced by a new one (as AcceptLoopAsync does).
        // Capture + dispose both client sockets from ConnectedPair() — NewSession only needs the
        // server side, but the client side must be disposed too or it leaks a TCP connection.
        var (oldServer, oldClient) = ConnectedPair();
        var (newServer, newClient) = ConnectedPair();
        using var _oldClient = oldClient;
        using var _newClient = newClient;
        using var oldSession = NewSession(oldServer);
        using var newSession = NewSession(newServer);
        dict["1.2.3.4"] = oldSession;
        dict["1.2.3.4"] = newSession;

        // The old session's finally runs RemoveSessionIfOwner. It must NOT remove the new session.
        var removed = ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<string, BgpSession>>)dict)
            .Remove(new System.Collections.Generic.KeyValuePair<string, BgpSession>("1.2.3.4", oldSession));
        Assert.False(removed, "old session's removal must not succeed when a newer session is registered");
        Assert.True(dict.TryGetValue("1.2.3.4", out var current));
        Assert.True(ReferenceEquals(current, newSession), "newer session must still be registered");

        // The new session's finally runs RemoveSessionIfOwner — this one SHOULD succeed.
        var removedNew = ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<string, BgpSession>>)dict)
            .Remove(new System.Collections.Generic.KeyValuePair<string, BgpSession>("1.2.3.4", newSession));
        Assert.True(removedNew, "current owner's removal must succeed");
        Assert.Empty(dict);
    }

    /// <summary>
    /// Session replacement must actually close the old session, not just fire-and-forget a Cease.
    /// Regression for the bug where AcceptLoopAsync did `_ = existing.NotifyCeaseAsync()` (which only
    /// latched + wrote bytes) and never cancelled the old session's CTS — so the old read/keepalive
    /// loops kept running until the peer closed the socket or the hold timer fired. MarkSilentClose
    /// latches SilentClose AND cancels the old CTS, so RunAsync unwinds promptly.
    /// </summary>
    [Fact]
    public async Task Replacement_MarkSilentClose_Stops_Old_Session_Promptly()
    {
        var (server, client) = ConnectedPair();
        using var clientSock = client;
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        using var session = new BgpSession(
            server,
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            new NopLogger<BgpSession>());

        var runTask = await EstablishSessionAsync(session, client, bgpConfig);

        // Simulate what AcceptLoopAsync does on replacement: MarkSilentClose on the old session.
        session.MarkSilentClose();

        // The old RunAsync must exit promptly — its CTS was cancelled, so the read loop unwinds
        // instead of lingering until a peer close or hold-timer expiry.
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.IsEstablished, "old session must be torn down after MarkSilentClose");

        // And it must not have sent a Cease on the way out (silent close, RFC 4724 §4).
        var sent = await DrainAsync(client, TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(sent, m => m is BgpNotificationMessage);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    private static async Task ReadExactAsync(Socket s, byte[] buf, CancellationToken cancellationToken)
    {
        var got = 0;
        while (got < buf.Length)
        {
            var n = await s.ReceiveAsync(new ArraySegment<byte>(buf, got, buf.Length - got), SocketFlags.None, cancellationToken);
            if (n == 0) throw new IOException("socket closed");
            got += n;
        }
    }
}
