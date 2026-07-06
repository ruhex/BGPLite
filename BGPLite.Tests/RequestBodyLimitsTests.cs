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
}
