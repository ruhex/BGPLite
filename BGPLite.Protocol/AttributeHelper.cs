using System.Buffers.Binary;

namespace BGPLite.Protocol;

public static class AttributeHelper
{
    public static BgpOrigin ReadOrigin(PathAttribute attr)
    {
        return (BgpOrigin)attr.Data[0];
    }

    public static PathAttribute WriteOrigin(BgpOrigin origin)
    {
        return new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagTransitive,
            TypeCode = BgpConstants.Attribute.Origin,
            Data = [(byte)origin]
        };
    }

    public static uint[] ReadAsPath(PathAttribute attr, bool fourByteAsn)
    {
        return ReadPathData(attr.Data, fourByteAsn ? 4 : 2, "AS_PATH");
    }

    public static PathAttribute WriteAsPath(uint[] ases, bool fourByteAsn)
    {
        return new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagTransitive,
            TypeCode = BgpConstants.Attribute.AsPath,
            Data = WritePathData(ases, fourByteAsn ? 4 : 2, "AS_PATH")
        };
    }

    public static PathAttribute WriteAs4Path(uint[] ases)
    {
        return new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagOptional | BgpConstants.Attribute.FlagTransitive,
            TypeCode = BgpConstants.Attribute.As4Path,
            Data = WritePathData(ases, 4, "AS4_PATH")
        };
    }

    public static uint[] ReadAs4Path(PathAttribute attr)
    {
        return ReadPathData(attr.Data, 4, "AS4_PATH");
    }

    public static uint ReadNextHop(PathAttribute attr)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(attr.Data);
    }

    /// <summary>
    /// Reads the aggregator ASN from a legacy AGGREGATOR attribute (type 7). Per RFC 6793 §3 the
    /// AGGREGATOR attribute is unconditionally 6 octets: a 2-octet AS followed by a 4-octet IPv4
    /// address — independent of whether the session negotiated 4-byte AS support. The 4-byte AS
    /// form lives in the separate AS4_AGGREGATOR attribute (type 18, see <see cref="ReadAs4AggregatorAsn"/>).
    /// The <paramref name="fourByteAsn"/> parameter is retained for signature compatibility with
    /// the call site but no longer affects the wire format — the prior branch accepted a malformed
    /// 8-byte AGGREGATOR and rejected every legal 6-byte one on a 4-byte session (regression of #31).
    /// </summary>
    public static uint ReadAggregatorAsn(PathAttribute attr, bool fourByteAsn = false)
    {
        _ = fourByteAsn; // retained for API compatibility; AGGREGATOR is always 6 octets (RFC 6793 §3).
        if (attr.Data.Length != 6)
            throw new BgpParseException($"Malformed AGGREGATOR attribute: expected 6 bytes, got {attr.Data.Length}");

        return BinaryPrimitives.ReadUInt16BigEndian(attr.Data);
    }

    /// <summary>
    /// Reads the aggregator ASN from an AS4_AGGREGATOR attribute (type 18). Per RFC 6793 §3 it is
    /// 8 octets: a 4-octet AS followed by a 4-octet IPv4 address. Only the leading AS is returned;
    /// the trailing aggregator IP is not currently consumed (it does not influence AS_PATH
    /// reconstruction). The prior code expected exactly 4 bytes (#31 regression), rejecting every
    /// well-formed AS4_AGGREGATOR.
    /// </summary>
    public static uint ReadAs4AggregatorAsn(PathAttribute attr)
    {
        if (attr.Data.Length != 8)
            throw new BgpParseException($"Malformed AS4_AGGREGATOR attribute: expected 8 bytes, got {attr.Data.Length}");

        return BinaryPrimitives.ReadUInt32BigEndian(attr.Data);
    }

    public static PathAttribute WriteNextHop(uint nextHop)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(data, nextHop);
        return new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagTransitive,
            TypeCode = BgpConstants.Attribute.NextHop,
            Data = data
        };
    }

    public static uint[] ReadCommunities(PathAttribute attr)
    {
        var count = attr.Data.Length / 4;
        var communities = new uint[count];
        for (var i = 0; i < count; i++)
            communities[i] = BinaryPrimitives.ReadUInt32BigEndian(attr.Data.AsSpan(i * 4));
        return communities;
    }

    public static PathAttribute WriteCommunities(uint[] communities)
    {
        var data = new byte[communities.Length * 4];
        for (var i = 0; i < communities.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(i * 4), communities[i]);
        return new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagOptional | BgpConstants.Attribute.FlagTransitive,
            TypeCode = BgpConstants.Attribute.Community,
            Data = data
        };
    }

    /// <summary>
    /// Decodes a BGP Large Communities attribute (RFC 8092 §2). Each Large Community is three
    /// 4-octet fields — Global Administrator : Local Data 1 : Local Data 2 — in network byte
    /// order, so the attribute payload MUST be a multiple of 12 bytes. A zero-length payload is
    /// a valid (empty) set; any other non-multiple-of-12 length is malformed.
    /// </summary>
    public static (uint Global, uint Local1, uint Local2)[] ReadLargeCommunities(PathAttribute attr)
    {
        if (attr.Data.Length == 0 || attr.Data.Length % 12 != 0)
            throw new BgpParseException("Large Communities attribute length must be a non-zero multiple of 12");

        var count = attr.Data.Length / 12;
        var large = new (uint Global, uint Local1, uint Local2)[count];
        for (var i = 0; i < count; i++)
        {
            var offset = i * 12;
            large[i] = (
                BinaryPrimitives.ReadUInt32BigEndian(attr.Data.AsSpan(offset)),
                BinaryPrimitives.ReadUInt32BigEndian(attr.Data.AsSpan(offset + 4)),
                BinaryPrimitives.ReadUInt32BigEndian(attr.Data.AsSpan(offset + 8)));
        }
        return large;
    }

    /// <summary>
    /// Encodes a BGP Large Communities attribute (RFC 8092 §2): 12 bytes per triplet, network
    /// byte order, flags OPTIONAL + TRANSITIVE (0xC0), type code 32.
    /// </summary>
    public static PathAttribute WriteLargeCommunities((uint Global, uint Local1, uint Local2)[] large)
    {
        var data = new byte[large.Length * 12];
        for (var i = 0; i < large.Length; i++)
        {
            var offset = i * 12;
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), large[i].Global);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset + 4), large[i].Local1);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset + 8), large[i].Local2);
        }
        return new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagOptional | BgpConstants.Attribute.FlagTransitive,
            TypeCode = BgpConstants.Attribute.LargeCommunity,
            Data = data
        };
    }

    /// <summary>
    /// Renders a Large Community as the canonical RFC 8092 text form
    /// "<c>global:local1:local2</c>" (e.g. for logging/diagnostics).
    /// </summary>
    public static string FormatLargeCommunity((uint Global, uint Local1, uint Local2) large) =>
        $"{large.Global}:{large.Local1}:{large.Local2}";

    public static bool IsKnownAttribute(byte typeCode) => typeCode switch
    {
        BgpConstants.Attribute.Origin => true,
        BgpConstants.Attribute.AsPath => true,
        BgpConstants.Attribute.NextHop => true,
        BgpConstants.Attribute.Community => true,
        BgpConstants.Attribute.Med => true,
        BgpConstants.Attribute.LocalPref => true,
        BgpConstants.Attribute.AtomicAggregate => true,
        BgpConstants.Attribute.Aggregator => true,
        BgpConstants.Attribute.As4Path => true,
        BgpConstants.Attribute.As4Aggregator => true,
        BgpConstants.Attribute.LargeCommunity => true,
        _ => false
    };

    private static uint[] ReadPathData(ReadOnlySpan<byte> data, int asSize, string attributeName)
    {
        var ases = new List<uint>();
        var offset = 0;

        while (offset + 2 <= data.Length)
        {
            var segmentType = data[offset++];
            var segmentLength = data[offset++];

            if (segmentType != BgpConstants.AsPath.AsSequence && segmentType != BgpConstants.AsPath.AsSet)
                throw new BgpParseException($"Invalid {attributeName} segment type: {segmentType}");

            var segBytes = segmentLength * asSize;
            if (offset + segBytes > data.Length)
                throw new BgpParseException($"Truncated {attributeName} segment");

            for (var i = 0; i < segmentLength; i++)
            {
                ases.Add(asSize switch
                {
                    2 => BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, asSize)),
                    4 => BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, asSize)),
                    _ => throw new ArgumentOutOfRangeException(nameof(asSize))
                });
                offset += asSize;
            }
        }

        if (offset != data.Length)
            throw new BgpParseException($"Malformed {attributeName} attribute");

        return ases.ToArray();
    }

    private static byte[] WritePathData(uint[] ases, int asSize, string attributeName)
    {
        // Intentionally emits a single AS_SEQUENCE segment. For the current route-server use case,
        // paths longer than 255 ASNs are treated as out of scope instead of being split into
        // multiple segments.
        if (ases.Length > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(ases), $"{attributeName} segment length cannot exceed 255 ASNs.");

        var data = new byte[2 + ases.Length * asSize];
        data[0] = BgpConstants.AsPath.AsSequence;
        data[1] = (byte)ases.Length;

        var offset = 2;
        foreach (var asn in ases)
        {
            switch (asSize)
            {
                case 2:
                    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, asSize), (ushort)asn);
                    break;
                case 4:
                    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, asSize), asn);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(asSize));
            }

            offset += asSize;
        }

        return data;
    }
}
