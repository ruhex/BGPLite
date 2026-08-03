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
    private int _disposed; // 0 = not disposed, 1 = disposed. Atomic CAS (matches BgpSession.Dispose).

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

    public bool IsPeerClosed
    {
        get
        {
            // Disposed ⇒ treat as closed (avoids ObjectDisposedException from Poll/Available).
            if (Volatile.Read(ref _disposed) == 1) return true;
            try
            {
                // Non-blocking EOF peek: Poll(SelectRead) returns true for {data, FIN, RST, terminated};
                // Available==0 rules out data-available, leaving closed/reset/terminated. This mirrors
                // the standard select()+FIONREAD pattern for read-readiness on a POSIX socket.
                // Dotnet docs note Poll cannot detect abrupt disconnects (broken cable, ungraceful
                // kill) — those surface only via a subsequent send/recv; that limitation is acceptable
                // here because the probe runs only AFTER a read returned OCE/IOException.
                return _socket.Poll(0, SelectMode.SelectRead) && _socket.Available == 0;
            }
            catch (SocketException)
            {
                // Poll/Available surfaced a socket-level error, including a connection reset by the
                // peer — treat as closed so the EOF↔cancel race handling reaches the explicit close
                // path rather than masking the close as a pure cancellation (#217).
                return true;
            }
            catch (ObjectDisposedException)
            {
                // Dispose raced between the _disposed check above and the Poll call. Treat as closed.
                return true;
            }
        }
    }

    public void Dispose()
    {
        // Atomic test-and-set (CodeRabbit #178): a volatile bool check-then-set races under
        // concurrent Dispose() — two callers can both pass the check before either writes,
        // double-disposing _stream/_socket. Interlocked.Exchange makes the first caller win and
        // the rest no-op, matching BgpSession.Dispose's pattern.
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        // Disposing the NetworkStream (ownsSocket:true) closes the socket transitively. The extra
        // _socket.Dispose() is redundant-but-harmless (matches the prior BgpSession.Dispose pattern).
        _stream.Dispose();
        _socket.Dispose();
    }
}
