using System.Threading.Channels;
using BGPLite.Protocol;
using BGPLite.Server;

namespace BGPLite.Tests;

/// <summary>
/// Scripts inbound frames and discards outbound bytes; reads block until a frame arrives. Driving a
/// <see cref="BgpSession"/> through the <c>IBgpConnection</c> seam (#96) instead of loopback sockets
/// makes frame delivery deterministic and sidesteps the timing flakiness #302 documents. Shared by
/// the fixtures that need a session established without a real socket.
/// </summary>
internal sealed class ScriptedConnection : IBgpConnection
{
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
    private readonly Queue<byte> _readBuffer = new();
    private int _pending;

    /// <summary>True once every enqueued frame has been fully handed to the read loop.</summary>
    public bool Drained => Volatile.Read(ref _pending) == 0 && _readBuffer.Count == 0;

    /// <summary>Queues raw bytes for the read loop to consume as one delivery.</summary>
    public void EnqueueFrame(byte[] frame)
    {
        Interlocked.Increment(ref _pending);
        _inbound.Writer.TryWrite(frame);
    }

    /// <summary>Encodes <paramref name="message"/> with the production writer and queues the frame.</summary>
    public void EnqueueMessage(BgpMessage message)
    {
        var buf = new byte[BgpMessageWriter.GetBufferSize(message)];
        var n = BgpMessageWriter.WriteMessage(message, buf);
        EnqueueFrame(buf[..n]);
    }

    /// <inheritdoc />
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
            Interlocked.Decrement(ref _pending);
        }
    }

    private readonly List<byte[]> _sent = [];

    /// <summary>Outbound frames, copied because SendMessageAsync returns its buffer to the pool.</summary>
    public IReadOnlyList<byte[]> Sent { get { lock (_sent) return [.. _sent]; } }

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        lock (_sent) _sent.Add(buffer.ToArray());
        return default;
    }

    /// <summary>Never reports a half-closed peer: teardown is driven explicitly by the fixtures.</summary>
    public bool IsPeerClosed => false;

    /// <summary>Completes the inbound channel so a blocked read unwinds as a closed connection.</summary>
    public void Dispose() => _inbound.Writer.TryComplete();
}
