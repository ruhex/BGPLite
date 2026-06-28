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
        Assert.Equal(BgpConstants.SubError.Unspecific, notif.SubErrorCode);
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
        var header = new byte[BgpConstants.MessageHeaderSize];
        ReadExact(client, header, 0, BgpConstants.MessageHeaderSize);
        var openLenFromWire = BgpMessageReader.GetMessageLength(header);
        Assert.Equal(BgpMessageType.Open, (BgpMessageType)header[18]);
        var openPayload = new byte[openLenFromWire - BgpConstants.MessageHeaderSize];
        ReadExact(client, openPayload, 0, openPayload.Length);

        // KEEPALIVE (19 bytes)
        ReadExact(client, header, 0, BgpConstants.MessageHeaderSize);
        Assert.Equal(BgpMessageType.Keepalive, (BgpMessageType)header[18]);

        // 3) Send KEEPALIVE → Established. Then send no more traffic; hold timer should expire.
        var keepaliveBuf = new byte[BgpMessageWriter.GetBufferSize(BgpKeepaliveMessage.Instance)];
        BgpMessageWriter.WriteMessage(BgpKeepaliveMessage.Instance, keepaliveBuf);
        client.Send(keepaliveBuf, 0, keepaliveBuf.Length, SocketFlags.None);

        // 4) Drain any messages the session sent (initial UPDATE, end-of-RIB, keepalives). Then
        //    wait for the hold-timer NOTIFICATION. Use a single message-framing loop to stay aligned.
        using var notifCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        BgpNotificationMessage? holdNotif = null;
        while (!notifCts.IsCancellationRequested)
        {
            var drainHeader = new byte[BgpConstants.MessageHeaderSize];
            try { await ReadExactAsync(client, drainHeader, notifCts.Token); }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; } // socket closed by peer after NOTIFICATION — normal EOF

            var totalLen = BgpMessageReader.GetMessageLength(drainHeader);
            var payload = new byte[totalLen - BgpConstants.MessageHeaderSize];
            if (payload.Length > 0)
            {
                try { await ReadExactAsync(client, payload, notifCts.Token); }
                catch (OperationCanceledException) { break; }
            }
            var msg = BgpMessageReader.ReadMessage(Concat(drainHeader, payload));
            if (msg is BgpNotificationMessage n)
            {
                holdNotif = n;
                break;
            }
            // otherwise: UPDATE / KEEPALIVE — keep draining.
        }

        Assert.NotNull(holdNotif);
        Assert.Equal(BgpConstants.Error.HoldTimerExpired, holdNotif!.ErrorCode);
        Assert.Equal(BgpConstants.SubError.Unspecific, holdNotif.SubErrorCode);

        // RunAsync should complete (it self-cancels after HoldTimerLoopAsync returns).
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.IsEstablished, "session must transition out of Established after hold expiry");
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
