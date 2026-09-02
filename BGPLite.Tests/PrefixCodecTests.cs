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
    public void Roundtrip_BoundaryLengths()
    {
        // #392: every byte-aligned boundary plus the non-aligned ones around it. Encode masks to
        // the network address, so the expected value is the address & mask for each length.
        var lengths = new[] { (byte)1, (byte)7, (byte)9, (byte)23, (byte)25, (byte)31 };
        foreach (var length in lengths)
        {
            var mask = length == 0 ? 0u : 0xFFFFFFFFu << (32 - length);
            var expected = new IpPrefix(0x0A1B2C30u & mask, length);

            var buf = new byte[8];
            var written = PrefixCodec.Encode(expected, buf);

            var (decoded, consumed) = PrefixCodec.Decode(buf.AsSpan(0, written));

            Assert.Equal(written, consumed);   // the whole frame is exactly one NLRI
            Assert.Equal(expected, decoded);
        }
    }

    [Fact]
    public void Decode_MasksHostBits()
    {
        // #392: host bits on the wire are masked to the network address at the parse boundary —
        // a /23 NLRI carries three data bytes, so its low-order bits are host bits: "10.0.1.0/23"
        // must decode to 10.0.0.0/23 (the RouteTable key), never 10.0.1.0.
        var nlri = new byte[] { 23, 10, 0, 1 };

        var (decoded, consumed) = PrefixCodec.Decode(nlri);

        Assert.Equal(new IpPrefix(0x0A000000, 23), decoded);
        Assert.Equal(4, consumed);
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

    private static IpPrefix V6Prefix() =>
        new(ToUInt128Hex("20010db8000000000000000000000001"), 128, isIpv4: false);

    private static UInt128 ToUInt128Hex(string hex)
    {
        UInt128 value = 0;
        foreach (var c in hex) value = (value << 4) | (UInt128)Uri.FromHex(c);
        return value;
    }

    [Fact]
    public void Encode_LengthAbove32_Throws()
    {
        // The IpPrefix constructor itself rejects IPv4 lengths above 32 — the codec never sees
        // an out-of-domain value through a canonical prefix.
        Assert.Throws<ArgumentOutOfRangeException>(() => new IpPrefix(0xC0A80000, (byte)33));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    public void Encode_Ipv6_Accepts_FullRange_AndRoundtrips(byte length)
    {
        var address = ToUInt128Hex("20010db8000000000000000000000001");
        var prefix = new IpPrefix(address, length, isIpv4: false);
        var buf = new byte[17];
        var written = PrefixCodec.Encode(prefix, buf);

        var (decoded, consumed) = PrefixCodec.Decode6(buf.AsSpan(0, written));
        Assert.Equal(written, consumed);
        Assert.Equal(prefix.Address, decoded.Address);
        Assert.False(decoded.IsIpv4);
    }

    [Theory]
    [InlineData(33)]
    [InlineData(64)]
    [InlineData(255)]
    public void Decode_LengthAbove32_Throws(int badLength)
    {
        // #222: a malformed NLRI prefix-length byte is a wire-level error and now surfaces as
        // BgpParseException (Update Message Error) so the session can treat-as-withdraw instead of
        // tearing down. Previously this was ArgumentOutOfRangeException which escaped the read loop.
        var buffer = new byte[8] { (byte)badLength, 0xC0, 0xA8, 0x00, 0x00, 0, 0, 0 };

        var ex = Assert.Throws<BgpParseException>(() => PrefixCodec.Decode(buffer));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.InvalidNetworkField, ex.SubErrorCode);
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
        // #222: a truncated NLRI is now BgpParseException (Update Message Error), not
        // ArgumentOutOfRangeException — so the session treats it as withdraw, not teardown.
        var buffer = Array.Empty<byte>();
        var ex = Assert.Throws<BgpParseException>(() =>
        {
            var span = new ReadOnlySpan<byte>(buffer);
            PrefixCodec.Decode(span);
        });
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.InvalidNetworkField, ex.SubErrorCode);
    }

    [Fact]
    public void Decode_BufferTooSmallForPrefix_Throws()
    {
        // /24 needs 4 bytes total (1 length + 3 data). 2-byte buffer is truncated
        // mid-prefix and must be rejected before any read past the length byte.
        // #222: surfaces as BgpParseException (Update Message Error).
        var buffer = new byte[] { 24, 0xC0 };

        var ex = Assert.Throws<BgpParseException>(() => PrefixCodec.Decode(buffer));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
    }
}
