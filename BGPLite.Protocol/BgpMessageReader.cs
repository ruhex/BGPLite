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
        // #105: SequenceEqual over ReadOnlySpan<byte> replaces the hand-rolled byte-by-byte loop —
        // idiomatic, vectorizable by the JIT, and the same semantics.
        if (!marker.SequenceEqual(BgpConstants.Marker))
            throw new BgpParseException("Invalid BGP marker");
    }

    #region OPEN

    private static BgpOpenMessage ParseOpen(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 10)
            throw new BgpParseException($"OPEN message too short: {payload.Length}",
                BgpConstants.Error.OpenMessageError, BgpConstants.SubError.Unspecific);

        var version = payload[0];
        if (version != BgpConstants.BgpVersion)
            throw new BgpParseException($"Unsupported BGP version: {version}",
                BgpConstants.Error.OpenMessageError, BgpConstants.SubError.UnsupportedVersion);

        var asn = BinaryPrimitives.ReadUInt16BigEndian(payload[1..]);
        var holdTime = BinaryPrimitives.ReadUInt16BigEndian(payload[3..]);
        var routerId = BinaryPrimitives.ReadUInt32BigEndian(payload[5..]);
        var optParamsLen = payload[9];

        var capabilities = new List<BgpCapabilityInfo>();
        if (optParamsLen > 0)
        {
            // #234: the declared optional-parameters length is authoritative (RFC 4271 §4.2) — a
            // value running past the message bytes is a malformed OPEN, not a silently shrinkable
            // one. Previously the guard skipped parsing entirely, dropping capabilities (e.g. a
            // corrupted Four-Octet-ASN TLV silently downgraded the session to 2-byte AS).
            if (payload.Length < 10 + optParamsLen)
                throw new BgpParseException(
                    $"OPEN optional-parameters length {optParamsLen} exceeds message: have {payload.Length - 10} bytes",
                    BgpConstants.Error.OpenMessageError, BgpConstants.SubError.Unspecific);
            ParseOptParameters(payload[10..][..optParamsLen], capabilities);
        }

        return new BgpOpenMessage
        {
            Version = version,
            Asn = asn,
            HoldTime = holdTime,
            RouterId = routerId,
            Capabilities = capabilities
        };
    }

    // #234: a TLV whose declared length runs past the buffer is malformed OPEN content
    // (RFC 4271 §4.2, RFC 5492 §3) and must reject the message — the previous silent `break`
    // treated truncation as "capability absent", masking wire corruption (e.g. a truncated
    // Four-Octet-ASN TLV silently downgraded the session to a 2-byte AS).
    private static void ParseOptParameters(ReadOnlySpan<byte> data, List<BgpCapabilityInfo> capabilities)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            if (offset + 2 > data.Length)
                throw new BgpParseException(
                    $"Truncated OPEN optional-parameter header at offset {offset}: have {data.Length - offset} bytes, need 2",
                    BgpConstants.Error.OpenMessageError, BgpConstants.SubError.Unspecific);
            var paramType = data[offset++];
            var paramLen = data[offset++];

            if (offset + paramLen > data.Length)
                throw new BgpParseException(
                    $"Truncated OPEN optional-parameter value: type {paramType} declares {paramLen} bytes at offset {offset}, have {data.Length - offset}",
                    BgpConstants.Error.OpenMessageError, BgpConstants.SubError.Unspecific);

            if (paramType == 2) // Capability
                ParseCapabilities(data[offset..][..paramLen], capabilities);

            offset += paramLen;
        }
    }

    private static void ParseCapabilities(ReadOnlySpan<byte> data, List<BgpCapabilityInfo> capabilities)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            if (offset + 2 > data.Length)
                throw new BgpParseException(
                    $"Truncated capability header at offset {offset}: have {data.Length - offset} bytes, need 2",
                    BgpConstants.Error.OpenMessageError, BgpConstants.SubError.Unspecific);
            var code = data[offset++];
            var len = data[offset++];

            if (offset + len > data.Length)
                throw new BgpParseException(
                    $"Truncated capability value: code {code} declares {len} bytes at offset {offset}, have {data.Length - offset}",
                    BgpConstants.Error.OpenMessageError, BgpConstants.SubError.Unspecific);

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
            throw new BgpParseException($"UPDATE message too short: {payload.Length}",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.Unspecific);

        var offset = 0;

        var withdrawnLen = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
        offset += 2;
        // #222: a declared length that runs past the payload is stream-level corruption, not a
        // per-UPDATE content error — surface it as a parse exception (Update Message Error) so the
        // caller can treat-as-withdraw instead of throwing ArgumentOutOfRangeException out of Slice.
        if (offset + withdrawnLen > payload.Length)
            throw new BgpParseException($"UPDATE withdrawn-routes length {withdrawnLen} exceeds payload",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.Unspecific);

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
        if (offset + attrsLen > payload.Length)
            throw new BgpParseException($"UPDATE path-attributes length {attrsLen} exceeds payload",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.Unspecific);

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
        // #222: bounds-check before every indexed read. Previously a truncated TLV (declared length
        // larger than the buffer, or fewer than 2 header bytes) threw ArgumentOutOfRangeException out
        // of Span.Slice / indexing, which escaped ReadLoopAsync (it only catches OCE/IOException) and
        // tore down the session with a generic Cease — a single malformed UPDATE killed the peer.
        // Now these surface as BgpParseException (Update Message Error) and are handled by the
        // treat-as-withdraw path. RFC 7606 §2 / RFC 4271 §6.3.
        if (data.Length < 2)
            throw new BgpParseException($"Truncated path attribute header: have {data.Length}, need 2",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.Unspecific);

        var flags = data[0];
        var typeCode = data[1];
        var offset = 2;

        int length;
        if ((flags & BgpConstants.Attribute.FlagExtendedLength) != 0)
        {
            if (data.Length < offset + 2)
                throw new BgpParseException($"Truncated extended-length path attribute: have {data.Length}, need {offset + 2}",
                    BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.Unspecific);
            length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += 2;
        }
        else
        {
            if (data.Length < offset + 1)
                throw new BgpParseException($"Truncated path attribute length byte: have {data.Length}, need {offset + 1}",
                    BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.Unspecific);
            length = data[offset];
            offset += 1;
        }

        if (offset + length > data.Length)
            throw new BgpParseException($"Truncated path attribute value: declared {length} at offset {offset}, have {data.Length}",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.Unspecific);

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

