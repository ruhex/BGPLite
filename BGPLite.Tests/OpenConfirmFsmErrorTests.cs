using System.Net;
using System.Net.Sockets;
using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging;
using BGPLite.Contracts;

namespace BGPLite.Tests;

/// <summary>
/// #453 (RFC 4271 §8.2.2): an OPEN received in OpenConfirm is an FSM error regardless of body
/// validity — the handshake phase accepts only KEEPALIVE and NOTIFICATION. Pre-fix, a malformed
/// OPEN body threw <see cref="BgpParseException"/> with the Open Message Error code out of the
/// handshake read, escaped to <c>RunAsync</c>'s catch (#223 path) and answered NOTIFICATION 2/x —
/// misreporting "your OPEN body was malformed" when the real fault is sending a second OPEN.
/// The same message class in Established is already an FSM error (#427).
/// </summary>
public sealed class OpenConfirmFsmErrorTests
{
    [Fact]
    public async Task MalformedOpen_InOpenConfirm_IsFsmError_NotBodyError()
    {
        var (server, client) = ConnectedPair();
        using var clientSock = client;
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        using var session = new BgpSession(
            new SocketBgpConnection(server),
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            new NopLogger<BgpSession>());
        var runTask = session.RunAsync();

        // Drive to OpenConfirm: a valid OPEN; drain the server's OPEN + KEEPALIVE. The client has
        // not confirmed yet, so the session parks in OpenConfirm awaiting the KEEPALIVE.
        Send(client, new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = 0,
            RouterId = 0x0A000002,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)]
        });
        await DrainAsync(client, TimeSpan.FromSeconds(5));
        for (var i = 0; i < 50 && session.State != BgpFsmState.OpenConfirm; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        Assert.Equal(BgpFsmState.OpenConfirm, session.State);

        // Well-framed OPEN, body-invalid (the #427 construction): declares optParamsLen=5 over a
        // payload that carries none — ParseOpen throws Open Message Error before any FSM switch.
        var payload = new byte[10];
        payload[0] = 4;                       // version
        payload[1] = 0xFD; payload[2] = 0xE8; // My AS 65002
        payload[9] = 5;                       // optional-parameters length: mismatches the 0 present
        var open = BuildMessage(BgpMessageType.Open, payload);
        client.Send(open, 0, open.Length, SocketFlags.None);

        var sent = await DrainAsync(client, TimeSpan.FromSeconds(2));
        var notification = Assert.Single(sent.OfType<BgpNotificationMessage>());
        Assert.Equal(BgpConstants.Error.FiniteStateMachineError, notification.ErrorCode);
        Assert.Equal(BgpConstants.SubError.Unspecific, notification.SubErrorCode);
        Assert.False(session.IsEstablished, "an OPEN in OpenConfirm must reset the session (RFC 4271 §8.2.2)");

        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
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

    private static void Send(Socket s, BgpMessage msg)
    {
        var buffer = new byte[BgpMessageWriter.GetBufferSize(msg)];
        var written = BgpMessageWriter.WriteMessage(msg, buffer);
        s.Send(buffer, 0, written, SocketFlags.None);
    }

    private static byte[] BuildMessage(BgpMessageType type, byte[] payload)
    {
        var frame = new byte[BgpConstants.MessageHeaderSize + payload.Length];
        for (var i = 0; i < BgpConstants.MarkerSize; i++) frame[i] = 0xFF;
        frame[16] = (byte)((frame.Length >> 8) & 0xFF);
        frame[17] = (byte)(frame.Length & 0xFF);
        frame[18] = (byte)type;
        payload.CopyTo(frame, BgpConstants.MessageHeaderSize);
        return frame;
    }

    private static async Task<List<BgpMessage>> DrainAsync(Socket client, TimeSpan timeout)
    {
        var sent = new List<BgpMessage>();
        client.ReceiveTimeout = (int)timeout.TotalMilliseconds;
        var buf = new byte[4096];
        var deadline = DateTime.UtcNow + timeout;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                if (client.Poll(100_000, SelectMode.SelectRead)) // 100ms
                {
                    var available = client.Available;
                    if (available == 0) break; // peer closed
                    var got = client.Receive(buf, 0, Math.Min(available, buf.Length), SocketFlags.None);
                    if (got == 0) break;
                    ParseAll(buf, got, sent);
                }
            }
        }
        catch (SocketException) { /* timeout / peer closed */ }
        return sent;
    }

    private static void ParseAll(byte[] buffer, int length, List<BgpMessage> into)
    {
        var offset = 0;
        while (offset + BgpConstants.MessageHeaderSize <= length)
        {
            var msgLength = (buffer[offset + 16] << 8) | buffer[offset + 17];
            if (msgLength < BgpConstants.MinMessageSize || offset + msgLength > length)
                return; // partial tail — not enough bytes yet
            into.Add(BgpMessageReader.ReadMessage(buffer.AsSpan(offset, msgLength)));
            offset += msgLength;
        }
    }

    private sealed class NopLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
