using System.Buffers.Binary;
using System.Net;

namespace BGPLite.Protocol;

/// <summary>
/// Wire codec for the MP-BGP IPv6/Unicast attributes (RFC 4760, #15 phase 2):
/// <list type="bullet">
/// <item><c>MP_REACH_NLRI</c> (type 14): AFI(2) + SAFI(1) + NH-Len(1) + Next Hop + Reserved(1) + NLRI.</item>
/// <item><c>MP_UNREACH_NLRI</c> (type 15): AFI(2) + SAFI(1) + Withdrawn NLRI.</item>
/// </list>
/// All functions operate on the attribute VALUE (after the 2-3 byte TLV header). Malformed input
/// throws <see cref="BgpParseException"/> with Update Message Error so the caller's treat-as-withdraw
/// path (RFC 7606 §2 — the attribute carries NLRI) handles it: the UPDATE is discarded, the session
/// stays up (D17). The 32-byte next-hop form of RFC 2545 §3 (global + link-local) is decoded — the
/// global address (first 16 bytes) is used; a next-hop length other than 16/32 is a parse error.
/// </summary>
public static class MpReachCodec
{
    public const ushort AfiIpv6 = (ushort)BgpConstants.Afi.IPv6;
    public const byte SafiUnicast = BgpConstants.Safi.Unicast;
    public const byte MpReachNlriType = 14;
    public const byte MpUnreachNlriType = 15;

    public readonly record struct MpReachV6(UInt128 NextHop, IReadOnlyList<IpPrefix> Prefixes);

    /// <summary>MP_REACH/MP_UNREACH AFI=1/SAFI=1 (IPv4/Unicast) decode result (#466): the 4-octet
    /// next hop plus the prefix list — semantically the classic IPv4 NLRI path.</summary>
    public readonly record struct MpReachV4(uint NextHop, IReadOnlyList<IpPrefix> Prefixes);

    public const ushort AfiIpv4 = (ushort)BgpConstants.Afi.IPv4;

    /// <summary>
    /// Decodes an MP_REACH_NLRI (type 14) VALUE for IPv4/Unicast (#466): AFI(2) + SAFI(1) +
    /// NH-Len(1) + a 4-octet next hop + Reserved(1) + classic IPv4 NLRI. A next-hop length other
    /// than 4 is a parse error (RFC 4760 §5 defines no other form for this family).
    /// </summary>
    public static MpReachV4 DecodeMpReachV4(ReadOnlySpan<byte> value)
    {
        if (value.Length < 8)
            throw new BgpParseException(
                $"Truncated MP_REACH_NLRI (IPv4): have {value.Length} bytes, need at least 8",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.InvalidNetworkField);

        var afi = BinaryPrimitives.ReadUInt16BigEndian(value);
        var safi = value[2];
        if (afi != AfiIpv4 || safi != SafiUnicast)
            throw new BgpParseException(
                $"MP_REACH_NLRI for unsupported address family: AFI={afi}, SAFI={safi} (expected 1/1)",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.InvalidNetworkField);

        var nhLength = value[3];
        if (nhLength != 4)
            throw new BgpParseException(
                $"Invalid MP_REACH_NLRI (IPv4) next-hop length: {nhLength} (must be 4 per RFC 4760 §5)",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.MalformedAttributeList);
        if (value.Length < 4 + nhLength + 1)
            throw new BgpParseException(
                $"Truncated MP_REACH_NLRI next hop: need {nhLength} bytes at offset 4",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.InvalidNetworkField);

        var nextHop = BinaryPrimitives.ReadUInt32BigEndian(value[4..]);

        // value[4+nhLength] is the Reserved byte — tolerated on receive (see DecodeMpReachV6).
        var nlriOffset = 4 + nhLength + 1;
        var prefixes = new List<IpPrefix>();
        while (nlriOffset < value.Length)
        {
            var (prefix, consumed) = PrefixCodec.Decode(value[nlriOffset..]);
            prefixes.Add(prefix);
            nlriOffset += consumed;
        }

        return new MpReachV4(nextHop, prefixes);
    }

