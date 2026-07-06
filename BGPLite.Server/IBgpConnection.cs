namespace BGPLite.Server;

/// <summary>
/// The transport seam for <see cref="BgpSession"/> (#96): a minimal duplex byte-channel that the
/// FSM reads from and writes to, decoupled from the concrete <c>Socket</c>/<c>NetworkStream</c>.
/// The production implementation is <see cref="SocketBgpConnection"/>; tests can substitute a fake
/// that scripts inbound messages and captures outbound bytes — making the FSM (OPEN exchange,
/// teardown latch, hold-timer expiry) unit-testable without real loopback TCP sockets and without
/// the multi-second drain scaffolding the socket tests require.
/// <para>
/// <b>Locking.</b> Implementations must be lock-free — the BGP framing serialization
/// (<c>_sendLock</c> inside <c>BgpSession.SendMessageAsync</c>) is a BGP concern and stays in the
/// session, not in the transport. A <c>WriteAsync</c> may be called concurrently with <c>ReadExactAsync</c>
/// (read and write run on separate tasks); two concurrent <c>WriteAsync</c> calls are the caller's
/// responsibility to prevent.
/// </para>
/// <para>
/// <b>EOF semantics.</b> <c>ReadExactAsync</c> fills <paramref name="buffer"/> completely or throws —
/// a zero-byte read (peer closed) surfaces as <c>IOException("Connection closed by peer")</c>,
/// matching <c>NetworkStream</c>'s contract so the FSM's existing error handling is unchanged.
/// </para>
/// </summary>
public interface IBgpConnection : IDisposable
{
    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes. Returns when the buffer is full, or
    /// throws <c>IOException</c> on a zero-byte read (peer closed the connection). Honors
    /// <paramref name="cancellationToken"/> for cooperative cancellation.
    /// </summary>
    ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="buffer"/> to the peer. The caller is responsible for serialization
    /// (the session's <c>_sendLock</c>); this method performs no locking of its own. Honors
    /// <paramref name="cancellationToken"/>.
    /// </summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
}