/// <summary>
/// Thrown by the BGP message codec when an inbound message is malformed. Carries the
/// RFC 4271 NOTIFICATION error code/subcode that should be sent to the peer so the session
/// handler (<c>BgpSession.RunAsync</c>) emits the right NOTIFICATION instead of a generic
/// Message Header Error (issue #223).
/// <para>
/// <see cref="ErrorCode"/>/<see cref="SubErrorCode"/> are nullable: a <c>null</c> error code
/// means "this was a fixed-header (marker/length/type) parse failure" → Message Header Error
/// (RFC 4271 §6.1). OPEN-body failures set Open Message Error (§6.2); UPDATE-body failures
/// set Update Message Error (§6.3). A <c>null</c> sub-error code maps to Unspecific (0).
/// </para>
/// </summary>
public sealed class BgpParseException : Exception
{
    public BgpParseException(string message, byte? errorCode = null, byte? subErrorCode = null) : base(message)
    {
        ErrorCode = errorCode;
        SubErrorCode = subErrorCode;
    }

    public BgpParseException(string message, Exception inner) : base(message, inner) { }

    /// <summary>RFC 4271 §6 error code the peer should be notified with, or <c>null</c> for a
    /// fixed-header failure (Message Header Error).</summary>
    public byte? ErrorCode { get; }
    /// <summary>RFC 4271 §6 sub-error code, or <c>null</c> for Unspecific (0).</summary>
    public byte? SubErrorCode { get; }
}