    /// <summary>Decodes an MP_UNREACH_NLRI (type 15) VALUE for IPv4/Unicast (#466):
    /// AFI(2) + SAFI(1) + the withdrawn classic IPv4 NLRI.</summary>
    public static IReadOnlyList<IpPrefix> DecodeMpUnreachV4(ReadOnlySpan<byte> value)
    {
        if (value.Length < 3)
            throw new BgpParseException(
                $"Truncated MP_UNREACH_NLRI (IPv4): have {value.Length} bytes, need at least 3",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.InvalidNetworkField);

        var afi = BinaryPrimitives.ReadUInt16BigEndian(value);
        var safi = value[2];
        if (afi != AfiIpv4 || safi != SafiUnicast)
            throw new BgpParseException(
                $"MP_UNREACH_NLRI for unsupported address family: AFI={afi}, SAFI={safi} (expected 1/1)",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.InvalidNetworkField);

        var prefixes = new List<IpPrefix>();
        var offset = 3;
        while (offset < value.Length)
        {
            var (prefix, consumed) = PrefixCodec.Decode(value[offset..]);
            prefixes.Add(prefix);
            offset += consumed;
        }
        return prefixes;
    }

    /// <summary>
    /// #467 (RFC 2545 §3): the MP_REACH next hop must be a GLOBAL IPv6 address. Rejects the
    /// unspecified address (::), loopback (::1), multicast (ff00::/8) and link-local
    /// (fe80::/10) — a lone link-local is only meaningful on a shared subnet and rides as the
    /// SECOND half of the RFC 2545 32-byte form, which the decoder never adopts. IPv4-mapped
    /// forms are global-scope representations and are accepted.
    /// </summary>
    public static bool IsGlobalUnicastNextHop(UInt128 nextHop)
    {
        if (nextHop == UInt128.Zero) return false;                              // ::
        if (nextHop == UInt128.One) return false;                               // ::1
        if ((nextHop >> 120) == (UInt128)0xFF) return false;                    // ff00::/8
        if ((nextHop >> 118) == (UInt128)0b1111111010) return false;            // fe80::/10
        return true;
    }

    /// <summary>Builds the MP_REACH_NLRI (type 14) attribute VALUE for IPv6/Unicast:
    /// a 16-byte global next hop plus the NLRI prefix list.</summary>
    public static byte[] EncodeMpReachV6(UInt128 nextHop, IReadOnlyList<IpPrefix> prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        foreach (var p in prefixes)
            if (p.IsIpv4)
                throw new ArgumentException($"IPv4 prefix {p} cannot be encoded in an IPv6 MP_REACH.", nameof(prefixes));

        // size = AFI(2) + SAFI(1) + NH-Len(1) + NH(16) + Reserved(1) + Σ NLRI
        var nlriSize = 0;
        foreach (var p in prefixes)
            nlriSize += 1 + (p.Length + 7) / 8;

        var buffer = new byte[21 + nlriSize];
        buffer[0] = (byte)(AfiIpv6 >> 8);
        buffer[1] = (byte)AfiIpv6;
        buffer[2] = SafiUnicast;
        buffer[3] = 16;                                   // next-hop length: global only
        for (var i = 0; i < 16; i++)
            buffer[4 + i] = (byte)(nextHop >> (120 - i * 8));
        // buffer[20] = reserved 0x00
        var offset = 21;
        foreach (var p in prefixes)
            offset += PrefixCodec.Encode(p, buffer.AsSpan(offset));
        return buffer;
    }

