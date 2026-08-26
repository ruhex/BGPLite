using System.Diagnostics;
using System.Threading.Channels;
using System.Net;
using System.Net.Sockets;
using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// #252: the per-send budget in SocketBgpConnection. Socket.SendTimeout does not apply to async
/// writes, so a peer that stops reading (TCP zero window) previously pinned WriteAsync — and the
/// session's send lock — until the OS retransmission timeout (minutes). A real socket pair where
/// the receiving side never reads exercises the timeout end-to-end.
/// </summary>
public class SendTimeoutTests
{
    [Fact]
    public async Task WriteAsync_AbortsWithinBudget_WhenPeerStopsReading()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(listener.LocalEndPoint!);
        using var server = listener.Accept();
        server.ReceiveBufferSize = 8 * 1024; // shrink kernel buffers so the send side fills fast
        client.SendBufferSize = 8 * 1024;

        using var connection = new SocketBgpConnection(client, sendTimeoutMs: 200);

        var sw = Stopwatch.StartNew();
        // 8 MB into a peer that never reads: kernel buffers fill and the write blocks — only the
        // per-send budget can break it (the caller token is never cancelled).
        var ex = await Assert.ThrowsAsync<IOException>(
            () => connection.WriteAsync(new byte[8 * 1024 * 1024], CancellationToken.None).AsTask());
        sw.Stop();

        Assert.Contains("timed out", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"the per-send budget must bound the write, took {sw.Elapsed}");
    }

    [Fact]
    public async Task WriteAsync_AfterAbortedSend_FailsFastInsteadOfAppending()
    {
        // #285: aborting a write does not roll it back — the kernel keeps whatever it already
        // accepted, so the peer is left mid-frame. A later write would be read by the peer as that
        // truncated frame's payload, so the connection must refuse it instead.
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(listener.LocalEndPoint!);
        using var server = listener.Accept();
        server.ReceiveBufferSize = 8 * 1024;
        client.SendBufferSize = 8 * 1024;

        using var connection = new SocketBgpConnection(client, sendTimeoutMs: 200);

        await Assert.ThrowsAsync<IOException>(
            () => connection.WriteAsync(new byte[8 * 1024 * 1024], CancellationToken.None).AsTask());

        // The second write must fail immediately — not block for another budget window, and not
        // reach the socket at all.
        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<IOException>(
            () => connection.WriteAsync(new byte[19], CancellationToken.None).AsTask());
        sw.Stop();

        Assert.Contains("unusable", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(150),
            $"a faulted connection must reject writes without touching the socket, took {sw.Elapsed}");
    }

    [Fact]
    public async Task WriteAsync_HonorsCallerCancellation_WithDifferentException()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(listener.LocalEndPoint!);
        using var server = listener.Accept();
        server.ReceiveBufferSize = 8 * 1024;
        client.SendBufferSize = 8 * 1024;

        using var connection = new SocketBgpConnection(client, sendTimeoutMs: 60_000);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Caller-initiated cancellation surfaces as OperationCanceledException — the benign-cancel
        // contract of the send paths — not as the dead-connection IOException.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => connection.WriteAsync(new byte[8 * 1024 * 1024], cts.Token).AsTask());
    }

    [Fact]
    public async Task RefreshRoutes_SendFailure_TearsDownTheSession()
    {
        // #285: RefreshCycleAsync used to swallow the send IOException in its generic
        // catch (Exception) and leave the session Established. After a budget abort the peer sits
        // inside a truncated frame (see WriteAsync_AfterAbortedSend_FailsFastInsteadOfAppending),
        // so every later frame is read as that frame's payload — silent route corruption with both
        // sides reporting a healthy session.
        //
        // The failure is injected through the IBgpConnection seam rather than by stalling a real
        // peer: kernel socket buffers are auto-tuned (macOS loopback absorbed a 50 000-route dump
        // whole despite SO_RCVBUF=8K), so a socket-driven version of this test asserts on the
        // host's buffering, not on the session's behaviour. The transport half — that an aborted
        // write raises IOException and poisons the connection — is covered by the two
        // SocketBgpConnection tests above.
        var connection = new FailableConnection();
        var routeTable = new RouteTable();
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        using var session = new BgpSession(
            connection,
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            routeTable,
            AllowAllFilter.Instance,
            new BgpMetrics(),
            NullLogger<BgpSession>.Instance);

        var runTask = session.RunAsync();
        connection.EnqueueMessage(new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = 0,
            RouterId = 0x0A000002,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)]
        });
        connection.EnqueueMessage(BgpKeepaliveMessage.Instance);

        for (var i = 0; i < 200 && !session.IsEstablished; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        Assert.True(session.IsEstablished, "session must reach Established before the refresh");

        routeTable.AddOrUpdate(new Route { Prefix = 0x0A000000, PrefixLength = 8, NextHop = 0x7F000001 });
        connection.FailNextWrites = true;

        await session.RefreshRoutesAsync();

        // FaultSession cancels the session CTS; RunAsync unwinds its loops and transitions to Idle
        // on another task, so allow a bounded window rather than asserting on the instant
        // RefreshRoutesAsync returns.
        for (var i = 0; i < 200 && session.IsEstablished; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10));

        Assert.False(session.IsEstablished,
            "a refresh whose send failed must tear the session down, not leave it Established (#285)");

        try { await runTask.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { /* session torn down */ }
    }

    /// <summary>
    /// An <see cref="IBgpConnection"/> that scripts inbound messages and can be switched to fail
    /// every subsequent write with <see cref="IOException"/> — the exception
    /// <see cref="SocketBgpConnection.WriteAsync"/> raises when the per-send budget aborts a write.
    /// </summary>
    private sealed class FailableConnection : IBgpConnection
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private readonly Queue<byte> _readBuffer = new();

        public volatile bool FailNextWrites;

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

                var toCopy = Math.Min(chunk.Length, buffer.Length - offset);
                for (var i = 0; i < toCopy; i++) buffer.Span[offset++] = chunk[i];
                for (var i = toCopy; i < chunk.Length; i++) _readBuffer.Enqueue(chunk[i]);
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            if (FailNextWrites)
                throw new IOException("Send timed out — peer is not reading (TCP zero window)");
            return default;
        }

        public bool IsPeerClosed => false;

        public void Dispose() => _inbound.Writer.TryComplete();
    }
}
