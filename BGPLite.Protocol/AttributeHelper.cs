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

    /// <summary>
    /// Writes AS4_PATH attribute (type 17) per RFC 6793 §6.
    /// Carries the true 4-byte AS sequence when sending to a 2-byte-only peer.
    /// </summary>
    public static PathAttribute WriteAs4Path(uint[] ases)
    {
        return new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagOptional | BgpConstants.Attribute.FlagTransitive,
            TypeCode = BgpConstants.Attribute.As4Path,
            Data = WritePathData(ases, 4, "AS4_PATH")
        };
    }

    /// <summary>
    /// Reads AS4_PATH attribute (type 17) per RFC 6793 §6.
    /// Returns the 4-byte AS sequence for reconstruction when receiving from a 2-byte-only peer.
    /// NOTE: AS_SET segments are not preserved — returns a flat AS sequence (matching ReadAsPath).
    /// </summary>
    public static uint[] ReadAs4Path(PathAttribute attr)
    {
        return ReadPathData(attr.Data, 4, "AS4_PATH");
    }

    private static uint[] ReadPathData(ReadOnlySpan<byte> data, int asSize, string attributeName)
    {
        var ases = new List<uint>();
        var offset = 0;

        while (offset + 2 <= data.Length)
        {
            // segment header: [type][length]
            var segmentType = data[offset++];
            var segmentLength = data[offset++];

            if (segmentType != BgpConstants.AsPath.AsSequence &&
                segmentType != BgpConstants.AsPath.AsSet)
                throw new BgpParseException($"Invalid {attributeName} segment type: {segmentType}");

            var segBytes = segmentLength * asSize;
            if (offset + segBytes > data.Length)
                throw new BgpParseException($"Truncated {attributeName} segment");

            for (var i = 0; i < segmentLength; i++)
            {
                ases.Add(ReadAsn(data.Slice(offset, asSize), asSize));
                offset += asSize;
            }
        }

        if (offset != data.Length)
            throw new BgpParseException($"Malformed {attributeName} attribute");

        return ases.ToArray();
    }

    private static byte[] WritePathData(uint[] ases, int asSize, string attributeName)
    {
        if (ases.Length > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(ases), $"{attributeName} segment length cannot exceed 255 ASNs.");

        var data = new byte[2 + ases.Length * asSize];
        data[0] = BgpConstants.AsPath.AsSequence;
        data[1] = (byte)ases.Length;

        var offset = 2;
        foreach (var asn in ases)
        {
            WriteAsn(data.AsSpan(offset, asSize), asn, asSize);
            offset += asSize;
        }

        return data;
    }

    private static uint ReadAsn(ReadOnlySpan<byte> data, int asSize) => asSize switch
    {
        2 => BinaryPrimitives.ReadUInt16BigEndian(data),
        4 => BinaryPrimitives.ReadUInt32BigEndian(data),
        _ => throw new ArgumentOutOfRangeException(nameof(asSize))
    };

    private static void WriteAsn(Span<byte> data, uint asn, int asSize)
    {
        switch (asSize)
        {
            case 2:
                BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)asn);
                break;
            case 4:
                BinaryPrimitives.WriteUInt32BigEndian(data, asn);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(asSize));
        }
    }

    public static uint ReadNextHop(PathAttribute attr)
    {
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
        _ => false
    };
}
