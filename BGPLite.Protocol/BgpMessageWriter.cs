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
            BgpRouteRefreshMessage refresh => WriteRouteRefresh(refresh, buffer),
            _ => throw new NotSupportedException($"Unknown BGP message type: {message.Type}")
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
            BgpRouteRefreshMessage => BgpConstants.MessageHeaderSize + 4,
            _ => throw new NotSupportedException($"Unknown BGP message type: {message.Type}")
        };
    }

    private static void WriteHeader(BgpMessageType type, int totalLength, Span<byte> buffer)
    {
        // RFC 4271 §4.1: BGP message length is a 16-bit field. BgpMessageReader
        // rejects anything outside [MinMessageSize, MaxMessageSize], so we must
        // reject the same range up front to keep writer and reader aligned
        // (otherwise the writer could emit a frame the reader would discard).
        if (totalLength < BgpConstants.MinMessageSize || totalLength > BgpConstants.MaxMessageSize)
            throw new ArgumentOutOfRangeException(nameof(totalLength), totalLength, $"BGP message length must be in {BgpConstants.MinMessageSize}..{BgpConstants.MaxMessageSize}.");

        BgpConstants.Marker.CopyTo(buffer);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[16..], (ushort)totalLength);
        buffer[18] = (byte)type;
    }

    #region OPEN

    private static int WriteOpen(BgpOpenMessage msg, Span<byte> buffer)
    {
        // optParamsLen = 2 (type+length) + capDataLen. If optParamsLen fits in
        // a byte, then capDataLen and every individual cap.Data.Length fit too,
        // so one guard covers all three (RFC 4271 §4.2 / §6.2). Run the check
        // BEFORE writing to the caller's buffer so a rejected write leaves the
        // span untouched.
        var capDataLen = GetCapabilitiesDataLength(msg.Capabilities);
        var optParamsLen = msg.Capabilities.Count == 0 ? 0 : 2 + capDataLen;
        RequireFitsByte(optParamsLen, nameof(BgpOpenMessage.Capabilities), "optional-parameters");

        var payloadSize = 9 + 1 + optParamsLen;
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

        buffer[p++] = (byte)optParamsLen;

        WriteCapabilities(msg.Capabilities, capDataLen, buffer[p..]);

        return totalLength;
    }

    private static int GetOpenPayloadSize(BgpOpenMessage msg)
    {
        if (msg.Capabilities.Count == 0) return 10; // 9 (fixed) + 1 (optParams length byte)
        // Capabilities present: 9 (fixed) + 1 (optParams length byte) + 2 (Capabilities TLV header) + capDataLen
        return 12 + GetCapabilitiesDataLength(msg.Capabilities);
    }

    private static int GetCapabilitiesDataLength(List<BgpCapabilityInfo> capabilities)
    {
        var capDataLen = 0;
        foreach (var cap in capabilities)
            capDataLen += 2 + cap.Data.Length;
        return capDataLen;
    }

    private static void WriteCapabilities(List<BgpCapabilityInfo> capabilities, int capDataLen, Span<byte> buffer)
    {
        if (capabilities.Count == 0) return;

        var p = 0;
        buffer[p++] = 2; // Type: Capabilities
        buffer[p++] = (byte)capDataLen;

        foreach (var cap in capabilities)
        {
            buffer[p++] = cap.Code;
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

        // Path attributes. Ascending type-code order is an implementation invariant (D15) —
        // RFC 4271 does NOT require attributes to be ordered. Producers already emit ordered
        // (UpdateCodec.BuildUpdateAttributes); an OrderBy here makes the writer guarantee
        // it for any future producer — LINQ OrderBy is stable, so equal type codes keep their
        // caller-supplied relative order (#272, epic #6).
        var attrs = msg.PathAttributes.Count > 1
            ? [.. msg.PathAttributes.OrderBy(static a => a.TypeCode)]
            : msg.PathAttributes;

        var attrsLen = GetAttributesLength(attrs);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[p..], (ushort)attrsLen);
        p += 2;
        foreach (var attr in attrs)
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

    /// <summary>
    /// Whether an attribute is serialized with the two-octet length field. RFC 4271 §4.3 lets the
    /// Extended Length bit select the field width independently of the value length — it is only
    /// <em>required</em> above 255 octets, not forbidden below — so a caller-supplied flag is
    /// honoured rather than ignored.
    /// <para>
    /// The single source of truth for both <see cref="GetAttributesLength"/> and
    /// <see cref="WriteAttribute"/>: previously they each re-derived it from
    /// <c>Data.Length &gt; 255</c> while the flags byte was written through verbatim, so an
    /// attribute arriving with 0x10 already set and a value of 255 octets or fewer produced a TLV
    /// whose flags declared a two-octet length field followed by a one-octet one (#291).
    /// </para>
    /// </summary>
    private static bool UsesExtendedLength(PathAttribute attr) =>
        attr.Data.Length > 255 || (attr.Flags & BgpConstants.Attribute.FlagExtendedLength) != 0;

    private static int GetAttributesLength(List<PathAttribute> attributes)
    {
        var len = 0;
        foreach (var attr in attributes)
        {
            len += 2; // flags + type code
            len += UsesExtendedLength(attr) ? 2 : 1; // length field width
            len += attr.Data.Length;
        }
        return len;
    }

    private static int WriteAttribute(PathAttribute attr, Span<byte> buffer)
    {
        // RFC 4271 §4.3: flag bit 0x08 is reserved and MUST be zero on the wire. BgpMessageReader
        // rejects it on the way in (#272); emitting it would make the writer produce a frame its
        // own reader refuses — the same round-trip break this PR fixes for the Extended Length bit.
        // Checked BEFORE anything is written so a rejected attribute leaves the caller's span
        // untouched, matching WriteOpen's RequireFitsByte guard.
        if ((attr.Flags & BgpConstants.Attribute.FlagReserved) != 0)
            throw new ArgumentOutOfRangeException(nameof(attr), attr.Flags,
                $"Path attribute flag bit 0x{BgpConstants.Attribute.FlagReserved:X2} is reserved and must be zero (RFC 4271 §4.3).");

        var extended = UsesExtendedLength(attr);

        var p = 0;
        // Emit the flag that matches the field actually written. Above 255 octets the bit is
        // required, so set it even when the caller left it clear.
        buffer[p++] = extended ? (byte)(attr.Flags | BgpConstants.Attribute.FlagExtendedLength) : attr.Flags;
        buffer[p++] = attr.TypeCode;

        if (extended)
        {
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

    private static int WriteRouteRefresh(BgpRouteRefreshMessage msg, Span<byte> buffer)
    {
        var totalLength = BgpConstants.MessageHeaderSize + 4;
        WriteHeader(BgpMessageType.RouteRefresh, totalLength, buffer);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[BgpConstants.MessageHeaderSize..], msg.Afi);
        buffer[BgpConstants.MessageHeaderSize + 2] = msg.Reserved;
        buffer[BgpConstants.MessageHeaderSize + 3] = msg.Safi;
        return totalLength;
    }

    #endregion
}
