namespace BGPLite.Protocol;

/// <summary>
/// Negotiated OPEN parameters produced by <see cref="OpenNegotiator.Validate"/>.
/// </summary>
public sealed record OpenNegotiation(
    uint RemoteAsn,
    bool RemoteFourByteAsn,
    bool LocalFourByteAsn,
    bool RemoteRouteRefresh,
    ushort NegotiatedHoldTime,
    TimeSpan KeepAliveInterval);

/// <summary>
/// Pure OPEN validation + capability negotiation (RFC 4271 §4.2/§6.2, RFC 6793, RFC 2918). Verifies
/// the BGP version, 4-octet-ASN capability well-formedness, the expected peer ASN
/// (<paramref name="expectedRemoteAsn"/> is null for auto-registered/unknown peers, accepting any
/// declared ASN), the hold time (0 or ≥ 3), and the BGP Identifier (non-zero, no collision with the
/// local router ID), then derives the negotiated session parameters. Throws
/// <see cref="BgpNotificationException"/> with the RFC-mandated error/sub-error on rejection —
/// the session layer catches it and emits the NOTIFICATION.
/// <para>
/// Hold time negotiation (#224, RFC 4271 §6.2.2): the negotiated value is the smaller of the
/// locally configured <paramref name="localHoldTime"/> and the peer's <c>open.HoldTime</c>. A
/// value of 0 means "timer disabled" (RFC 4271 §4.2) — if either side proposes 0, the negotiated
/// hold time is 0 and the keepalive/hold timers are disabled for the session. This matches the
/// common practice of major implementations (Cisco/Juniper).
/// </para>
/// <para>
/// #269: moved verbatim from <c>BgpSession</c> (Server) so the protocol library owns the full
/// OPEN contract — a consumer of the standalone package needs no session machinery to validate
/// an OPEN.
/// </para>
/// </summary>
public static class OpenNegotiator
{
    public static OpenNegotiation Validate(BgpOpenMessage open, uint? expectedRemoteAsn, uint localRouterId, int localHoldTime)
    {
        // #224: the local hold time is the locally configured BgpConfig.HoldTime (validated at
        // config-load: 0 or ≥3). Guard the entry point defensively so a future caller (or a
        // unit test) cannot pass an out-of-range value that would silently corrupt the negotiation:
        // a negative value would survive the Math.Min below and truncate to a bogus ushort.
        if (localHoldTime != 0 && localHoldTime < 3)
            throw new ArgumentOutOfRangeException(nameof(localHoldTime), localHoldTime,
                $"Local hold time must be 0 (disabled) or at least 3 seconds (RFC 4271 §4.2).");

        if (open.Version != BgpConstants.BgpVersion)
            // RFC 4271 §6.2: the Data field indicates the largest locally-supported version less
            // than the peer's bid, or the smallest locally-supported version when even that is
            // larger. BGPLite supports only version 4, so both branches resolve to 4 (#317).
            // Mirrors BgpMessageReader.ParseOpen byte-for-byte — one wire behavior for one
            // condition regardless of which reject site fires.
            throw new BgpNotificationException(
                BgpConstants.Error.OpenMessageError, BgpConstants.SubError.UnsupportedVersion,
                $"Unsupported BGP version: {open.Version}",
                [(byte)(BgpConstants.BgpVersion >> 8), (byte)BgpConstants.BgpVersion]);

        var malformedFourOctetAsnCapability = UpdateCodec.GetMalformedFourOctetAsnCapabilityData(open);
        if (malformedFourOctetAsnCapability.Length > 0)
        {
            throw new BgpNotificationException(
                BgpConstants.Error.OpenMessageError,
                BgpConstants.SubError.UnsupportedCapability,
                "Malformed 4-octet ASN capability",
                malformedFourOctetAsnCapability);
        }

        var remoteFourByteAsn = CapabilityHelper.GetRemoteAsn(open).HasValue;
        var remoteAsn = CapabilityHelper.GetEffectiveAsn(open);
        var remoteRouteRefresh = open.Capabilities.Any(c => c.Code == BgpConstants.Capability.RouteRefresh);

        // RFC 7607 §2: "If a BGP speaker receives zero as the peer AS in an OPEN message, it MUST
        // abort the connection and send a NOTIFICATION with Error Code 'OPEN Message Error' and
        // subcode 'Bad Peer AS'." Both fields are checked: the declared My Autonomous System and
        // the effective ASN (the 4-octet capability value when present, which takes precedence per
        // RFC 6793 §4.1). Without this an AS-0 peer was accepted and — since BGPLite auto-registers
        // unknown peers — persisted in the PeerStore as a real peer (#300).
        if (open.Asn == 0 || remoteAsn == 0)
            throw new BgpNotificationException(BgpConstants.Error.OpenMessageError, BgpConstants.SubError.BadPeerAs, "Invalid peer AS: 0 (RFC 7607)");

        if (expectedRemoteAsn.HasValue && remoteAsn != expectedRemoteAsn.Value)
            throw new BgpNotificationException(BgpConstants.Error.OpenMessageError, BgpConstants.SubError.BadPeerAs, $"Unexpected ASN: expected {expectedRemoteAsn}, got {remoteAsn}");

        var peerHoldTime = open.HoldTime;
        if (peerHoldTime != 0 && peerHoldTime < 3)
            throw new BgpNotificationException(BgpConstants.Error.OpenMessageError, BgpConstants.SubError.UnacceptableHoldTime, $"Unacceptable hold time: {peerHoldTime}");

        // BGP Identifier must be non-zero and must not collide with our own (RFC 4271 §6.2).
        if (open.RouterId == 0)
            throw new BgpNotificationException(BgpConstants.Error.OpenMessageError, BgpConstants.SubError.BadBgpIdentifier, "Invalid BGP identifier: 0.0.0.0");

        if (open.RouterId == localRouterId)
            throw new BgpNotificationException(BgpConstants.Error.OpenMessageError, BgpConstants.SubError.BadBgpIdentifier, "BGP identifier collision with local RouterId");

        // #224: negotiate hold time = min(local, peer) per RFC 4271 §6.2.2. A 0 on either side
        // disables the timer (RFC 4271 §4.2) — Math.Min with 0 yields 0 naturally, so no special
        // case is needed: either-side-zero → zero, which is exactly the "either side disables"
        // semantics. The peer's value was already validated above (0 or ≥3); local is validated at
        // config-load time (BgpConfig), and the argument guard at the top of this method rejects
        // out-of-range local values before reaching here.
        var negotiatedHoldTime = (ushort)Math.Min(localHoldTime, peerHoldTime);

        var keepAliveInterval = negotiatedHoldTime == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Max(negotiatedHoldTime / 3, 1));

        return new OpenNegotiation(
            remoteAsn,
            remoteFourByteAsn,
            remoteFourByteAsn, // RFC 6793 §6: AS_PATH encoding follows the negotiated capability
            remoteRouteRefresh,
            negotiatedHoldTime,
            keepAliveInterval);
    }
}
