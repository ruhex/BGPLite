using System.Buffers.Binary;

namespace BGPLite.Protocol;

public static class BgpMessageReader
{
    public static BgpMessage ReadMessage(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < BgpConstants.MinMessageSize)
            throw new BgpParseException($"Message too short: {buffer.Length}");

        ValidateMarker(buffer[..BgpConstants.MarkerSize]);

        var length = BinaryPrimitives.ReadUInt16BigEndian(buffer[16..]);
        if (length < BgpConstants.MinMessageSize || length > BgpConstants.MaxMessageSize)
            throw new BgpParseException($"Invalid message length: {length}");

        if (buffer.Length < length)
            throw new BgpParseException($"Incomplete message: have {buffer.Length}, need {length}");

        var type = (BgpMessageType)buffer[18];
        var payload = buffer[BgpConstants.MessageHeaderSize..length];

        return type switch
        {
            BgpMessageType.Open => ParseOpen(payload),
            BgpMessageType.Keepalive => BgpKeepaliveMessage.Instance,
            BgpMessageType.Update => ParseUpdate(payload),
            BgpMessageType.Notification => ParseNotification(payload),
            BgpMessageType.RouteRefresh => ParseRouteRefresh(payload),
            _ => throw new BgpParseException($"Unknown message type: {type}")
        };
    }

    public static int GetMessageLength(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < BgpConstants.MessageHeaderSize)
            return -1;
        return BinaryPrimitives.ReadUInt16BigEndian(buffer[16..]);
    }

    private static void ValidateMarker(ReadOnlySpan<byte> marker)
    {
        var expected = BgpConstants.Marker;
        for (var i = 0; i < BgpConstants.MarkerSize; i++)
        {
            if (marker[i] != expected[i])
                throw new BgpParseException("Invalid BGP marker");
        }
    }

    #region OPEN

    private static BgpOpenMessage ParseOpen(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 10)
            throw new BgpParseException($"OPEN message too short: {payload.Length}");

        var version = payload[0];
        if (version != BgpConstants.BgpVersion)
            throw new BgpParseException($"Unsupported BGP version: {version}");

        var asn = BinaryPrimitives.ReadUInt16BigEndian(payload[1..]);
        var holdTime = BinaryPrimitives.ReadUInt16BigEndian(payload[3..]);
        var routerId = BinaryPrimitives.ReadUInt32BigEndian(payload[5..]);
        var optParamsLen = payload[9];

        var capabilities = new List<BgpCapabilityInfo>();
        if (optParamsLen > 0 && payload.Length >= 10 + optParamsLen)
            ParseOptParameters(payload[10..][..optParamsLen], capabilities);

        return new BgpOpenMessage
        {
            Version = version,
            Asn = asn,
            HoldTime = holdTime,
            RouterId = routerId,
            Capabilities = capabilities
        };
    }

    private static void ParseOptParameters(ReadOnlySpan<byte> data, List<BgpCapabilityInfo> capabilities)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            if (offset + 2 > data.Length) break;
            var paramType = data[offset++];
            var paramLen = data[offset++];

            if (offset + paramLen > data.Length) break;

            if (paramType == 2) // Capability
                ParseCapabilities(data[offset..][..paramLen], capabilities);

            offset += paramLen;
        }
    }

    private static void ParseCapabilities(ReadOnlySpan<byte> data, List<BgpCapabilityInfo> capabilities)
    {
        var offset = 0;
        while (offset + 2 <= data.Length)
        {
            var code = data[offset++];
            var len = data[offset++];

            if (offset + len > data.Length) break;

            var capData = new byte[len];
            data.Slice(offset, len).CopyTo(capData);
            capabilities.Add(new BgpCapabilityInfo { Code = code, Data = capData });

            offset += len;
        }
    }

    #endregion

    #region UPDATE

    private static BgpUpdateMessage ParseUpdate(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
            throw new BgpParseException($"UPDATE message too short: {payload.Length}");

        var offset = 0;

        var withdrawnLen = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
        offset += 2;

        var withdrawn = new List<IpPrefix>();
        if (withdrawnLen > 0)
        {
            var withdrawnEnd = offset + withdrawnLen;
            while (offset < withdrawnEnd)
            {
                var (prefix, consumed) = PrefixCodec.Decode(payload[offset..]);
                withdrawn.Add(prefix);
                offset += consumed;
            }
        }

        var attrsLen = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
        offset += 2;

        var attributes = new List<PathAttribute>();
        if (attrsLen > 0)
        {
            var attrsEnd = offset + attrsLen;
            while (offset < attrsEnd)
            {
                var (attr, consumed) = ParseAttribute(payload[offset..]);
                attributes.Add(attr);
                offset += consumed;
            }
        }

        var nlri = new List<IpPrefix>();
        while (offset < payload.Length)
        {
            var (prefix, consumed) = PrefixCodec.Decode(payload[offset..]);
            nlri.Add(prefix);
            offset += consumed;
        }

        return new BgpUpdateMessage
        {
            WithdrawnRoutes = withdrawn,
            PathAttributes = attributes,
            Nlri = nlri
        };
    }

    private static (PathAttribute attr, int consumed) ParseAttribute(ReadOnlySpan<byte> data)
    {
        var flags = data[0];
        var typeCode = data[1];
        var offset = 2;

        int length;
        if ((flags & BgpConstants.Attribute.FlagExtendedLength) != 0)
        {
            length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += 2;
        }
        else
        {
            length = data[offset];
            offset += 1;
        }

        var attrData = new byte[length];
        data.Slice(offset, length).CopyTo(attrData);

        return (new PathAttribute { Flags = flags, TypeCode = typeCode, Data = attrData }, offset + length);
    }

    #endregion

    #region NOTIFICATION

    private static BgpNotificationMessage ParseNotification(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
            throw new BgpParseException($"NOTIFICATION message too short: {payload.Length}");

        var errorCode = payload[0];
        var subErrorCode = payload[1];
        byte[]? data = null;

        if (payload.Length > 2)
        {
            data = new byte[payload.Length - 2];
            payload[2..].CopyTo(data);
        }

        return new BgpNotificationMessage
        {
            ErrorCode = errorCode,
            SubErrorCode = subErrorCode,
            Data = data
        };
    }

    private static BgpRouteRefreshMessage ParseRouteRefresh(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 4)
            throw new BgpParseException($"ROUTE_REFRESH payload must be exactly 4 bytes, got {payload.Length}");

        var afi = BinaryPrimitives.ReadUInt16BigEndian(payload);
        var reserved = payload[2];
        var safi = payload[3];

        return new BgpRouteRefreshMessage
        {
            Afi = afi,
            Reserved = reserved,
            Safi = safi
        };
    }

    #endregion
}

public sealed class BgpParseException : Exception
{
    public BgpParseException(string message) : base(message) { }
    public BgpParseException(string message, Exception inner) : base(message, inner) { }
}
