using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BGPLite.Server;

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
}
