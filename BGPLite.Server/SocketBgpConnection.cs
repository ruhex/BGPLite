using System.Net.Sockets;

namespace BGPLite.Server;

/// <summary>
/// Production <see cref="IBgpConnection"/> over a connected <see cref="Socket"/> wrapped in a
/// <see cref="NetworkStream"/> (owns the socket). Replaces the direct <c>_socket</c>/<c>_stream</c>
/// fields that <see cref="BgpSession"/> previously held (#96).
/// <para>
/// #160 claimed <see cref="Socket.SendTimeout"/> = 60s as a "kernel-level backstop" for stuck
/// sends — but per the .NET documentation SendTimeout applies to SYNCHRONOUS Send calls only, so
/// every async write was effectively unbounded (#252): a peer that stops reading (TCP zero window)
/// pinned <c>WriteAsync</c> until the OS retransmission timeout (~15 min), holding the session's
/// send lock and paralyzing the keepalive loop. <see cref="WriteAsync"/> now enforces the per-send
/// budget itself with a linked CTS: when the budget fires the pending write is aborted and
/// surfaced as <see cref="IOException"/> (dead connection), regardless of the caller's token.
/// </para>
/// </summary>
internal sealed class SocketBgpConnection : IBgpConnection
{
    /// <summary>Per-send budget: how long a single WriteAsync may block on a non-reading peer (#160/#252).</summary>
    private const int DefaultSendTimeoutMs = 60_000;

    private readonly int _sendTimeoutMs;

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private int _disposed; // 0 = not disposed, 1 = disposed. Atomic CAS (matches BgpSession.Dispose).

    public SocketBgpConnection(Socket socket) : this(socket, DefaultSendTimeoutMs) { }

    /// <summary>Test seam: the per-send budget is injectable so the timeout is testable in milliseconds.</summary>
    internal SocketBgpConnection(Socket socket, int sendTimeoutMs)
    {
        _socket = socket;
        _sendTimeoutMs = sendTimeoutMs;
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

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        // #252: Socket.SendTimeout does not bound async writes — enforce the budget here so a
        // non-reading peer can never pin the send lock for minutes. The linked CTS also honors the
        // caller's token; distinguishing "budget fired" from "caller cancelled" keeps the benign
        // cancellation contract of the send paths intact.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_sendTimeoutMs);
        try
        {
            await _stream.WriteAsync(buffer, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller-initiated cancel — the send paths treat this as a normal cancelled send
        }
        catch (OperationCanceledException)
        {
            // The per-send budget fired: the peer is not reading. Aborting an in-flight socket
            // write leaves the connection unusable — surface it as the dead-connection exception
            // every send path already handles (same as a reset), not as a benign cancellation.
            throw new IOException($"Send timed out after {_sendTimeoutMs} ms — peer is not reading (TCP zero window)");
        }
    }

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
