using System.Net;
using System.Net.Sockets;
using BGPLite.Protocol;

namespace BGPLite.Tests;

/// <summary>
/// #15 phase 2: MP_REACH_NLRI / MP_UNREACH_NLRI (RFC 4760) wire codec for IPv6/Unicast —
/// attribute VALUE encode/decode roundtrips at boundary lengths, the RFC 2545 32-byte next-hop
/// form, and malformed-input handling (truncated header/body, unsupported AFI/SAFI, bad
/// next-hop length).
/// </summary>
public class MpReachCodecTests
{
    private static readonly byte[] Nh16 = new byte[16];
    private static IpPrefix V6(byte length) => new(BgpConstants.ToUInt128(IPAddress.Parse("2001:db8::")), length, isIpv4: false);

    [Fact]
    public void Encode_ExactByteLayout()
    {
        var prefixes = new List<IpPrefix> { new(ToV6("20010db8000000000000000000000001"), 128, isIpv4: false) };
        var data = MpReachCodec.EncodeMpReachV6(ToV6("20010db8ffff00000000000000000001"), prefixes);

        // AFI=2 / SAFI=1 / NH-Len=16 / next-hop / reserved / length=128 / 16 bytes of 2001:0db8::1
        Assert.Equal(
        [
            0x00, 0x02,             // AFI = 2
            0x01,                   // SAFI = 1
            16,                     // next-hop length
            0x20, 0x01, 0x0d, 0xb8, 0xff, 0xff, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,   // next hop
            0x00,                   // reserved
            128,                    // NLRI: /128
            0x20, 0x01, 0x0d, 0xb8, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,   // 2001:db8::1
        ], data);
    }

    [Fact]
    public void Roundtrip_MultiplePrefixes()
    {
        var prefixes = new List<IpPrefix>
        {
            V6(0),    // ::/0
            V6(32),   // 2001:db8::/32
            V6(64),   // 2001:db8::/64 (host bits masked)
            new(ToV6("20010db8000000000000000000000001"), 128, isIpv4: false),
        };
        var nextHop = ToV6("fe800000000000000000000000000001");

        var encoded = MpReachCodec.EncodeMpReachV6(nextHop, prefixes);
        var decoded = MpReachCodec.DecodeMpReachV6(encoded);

        Assert.Equal(nextHop, decoded.NextHop);
        Assert.Equal(prefixes.Count, decoded.Prefixes.Count);
        for (var i = 0; i < prefixes.Count; i++)
            Assert.Equal(prefixes[i], decoded.Prefixes[i]);
    }

    [Fact]
    public void Decode_AcceptsRfc2545_32ByteNextHop()
    {
        // RFC 2545 §3: global (16 bytes) + link-local (16 bytes) = 32-byte next-hop form.
        // header: AFI(2) + SAFI(1) + NH-Len(1) + NH(32) + Reserved(1) = 37, then /64 NLRI (9) = 46.
        var value = new byte[46];
        value[0] = 0; value[1] = 2; value[2] = 1; value[3] = 32;
        for (var i = 0; i < 16; i++) value[4 + i] = 0x20;         // global
        for (var i = 0; i < 16; i++) value[20 + i] = 0xFE;        // link-local
        value[36] = 0;                                             // reserved
        value[37] = 64;                                            // /64 prefix
        for (var i = 0; i < 8; i++) value[38 + i] = 0x20;         // 2001:...

        var decoded = MpReachCodec.DecodeMpReachV6(value);

        // The GLOBAL address (first 16 bytes of 0x20) is used; link-local (0xFE) skipped.
        Assert.Equal((UInt128)0x20, decoded.NextHop >> 120);   // top byte = global's first byte
        Assert.Single(decoded.Prefixes);
    }

    [Fact]
    public void Decode_BadNextHopLength_Throws()
    {
        var value = new byte[21];
        value[0] = 0; value[1] = 2; value[2] = 1; value[3] = 8; // NH len 8 — invalid

        Assert.Throws<BgpParseException>(() => MpReachCodec.DecodeMpReachV6(value));
    }

    [Fact]
    public void Decode_WrongAfi_Throws()
    {
        var value = new byte[21];
        value[0] = 0; value[1] = 1; value[2] = 1; value[3] = 16; // AFI=1 — IPv4, not supported here

        Assert.Throws<BgpParseException>(() => MpReachCodec.DecodeMpReachV6(value));
    }

    [Fact]
    public void Decode_Truncated_Throws()
    {
        Assert.Throws<BgpParseException>(() => MpReachCodec.DecodeMpReachV6(new byte[10]));
    }

    [Fact]
    public void EncodeUnreach_DecodeUnreach_Roundtrip()
    {
        var prefixes = new List<IpPrefix> { V6(48), V6(128) };
        var encoded = MpReachCodec.EncodeMpUnreachV6(prefixes);

        // AFI=2 / SAFI=1 header
        Assert.Equal(0x00, encoded[0]);
        Assert.Equal(0x02, encoded[1]);
        Assert.Equal(0x01, encoded[2]);

        var decoded = MpReachCodec.DecodeMpUnreachV6(encoded);
        Assert.Equal(2, decoded.Count);
        Assert.Equal(prefixes[0], decoded[0]);
        Assert.Equal(prefixes[1], decoded[1]);
    }

    [Fact]
    public void DecodeUnreach_Truncated_Throws()
    {
        Assert.Throws<BgpParseException>(() => MpReachCodec.DecodeMpUnreachV6(new byte[2]));
    }

    [Fact]
    public void ReaderExtracts_MpReachV6()
    {
        var attr = new PathAttribute { Flags = 0x80, TypeCode = 14, Data = MpReachCodec.EncodeMpReachV6(0x2001, [V6(128)]) };
        var update = new BgpUpdateMessage
        {
            WithdrawnRoutes = [],
            PathAttributes = [AttributeHelper.WriteOrigin(0), AttributeHelper.WriteAsPath([65002], fourByteAsn: true), attr],
            Nlri = []
        };
        var buf = new byte[512];
        var n = BgpMessageWriter.WriteMessage(update, buf);
        var read = (BgpUpdateMessage)BgpMessageReader.ReadMessage(buf.AsSpan(0, n));
        Assert.NotNull(read.MpReachV6);
        Assert.True(read.MpReachV6 is { } reach && reach.Prefixes.Count == 1);
    }

    [Fact]
    public void Encode_NonCanonicalPrefix_Masked()
    {
        var prefix = new IpPrefix(ToV6("20010db8012345670000000000000001"), 48, isIpv4: false);

        var encoded = MpReachCodec.EncodeMpReachV6(0, [prefix]);
        var decoded = MpReachCodec.DecodeMpReachV6(encoded);

        // The host bits below /48 were masked by the IpPrefix constructor.
        Assert.Equal(new IpPrefix(ToV6("20010db8012300000000000000000000"), 48, isIpv4: false), decoded.Prefixes[0]);
    }

    private static UInt128 ToV6(string hex)
    {
        UInt128 value = 0;
        foreach (var c in hex) value = (value << 4) | (UInt128)Uri.FromHex(c);
        return value;
    }
}