    /// <summary>Decodes an MP_REACH_NLRI (type 14) VALUE for IPv6/Unicast. The next hop is the
    /// first 16 bytes; the RFC 2545 32-byte form (global + link-local) is accepted and the
    /// link-local half is skipped — the link-local address is only meaningful on a shared
    /// interface, which this session is not required to have.</summary>
    public static MpReachV6 DecodeMpReachV6(ReadOnlySpan<byte> value)
    {
        // AFI(2) + SAFI(1) + NH-Len(1) + Reserved(1) = 5 bytes of fixed header, plus at least
        // 16 bytes of next hop.
        if (value.Length < 21)
            throw new BgpParseException(
                $"Truncated MP_REACH_NLRI: have {value.Length} bytes, need at least 21",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.InvalidNetworkField);

        var afi = BinaryPrimitives.ReadUInt16BigEndian(value);
        var safi = value[2];
        if (afi != AfiIpv6 || safi != SafiUnicast)
            throw new BgpParseException(
                $"MP_REACH_NLRI for unsupported address family: AFI={afi}, SAFI={safi} (expected 2/1)",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.InvalidNetworkField);

        var nhLength = value[3];
        if (nhLength is not 16 and not 32)
            throw new BgpParseException(
                $"Invalid MP_REACH_NLRI next-hop length: {nhLength} (must be 16 or 32 per RFC 2545)",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.MalformedAttributeList);
        if (value.Length < 4 + nhLength + 1)
            throw new BgpParseException(
                $"Truncated MP_REACH_NLRI next hop: need {nhLength} bytes at offset 4",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.InvalidNetworkField);

        UInt128 nextHop = 0;
        for (var i = 0; i < 16; i++)
            nextHop = (nextHop << 8) | value[4 + i];

        // value[4+nhLength] is the Reserved byte — MUST be 0 per RFC 4760 §4, but tolerated on
        // receive (some implementations send garbage there; it carries no information).
        var nlriOffset = 4 + nhLength + 1;
        var prefixes = new List<IpPrefix>();
        while (nlriOffset < value.Length)
        {
            var (prefix, consumed) = PrefixCodec.Decode6(value[nlriOffset..]);
            prefixes.Add(prefix);
            nlriOffset += consumed;
        }

        return new MpReachV6(nextHop, prefixes);
    }

    /// <summary>Builds the MP_UNREACH_NLRI (type 15) attribute VALUE for IPv6/Unicast:
    /// AFI(2) + SAFI(1) + the withdrawn NLRI prefix list.</summary>
    public static byte[] EncodeMpUnreachV6(IReadOnlyList<IpPrefix> prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        foreach (var p in prefixes)
            if (p.IsIpv4)
                throw new ArgumentException($"IPv4 prefix {p} cannot be encoded in an IPv6 MP_UNREACH.", nameof(prefixes));

        var nlriSize = 0;
        foreach (var p in prefixes)
            nlriSize += 1 + (p.Length + 7) / 8;

        var buffer = new byte[3 + nlriSize];
        buffer[0] = (byte)(AfiIpv6 >> 8);
        buffer[1] = (byte)AfiIpv6;
        buffer[2] = SafiUnicast;
        var offset = 3;
        foreach (var p in prefixes)
            offset += PrefixCodec.Encode(p, buffer.AsSpan(offset));
        return buffer;
    }

    /// <summary>Decodes an MP_UNREACH_NLRI (type 15) VALUE for IPv6/Unicast.</summary>
    public static IReadOnlyList<IpPrefix> DecodeMpUnreachV6(ReadOnlySpan<byte> value)
    {
        if (value.Length < 3)
            throw new BgpParseException(
                $"Truncated MP_UNREACH_NLRI: have {value.Length} bytes, need at least 3",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.InvalidNetworkField);

        var afi = BinaryPrimitives.ReadUInt16BigEndian(value);
        var safi = value[2];
        if (afi != AfiIpv6 || safi != SafiUnicast)
            throw new BgpParseException(
                $"MP_UNREACH_NLRI for unsupported address family: AFI={afi}, SAFI={safi} (expected 2/1)",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.InvalidNetworkField);

        var prefixes = new List<IpPrefix>();
        var offset = 3;
        while (offset < value.Length)
        {
            var (prefix, consumed) = PrefixCodec.Decode6(value[offset..]);
            prefixes.Add(prefix);
            offset += consumed;
        }
        return prefixes;
    }
}
