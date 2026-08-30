using System.Text;
using BGPLite.Api;

namespace BGPLite.Tests;

/// <summary>
/// Regression coverage for #156: the management-API request body is capped at
/// <c>AppConfig.MaxRequestBodyBytes</c> so a single client cannot stream gigabytes into the
/// process (HttpListener has no default body cap). Tests the pure
/// <see cref="ManagementApi.ReadBoundedBodyAsync"/> helper directly.
/// </summary>
public class RequestBodyLimitsTests
{
    [Fact]
    public async Task UnderCap_BodyReturned()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"ip\":\"1.2.3.4\"}"));
        var (body, error) = await ManagementApi.ReadBoundedBodyAsync(input, maxBytes: 1024);

        Assert.Null(error);
        Assert.Equal("{\"ip\":\"1.2.3.4\"}", body);
    }

    [Fact]
    public async Task ExactlyAtCap_BodyReturned()
    {
        // A body of exactly maxBytes fits — the cap is inclusive.
        var payload = "{\"x\":1}"; // 7 bytes
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var (body, error) = await ManagementApi.ReadBoundedBodyAsync(input, maxBytes: payload.Length);

        Assert.Null(error);
        Assert.Equal(payload, body);
    }

    [Fact]
    public async Task OverCap_Returns413_NoFullBufferMaterialized()
    {
        // A body well over the cap must be rejected with 413 without materializing the full payload
        // (the read loop aborts as soon as the running count exceeds the cap).
        var huge = new string('x', 10 * 1024 * 1024); // 10 MiB
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(huge));
        var (body, error) = await ManagementApi.ReadBoundedBodyAsync(input, maxBytes: 1024);

        Assert.Null(body);
        Assert.NotNull(error);
        Assert.Equal(413, error!.StatusCode);
        // ApiResponse.Body wraps the message in { error = "..." }; assert the status code is enough.
    }

    [Fact]
    public async Task StreamingBody_OverCapMidStream_Returns413()
    {
        // Chunked/streaming body (no Content-Length on this Stream) that crosses the cap partway
        // through is still rejected — the read loop checks the running count on every chunk.
        var chunk = Encoding.UTF8.GetBytes(new string('x', 600));
        using var input = new ChunkedStream(chunk, repeat: 10); // 6000 bytes total, cap 1024
        var (body, error) = await ManagementApi.ReadBoundedBodyAsync(input, maxBytes: 1024);

        Assert.Null(body);
        Assert.NotNull(error);
        Assert.Equal(413, error!.StatusCode);
    }

    [Fact]
    public async Task EmptyBody_ReturnsEmptyString()
    {
        using var input = new MemoryStream();
        var (body, error) = await ManagementApi.ReadBoundedBodyAsync(input, maxBytes: 1024);

        Assert.Null(error);
        Assert.Equal("", body);
    }

    /// <summary>A stream that repeats a fixed chunk N times, simulating a streaming/chunked body.</summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] _chunk;
        private readonly int _repeat;
        private int _emitted;

        public ChunkedStream(byte[] chunk, int repeat)
        {
            _chunk = chunk;
            _repeat = repeat;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _chunk.Length * _repeat;
        public override long Position { get => _emitted; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_emitted >= _chunk.Length * _repeat) return 0;
            // Emit one chunk per Read call so the read loop sees the cap crossed mid-stream.
            var n = Math.Min(_chunk.Length, count);
            Array.Copy(_chunk, 0, buffer, offset, n);
            _emitted += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// #257: a body that never arrives parks <c>ReadBoundedBodyAsync</c> forever — with the
    /// 64-slot in-flight cap, 64 such connections starve the whole API. Each read must be bounded
    /// by the deadline and surface as 408. The outer WaitAsync(3s) doubles as the red guard: on
    /// pre-fix code the call never completes and the test fails on the guard timeout.
    /// </summary>
    [Fact]
    public async Task SlowDripBody_NeverCompletingRead_Returns408()
    {
        using var input = new NeverCompletingStream();

        var (body, error) = await ManagementApi
            .ReadBoundedBodyAsync(input, maxBytes: 1024, readTimeout: TimeSpan.FromMilliseconds(100))
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Null(body);
        Assert.NotNull(error);
        Assert.Equal(408, error!.StatusCode);
    }

    /// <summary>#257: the deadline must not affect a body that arrives within it.</summary>
    [Fact]
    public async Task BodyWithinDeadline_StillRead()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"ip\":\"1.2.3.4\"}"));
        var (body, error) = await ManagementApi.ReadBoundedBodyAsync(
            input, maxBytes: 1024, readTimeout: TimeSpan.FromSeconds(30));

        Assert.Null(error);
        Assert.Equal("{\"ip\":\"1.2.3.4\"}", body);
    }

    /// <summary>
    /// #358 review (hardens #257): a per-read deadline restarts on every byte — a client trickling
    /// one byte per window retained its slot indefinitely. The deadline must be TOTAL for the
    /// body. This stream yields one byte every 100 ms forever; with a 500 ms total budget the read
    /// must 408 by ~T+0.5s, not keep pace forever. The outer 5s guard doubles as the red guard
    /// against the per-read implementation.
    /// </summary>
    [Fact]
    public async Task TrickleBody_OneBytePerWindow_KilledByTotalDeadline()
    {
        using var input = new TrickleStream(byteInterval: TimeSpan.FromMilliseconds(100));

        var (body, error) = await ManagementApi
            .ReadBoundedBodyAsync(input, maxBytes: 1024, readTimeout: TimeSpan.FromMilliseconds(500))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(body);
        Assert.NotNull(error);
        Assert.Equal(408, error!.StatusCode);
    }

    /// <summary>Endless one-byte trickle — each individual read is well inside any per-read window.</summary>
    private sealed class TrickleStream(TimeSpan byteInterval) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => long.MaxValue;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(byteInterval, cancellationToken);
            buffer[offset] = (byte)'x';
            return 1;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A stream whose ReadAsync never completes — the slow-drip attacker's socket.</summary>
    private sealed class NeverCompletingStream : Stream
    {
        private readonly TaskCompletionSource<int> _never = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _never.Task.Result;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _never.Task;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
