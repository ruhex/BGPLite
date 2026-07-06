using System.Net.Sockets;

namespace BGPLite.Server;

/// <summary>
/// Production <see cref="IBgpConnection"/> over a connected <see cref="Socket"/> wrapped in a
/// <see cref="NetworkStream"/> (owns the socket). Replaces the direct <c>_socket</c>/<c>_stream</c>
/// fields that <see cref="BgpSession"/> previously held (#96).
/// <para>
/// Sets <see cref="Socket.SendTimeout"/> = 60s as a kernel-level backstop (#160): a peer that stops
/// reading (TCP receive window full) blocks <c>WriteAsync</c> on the kernel send buffer until the
/// OS TCP retransmission timeout (minutes). The per-send <c>CancellationToken</c> is the primary
/// bound, but <c>SendTimeout</c> fires at the kernel level even if a future send path forgets to
/// thread the token. 60s matches the per-send budget.
/// </para>
/// </summary>
internal sealed class SocketBgpConnection : IBgpConnection
{
    /// <summary>Kernel-level send timeout backstop (#160). See class docs.</summary>
    private const int SendTimeoutMs = 60_000;

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private volatile bool _disposed;

    public SocketBgpConnection(Socket socket)
    {
        _socket = socket;
        // Kernel-level send backstop. Set before wrapping in NetworkStream so the option is in
        // place before any send. Safe to set on a connected socket.
        _socket.SendTimeout = SendTimeoutMs;
        // ownsSocket:true so disposing the stream transitively closes the socket — same ownership
        // semantics as the prior `new NetworkStream(socket, ownsSocket: true)` in BgpSession.
        _stream = new NetworkStream(_socket, ownsSocket: true);
    }

    public async ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer[totalRead..], cancellationToken);
            if (read == 0)
                throw new IOException("Connection closed by peer");
            totalRead += read;
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        => _stream.WriteAsync(buffer, cancellationToken);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Disposing the NetworkStream (ownsSocket:true) closes the socket transitively. The extra
        // _socket.Dispose() is redundant-but-harmless (matches the prior BgpSession.Dispose pattern).
        _stream.Dispose();
        _socket.Dispose();
    }
}
