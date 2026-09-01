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
            throw BadMessageLength(length);

        if (buffer.Length < length)
            throw new BgpParseException($"Incomplete message: have {buffer.Length}, need {length}");

        var type = (BgpMessageType)buffer[18];

        // RFC 4271 §6.1: "if the Length field of a KEEPALIVE message is not equal to 19 ... then the
        // Error Subcode MUST be set to Bad Message Length." Nothing checked this — type 4 mapped
        // straight to the singleton and any trailing bytes were silently ignored, so a peer could
        // pad a KEEPALIVE arbitrarily and BGPLite would accept it (#300).
        //
        // ONLY KEEPALIVE is validated here, deliberately. §6.1 also gives per-type minimums for
        // OPEN (29), UPDATE (23) and NOTIFICATION (21), and all three are already rejected today —
        // but by their body parsers, as Open/Update Message Error rather than as header errors.
        // Reclassifying them would change behaviour in ways that are not improvements:
        //   - OPEN: #223 deliberately made a too-short OPEN report Open Message Error (2), with a
        //     test asserting it. Moving it to Message Header Error would flip that decision, and
        //     would make ParseOpen's own `payload.Length < 10` guard unreachable.
        //   - UPDATE: a body error is routed to treat-as-withdraw and the session SURVIVES. A header
        //     error tears it down — so a 22-byte UPDATE would become a remote session kill, exactly
        //     the class of defect #222 and #284 closed. RFC 7606 §3 revises UPDATE error handling
        //     toward keeping the session, and that direction wins here.
        //   - NOTIFICATION: already a header error, just without the subcode; not worth a special
        //     case of its own.
        // Recorded rather than silently skipped; see the PR for the full reasoning.
        if (type == BgpMessageType.Keepalive && length != BgpConstants.MessageHeaderSize)
            throw BadMessageLength(length);

        var payload = buffer[BgpConstants.MessageHeaderSize..length];

        return type switch
        {
            BgpMessageType.Open => ParseOpen(payload),
            BgpMessageType.Keepalive => BgpKeepaliveMessage.Instance,
            BgpMessageType.Update => ParseUpdate(payload),
            BgpMessageType.Notification => ParseNotification(payload),
            BgpMessageType.RouteRefresh => ParseRouteRefresh(payload),
            // RFC 4271 §6.1: "the Error Subcode MUST be set to Bad Message Type. The Data field
            // MUST contain the erroneous Message Type field."
            _ => throw new BgpParseException($"Unknown message type: {(byte)type}",
                subErrorCode: BgpConstants.SubError.BadMessageType, notificationData: [(byte)type])
        };
    }

    /// <summary>
    /// RFC 4271 §6.1 Bad Message Length: "The Data field MUST contain the erroneous Length field."
    /// <c>ErrorCode</c> is deliberately left <c>null</c> — that is how <c>BgpSession.ReadLoopAsync</c>
    /// tells a fixed-header failure (tear the session down) from a message-body failure
    /// (treat-as-withdraw), and turning it into a body error here would both violate §6.1 and
    /// desync the stream, since the payload has not been consumed (#223, #300).
    /// </summary>
    private static BgpParseException BadMessageLength(int length) =>
        new($"Invalid message length: {length}",
            subErrorCode: BgpConstants.SubError.BadMessageLength,
            notificationData: [(byte)(length >> 8), (byte)length]);

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
        //
        // RFC 4271 §6.1: "If the Marker field of the message header is not as expected, then a
        // synchronization error has occurred and the Error Subcode MUST be set to Connection Not
        // Synchronized." This previously emitted Unspecific, which tells the peer's operator
        // nothing about the one header failure that actually means "our streams have diverged"
        // (#300). ErrorCode stays null so it remains a fixed-header failure that tears the session
        // down, which is exactly right for a desync.
        if (!marker.SequenceEqual(BgpConstants.Marker))
            throw new BgpParseException("Invalid BGP marker",
                subErrorCode: BgpConstants.SubError.ConnectionNotSynchronized);
    }

    #region OPEN

    private static BgpOpenMessage ParseOpen(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 10)
            throw new BgpParseException($"OPEN message too short: {payload.Length}",
                BgpConstants.Error.OpenMessageError, BgpConstants.SubError.Unspecific);

        var version = payload[0];
        if (version != BgpConstants.BgpVersion)
            // RFC 4271 §6.2 Unsupported Version Number: the Data field "indicates the largest,
            // locally-supported version number less than the version the remote BGP peer bid …
            // or if the smallest locally-supported version number is larger than the peer's bid,
            // the smallest locally-supported version number". BGPLite supports only version 4, so
            // both branches resolve to 4: bid>4 → largest-below-bid is 4; bid<4 → smallest is 4.
            // Without the field a BGPv3 speaker gets no downgrade hint (#317). Byte-identical
            // with OpenNegotiator.Validate, the other reject site for the same condition.
            throw new BgpParseException($"Unsupported BGP version: {version}",
                BgpConstants.Error.OpenMessageError, BgpConstants.SubError.UnsupportedVersion,
                notificationData: [(byte)(BgpConstants.BgpVersion >> 8), (byte)BgpConstants.BgpVersion]);

        var asn = BinaryPrimitives.ReadUInt16BigEndian(payload[1..]);
        var holdTime = BinaryPrimitives.ReadUInt16BigEndian(payload[3..]);
        var routerId = BinaryPrimitives.ReadUInt32BigEndian(payload[5..]);
        var optParamsLen = payload[9];

        var capabilities = new List<BgpCapabilityInfo>();
        // #234: the declared optional-parameters length is authoritative (RFC 4271 §4.2) — it
        // must match the bytes present exactly, including the zero-length case. Previously a
        // length running past the message silently skipped parsing (dropping capabilities, e.g.
        // corrupting a Four-Octet-ASN TLV into a 2-byte-AS session), while surplus trailing
        // bytes were silently ignored.
        if (payload.Length != 10 + optParamsLen)
            throw new BgpParseException(
                $"OPEN optional-parameters length {optParamsLen} does not match message: have {payload.Length - 10} bytes",
                BgpConstants.Error.OpenMessageError, BgpConstants.SubError.Unspecific);
        if (optParamsLen > 0)
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
            {
                ParseCapabilities(data[offset..][..paramLen], capabilities);
            }
            else
            {
                // RFC 4271 §6.2 (#329): an optional parameter type this speaker does not recognize
                // must be answered with Unsupported Optional Parameter (2/4) — the sender otherwise
                // believes the parameter was accepted (e.g. RFC 9072 Extended Optional Parameters,
                // type 255). Unrecognized CAPABILITIES inside type 2 stay ignored per RFC 5492 §4.2.
                throw new BgpParseException(
                    $"Unsupported OPEN optional parameter type {paramType} (length {paramLen})",
                    BgpConstants.Error.OpenMessageError, BgpConstants.SubError.UnsupportedOptionalParameter);
            }

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
        // RFC 4271 §6.3: "If the Withdrawn Routes Length or Total Attribute Length is too large
        // ... the Error Subcode MUST be set to Malformed Attribute List."
        if (offset + withdrawnLen > payload.Length)
            throw new BgpParseException($"UPDATE withdrawn-routes length {withdrawnLen} exceeds payload",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.MalformedAttributeList);

        var withdrawn = new List<IpPrefix>();
        if (withdrawnLen > 0)
        {
            var withdrawnEnd = offset + withdrawnLen;
            while (offset < withdrawnEnd)
            {
                // Slice to the declared end of the withdrawn-routes section (not payload end), the
                // same rule the attribute loop below already follows (#245). Decoding against the
                // payload let a prefix whose declared value crosses withdrawnEnd consume the bytes
                // of the next field: either desyncing every subsequent field (a prefix the peer
                // never withdrew was reported as withdrawn) or, when the overrun reached the end of
                // the payload, throwing ArgumentOutOfRangeException out of the codec — which is not
                // a BgpParseException, so ReadLoopAsync's treat-as-withdraw filter never saw it and
                // a 23-byte UPDATE tore down the session (#284, same failure mode as #222).
                var (prefix, consumed) = PrefixCodec.Decode(payload[offset..withdrawnEnd]);
                withdrawn.Add(prefix);
                offset += consumed;
            }
        }

        // The withdrawn-routes section may end exactly at the payload end, leaving no room for the
        // Total Path Attribute Length field. The `payload.Length < 4` guard above only covers an
        // UPDATE with no withdrawn routes, so bounds-check here as well (#284).
        if (offset + 2 > payload.Length)
            throw new BgpParseException(
                $"UPDATE truncated before Total Path Attribute Length: have {payload.Length - offset} bytes at offset {offset}, need 2",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.MalformedAttributeList);

        var attrsLen = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
        offset += 2;
        if (offset + attrsLen > payload.Length)
            throw new BgpParseException($"UPDATE path-attributes length {attrsLen} exceeds payload",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.MalformedAttributeList);

        var attributes = new List<PathAttribute>();
        MpReachCodec.MpReachV6? mpReachV6 = null;
        IReadOnlyList<IpPrefix>? mpUnreachV6 = null;
        if (attrsLen > 0)
        {
            var attrsEnd = offset + attrsLen;
            while (offset < attrsEnd)
            {
                // Slice to the declared end of the attribute section (not payload end): an
                // attribute TLV whose declared value crosses attrsEnd must be rejected instead
                // of silently consuming NLRI bytes as attribute data (#245 review finding).
                var (attr, consumed) = ParseAttribute(payload.Slice(offset, attrsEnd - offset));
                offset += consumed;

                // #15 phase 2 (RFC 4760): MP_REACH_NLRI (14) / MP_UNREACH_NLRI (15) carry the
                // IPv6 announcements/withdrawals — decode them into typed fields here and remove
                // from the generic list so ParseRouteAttributes treats the UPDATE as v4-only.
                // Malformed AFI=2 payloads throw BgpParseException (Update Message Error) — the
                // whole UPDATE is discarded with the session kept (D17), matching RFC 7606 §2
                // (the attribute carries NLRI ⇒ treat-as-withdraw).
                if (attr.TypeCode == MpReachCodec.MpReachNlriType)
                {
                    var reach = MpReachCodec.DecodeMpReachV6(attr.Data);
                    mpReachV6 = reach;
                    continue;
                }
                if (attr.TypeCode == MpReachCodec.MpUnreachNlriType)
                {
                    mpUnreachV6 = MpReachCodec.DecodeMpUnreachV6(attr.Data);
                    continue;
                }
                attributes.Add(attr);
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
            Nlri = nlri,
            MpReachV6 = mpReachV6,
            MpUnreachV6 = mpUnreachV6
        };
    }

    private static (PathAttribute attr, int consumed) ParseAttribute(ReadOnlySpan<byte> data)
    {
        // #222: bounds-check before every indexed read. Previously a truncated TLV (declared length
        // larger than the buffer, or fewer than 2 header bytes) threw ArgumentOutOfRangeException out
        // of Span.Slice / indexing, which escaped ReadLoopAsync (it only catches OCE/IOException) and
        // tore down the session with a generic Cease — a single malformed UPDATE killed the peer.
        // Now these surface as BgpParseException (Update Message Error) and are handled by the
        // treat-as-withdraw path. RFC 7606 §2 / RFC 4271 §6.3. Subcodes (#235): a header that
        // cannot even be read (flags/type/length bytes missing) is a malformed attribute list (1);
        // a readable header whose declared value length overshoots the buffer is an attribute
        // length error (5).
        if (data.Length < 2)
            throw new BgpParseException($"Truncated path attribute header: have {data.Length}, need 2",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.MalformedAttributeList);

        var flags = data[0];
        // RFC 4271 §4.3: flag bit 0x08 is reserved and MUST be zero — an attribute with it set
        // is malformed (Attribute Flags Error, subcode 4) and is rejected via treat-as-withdraw (#272).
        if ((flags & BgpConstants.Attribute.FlagReserved) != 0)
            throw new BgpParseException($"Reserved attribute flag bit 0x08 set (flags=0x{flags:X2})",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.AttributeFlagsError);
        var typeCode = data[1];
        var offset = 2;

        int length;
        if ((flags & BgpConstants.Attribute.FlagExtendedLength) != 0)
        {
            if (data.Length < offset + 2)
                throw new BgpParseException($"Truncated extended-length path attribute: have {data.Length}, need {offset + 2}",
                    BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.MalformedAttributeList);
            length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += 2;
        }
        else
        {
            if (data.Length < offset + 1)
                throw new BgpParseException($"Truncated path attribute length byte: have {data.Length}, need {offset + 1}",
                    BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.MalformedAttributeList);
            length = data[offset];
            offset += 1;
        }

        if (offset + length > data.Length)
            throw new BgpParseException($"Truncated path attribute value: declared {length} at offset {offset}, have {data.Length}",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.AttributeLengthError);

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
    private readonly byte[]? _notificationData;

    public BgpParseException(string message, byte? errorCode = null, byte? subErrorCode = null, byte[]? notificationData = null) : base(message)
    {
        ErrorCode = errorCode;
        SubErrorCode = subErrorCode;
        _notificationData = notificationData is null ? null : (byte[])notificationData.Clone();
    }

    public BgpParseException(string message, Exception inner) : base(message, inner) { }

    /// <summary>RFC 4271 §6 error code the peer should be notified with, or <c>null</c> for a
    /// fixed-header failure (Message Header Error).</summary>
    public byte? ErrorCode { get; }
    /// <summary>RFC 4271 §6 sub-error code, or <c>null</c> for Unspecific (0).</summary>
    public byte? SubErrorCode { get; }
    /// <summary>
    /// Contents of the NOTIFICATION Data field, or <c>null</c> when the failure carries none.
    /// RFC 4271 §6.1 requires the erroneous Length field for Bad Message Length and the erroneous
    /// Message Type for Bad Message Type, so the peer's operator gets a usable diagnostic instead
    /// of a bare "unknown error" (#300). Cloned in and out, mirroring
    /// <see cref="BgpNotificationException.NotificationData"/>.
    /// </summary>
    public byte[]? NotificationData => _notificationData is null ? null : (byte[])_notificationData.Clone();
}
