using BGPLite.Protocol;

namespace BGPLite.Tests;

public class PrefixCodecTests
{
    [Fact]
    public void Encode_24bitPrefix_3bytes()
    {
        var prefix = new IpPrefix(0xC0A80000, 24); // 192.168.0.0/24
        var buffer = new byte[8];
        var written = PrefixCodec.Encode(prefix, buffer);

        Assert.Equal(4, written);
        Assert.Equal((byte)24, buffer[0]);
        Assert.Equal(0xC0, buffer[1]);
        Assert.Equal(0xA8, buffer[2]);
        Assert.Equal(0x00, buffer[3]);
    }

    [Fact]
    public void Encode_8bitPrefix_2bytes()
    {
        var prefix = new IpPrefix(0x0A000000, 8); // 10.0.0.0/8
        var buffer = new byte[8];
        var written = PrefixCodec.Encode(prefix, buffer);

        Assert.Equal(2, written);
        Assert.Equal((byte)8, buffer[0]);
        Assert.Equal(0x0A, buffer[1]);
    }

    [Fact]
    public void Encode_32bitPrefix_5bytes()
    {
        var prefix = new IpPrefix(0x01020304, 32); // 1.2.3.4/32
        var buffer = new byte[8];
        var written = PrefixCodec.Encode(prefix, buffer);

        Assert.Equal(5, written);
        Assert.Equal((byte)32, buffer[0]);
        Assert.Equal(0x01, buffer[1]);
        Assert.Equal(0x02, buffer[2]);
        Assert.Equal(0x03, buffer[3]);
        Assert.Equal(0x04, buffer[4]);
    }

    [Fact]
    public void Encode_DefaultRoute_1byte()
    {
        var prefix = new IpPrefix(0, 0); // 0.0.0.0/0
        var buffer = new byte[8];
        var written = PrefixCodec.Encode(prefix, buffer);

        Assert.Equal(1, written);
        Assert.Equal((byte)0, buffer[0]);
    }

    [Fact]
    public void Roundtrip_VariousPrefixes()
    {
        var prefixes = new[]
        {
            new IpPrefix(0xC0A80000, 24),
            new IpPrefix(0x0A000000, 8),
            new IpPrefix(0x01020304, 32),
            new IpPrefix(0, 0),
            new IpPrefix(0xAC100000, 20)
        };

        var buffer = new byte[64];
        var written = PrefixCodec.EncodeList(prefixes, buffer);

        var decoded = new IpPrefix[prefixes.Length];
        var count = PrefixCodec.DecodeList(buffer, written, decoded);

        Assert.Equal(prefixes.Length, count);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(prefixes[i].Address, decoded[i].Address);
            Assert.Equal(prefixes[i].Length, decoded[i].Length);
        }
    }

    [Theory]
    [InlineData(33)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(255)]
    public void Encode_LengthAbove32_Throws(int badLength)
    {
        var prefix = new IpPrefix(0xC0A80000, (byte)badLength);
        var buffer = new byte[8];

        Assert.Throws<ArgumentOutOfRangeException>(() => PrefixCodec.Encode(prefix, buffer));
    }

    [Theory]
    [InlineData(33)]
    [InlineData(64)]
    [InlineData(255)]
    public void Decode_LengthAbove32_Throws(int badLength)
    {
        var buffer = new byte[8] { (byte)badLength, 0xC0, 0xA8, 0x00, 0x00, 0, 0, 0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => PrefixCodec.Decode(buffer));
    }

    [Fact]
    public void Encode_DoesNotWriteBeyondBuffer_ForValidPrefix()
    {
        // Regression: PrefixCodec previously performed OOB writes for length > 32
        // and even for valid lengths it must not touch bytes past the encoded span.
        var buffer = new byte[] { 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA };
        var prefix = new IpPrefix(0xC0A80000, 24);

        var written = PrefixCodec.Encode(prefix, buffer);

        Assert.Equal(4, written);
        Assert.Equal(0xAA, buffer[4]);
        Assert.Equal(0xAA, buffer[5]);
        Assert.Equal(0xAA, buffer[6]);
        Assert.Equal(0xAA, buffer[7]);
    }

    [Fact]
    public void Encode_EmptyBuffer_Throws()
    {
        var prefix = new IpPrefix(0xC0A80000, 24);
        var buffer = Array.Empty<byte>();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var span = new Span<byte>(buffer);
            PrefixCodec.Encode(prefix, span);
        });
    }

    [Fact]
    public void Encode_BufferTooSmallForPrefix_Throws()
    {
        // /24 needs 4 bytes total (1 length + 3 data). 3-byte buffer cannot hold it.
        var prefix = new IpPrefix(0xC0A80000, 24);
        var buffer = new byte[3];

        Assert.Throws<ArgumentOutOfRangeException>(() => PrefixCodec.Encode(prefix, buffer));
    }

    [Fact]
    public void Decode_EmptyBuffer_Throws()
    {
        var buffer = Array.Empty<byte>();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var span = new ReadOnlySpan<byte>(buffer);
            PrefixCodec.Decode(span);
        });
    }

    [Fact]
    public void Decode_BufferTooSmallForPrefix_Throws()
    {
        // /24 needs 4 bytes total (1 length + 3 data). 2-byte buffer is truncated
        // mid-prefix and must be rejected before any read past the length byte.
        var buffer = new byte[] { 24, 0xC0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => PrefixCodec.Decode(buffer));
    }
}
