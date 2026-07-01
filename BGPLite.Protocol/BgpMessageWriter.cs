using System.Buffers.Binary;

namespace BGPLite.Protocol;

public static class BgpMessageWriter
{
    public static int WriteMessage(BgpMessage message, Span<byte> buffer)
    {
        return message switch
        {
            BgpOpenMessage open => WriteOpen(open, buffer),
            BgpKeepaliveMessage => WriteKeepalive(buffer),
            BgpUpdateMessage update => WriteUpdate(update, buffer),
            BgpNotificationMessage notification => WriteNotification(notification, buffer),
            _ => throw new ArgumentException($"Unknown message type: {message.Type}")
        };
    }

    public static int GetBufferSize(BgpMessage message)
    {
        return message switch
        {
            BgpOpenMessage open => BgpConstants.MessageHeaderSize + GetOpenPayloadSize(open),
            BgpKeepaliveMessage => BgpConstants.MessageHeaderSize,
            BgpUpdateMessage update => BgpConstants.MessageHeaderSize + GetUpdatePayloadSize(update),
            BgpNotificationMessage n => BgpConstants.MessageHeaderSize + 2 + (n.Data?.Length ?? 0),
            _ => throw new ArgumentException($"Unknown message type: {message.Type}")
        };
    }

    private static void WriteHeader(BgpMessageType type, int totalLength, Span<byte> buffer)
    {
        BgpConstants.Marker.CopyTo(buffer);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[16..], (ushort)totalLength);
        buffer[18] = (byte)type;
    }

    #region OPEN

    private static int WriteOpen(BgpOpenMessage msg, Span<byte> buffer)
    {
        var payloadSize = GetOpenPayloadSize(msg);
        var totalLength = BgpConstants.MessageHeaderSize + payloadSize;

        WriteHeader(BgpMessageType.Open, totalLength, buffer);

        var p = BgpConstants.MessageHeaderSize;
        buffer[p++] = msg.Version;
        BinaryPrimitives.WriteUInt16BigEndian(buffer[p..], msg.Asn);
        p += 2;
        BinaryPrimitives.WriteUInt16BigEndian(buffer[p..], msg.HoldTime);
        p += 2;
        BinaryPrimitives.WriteUInt32BigEndian(buffer[p..], msg.RouterId);
        p += 4;

        var optParamsLen = GetOptParamsLength(msg.Capabilities);
        // RFC 4271 §4.2: optional-parameters length is a single byte.
        // Silently truncating > 255 yields a malformed frame; reject up front.
        RequireFitsByte(optParamsLen, nameof(BgpOpenMessage.Capabilities), "optional-parameters");
        buffer[p++] = (byte)optParamsLen;

        WriteCapabilities(msg.Capabilities, buffer[p..]);

        return totalLength;
    }

    private static int GetOpenPayloadSize(BgpOpenMessage msg) =>
        9 + 1 + GetOptParamsLength(msg.Capabilities);

    private static int GetOptParamsLength(List<BgpCapabilityInfo> capabilities)
    {
        if (capabilities.Count == 0) return 0;

        var capDataLen = 0;
        foreach (var cap in capabilities)
            capDataLen += 2 + cap.Data.Length;

        return 2 + capDataLen;
    }

    private static void WriteCapabilities(List<BgpCapabilityInfo> capabilities, Span<byte> buffer)
    {
        if (capabilities.Count == 0) return;

        var capDataLen = 0;
        foreach (var cap in capabilities)
            capDataLen += 2 + cap.Data.Length;

        var p = 0;
        buffer[p++] = 2; // Type: Capabilities
        // RFC 4271 §6.2: capability data length is a single byte.
        RequireFitsByte(capDataLen, nameof(BgpCapabilityInfo.Data), "capability data");
        buffer[p++] = (byte)capDataLen;

        foreach (var cap in capabilities)
        {
            buffer[p++] = cap.Code;
            // RFC 4271 §6.2: per-capability length is a single byte.
            RequireFitsByte(cap.Data.Length, nameof(BgpCapabilityInfo.Data), "capability length");
            buffer[p++] = (byte)cap.Data.Length;
            cap.Data.AsSpan().CopyTo(buffer[p..]);
            p += cap.Data.Length;
        }
    }

    private static void RequireFitsByte(int value, string paramName, string field)
    {
        if (value < 0 || value > byte.MaxValue)
            throw new ArgumentOutOfRangeException(paramName, value, $"{field} length must fit in a single byte (0..{byte.MaxValue}).");
    }

    #endregion

    #region KEEPALIVE

    private static int WriteKeepalive(Span<byte> buffer)
    {
        WriteHeader(BgpMessageType.Keepalive, BgpConstants.MessageHeaderSize, buffer);
        return BgpConstants.MessageHeaderSize;
    }

    #endregion

    #region UPDATE

    private static int WriteUpdate(BgpUpdateMessage msg, Span<byte> buffer)
    {
        var payloadSize = GetUpdatePayloadSize(msg);
        var totalLength = BgpConstants.MessageHeaderSize + payloadSize;

        WriteHeader(BgpMessageType.Update, totalLength, buffer);

        var p = BgpConstants.MessageHeaderSize;

        // Withdrawn routes
        var withdrawnLen = GetNlriLength(msg.WithdrawnRoutes);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[p..], (ushort)withdrawnLen);
        p += 2;
        foreach (var w in msg.WithdrawnRoutes)
            p += PrefixCodec.Encode(w, buffer[p..]);

        // Path attributes
        var attrsLen = GetAttributesLength(msg.PathAttributes);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[p..], (ushort)attrsLen);
        p += 2;
        foreach (var attr in msg.PathAttributes)
            p += WriteAttribute(attr, buffer[p..]);

        // NLRI
        foreach (var nlri in msg.Nlri)
            p += PrefixCodec.Encode(nlri, buffer[p..]);

        return totalLength;
    }

    private static int GetUpdatePayloadSize(BgpUpdateMessage msg) =>
        2 + GetNlriLength(msg.WithdrawnRoutes) + 2 + GetAttributesLength(msg.PathAttributes) + GetNlriLength(msg.Nlri);

    private static int GetNlriLength(List<IpPrefix> prefixes)
    {
        var len = 0;
        foreach (var p in prefixes)
            len += 1 + (p.Length + 7) / 8;
        return len;
    }

    private static int GetAttributesLength(List<PathAttribute> attributes)
    {
        var len = 0;
        foreach (var attr in attributes)
        {
            len += 2; // flags + type code
            if (attr.Data.Length > 255)
                len += 2; // extended length
            else
                len += 1; // single byte length
            len += attr.Data.Length;
        }
        return len;
    }

    private static int WriteAttribute(PathAttribute attr, Span<byte> buffer)
    {
        var p = 0;
        buffer[p++] = attr.Flags;
        buffer[p++] = attr.TypeCode;

        if (attr.Data.Length > 255)
        {
            buffer[p - 2] |= BgpConstants.Attribute.FlagExtendedLength;
            BinaryPrimitives.WriteUInt16BigEndian(buffer[p..], (ushort)attr.Data.Length);
            p += 2;
        }
        else
        {
            buffer[p++] = (byte)attr.Data.Length;
        }

        attr.Data.AsSpan().CopyTo(buffer[p..]);
        return p + attr.Data.Length;
    }

    #endregion

    #region NOTIFICATION

    private static int WriteNotification(BgpNotificationMessage msg, Span<byte> buffer)
    {
        var totalLength = BgpConstants.MessageHeaderSize + 2 + (msg.Data?.Length ?? 0);
        WriteHeader(BgpMessageType.Notification, totalLength, buffer);

        var p = BgpConstants.MessageHeaderSize;
        buffer[p++] = msg.ErrorCode;
        buffer[p++] = msg.SubErrorCode;

        if (msg.Data is { Length: > 0 })
        {
            msg.Data.AsSpan().CopyTo(buffer[p..]);
        }

        return totalLength;
    }

    #endregion
}
