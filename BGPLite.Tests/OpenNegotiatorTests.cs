using BGPLite.Protocol;
using BGPLite.Server;

namespace BGPLite.Tests;

/// <summary>
/// Direct unit tests for the pure <see cref="OpenNegotiator.Validate(BgpOpenMessage, uint?, uint, int)"/>
/// extraction (#97). Every OPEN-validation branch is exercised without standing up a BgpSession
/// over a live socket — mirroring how the RFC-6793 tests exercise MergeAsPathWithAs4Path.
/// <para>
/// The <c>Negotiate</c> helper defaults <c>localHoldTime</c> to <c>180</c> (the <c>BgpConfig</c>
/// default), so the legacy tests where the peer also proposes 180 keep their original semantics.
/// The #224 hold-time-negotiation tests pass an explicit <c>localHoldTime</c> to exercise the
/// <c>min(local, peer)</c> rule.
/// </para>
/// </summary>
public class OpenNegotiatorTests
{
    private const uint LocalRouterId = 0x0A000001u; // 10.0.0.1
    private const uint PeerRouterId = 0x0A000002u;  // 10.0.0.2
    private const uint AsnFourOctet = 200000u;      // > 16 bits → exercises the 4-octet capability
    private const int DefaultLocalHoldTime = 180;    // matches BgpConfig.HoldTime default

    private static BgpOpenMessage Open(
        ushort holdTime = 180,
        uint routerId = PeerRouterId,
        List<BgpCapabilityInfo>? capabilities = null,
        byte version = BgpConstants.BgpVersion,
        ushort asn = 65002) =>
        new()
        {
            Version = version,
            Asn = asn,
            HoldTime = holdTime,
            RouterId = routerId,
            Capabilities = capabilities ?? [BgpCapabilityInfo.FourOctetAsn(AsnFourOctet)]
        };

    /// <summary>Shorthand for the 4-arg ValidateOpen with the default local hold time (180s).</summary>
    private static OpenNegotiation Negotiate(
        BgpOpenMessage open, uint? expectedRemoteAsn = AsnFourOctet, int localHoldTime = DefaultLocalHoldTime) =>
        OpenNegotiator.Validate(open, expectedRemoteAsn, LocalRouterId, localHoldTime);

    [Fact]
    public void ValidOpen_NegotiatesFourByteAsn_RouteRefresh_KeepAlive()
    {
        var open = Open(capabilities:
        [
            BgpCapabilityInfo.FourOctetAsn(AsnFourOctet),
            new() { Code = BgpConstants.Capability.RouteRefresh }
        ]);

        var n = Negotiate(open, expectedRemoteAsn: AsnFourOctet);

        Assert.Equal(AsnFourOctet, n.RemoteAsn);
        Assert.True(n.RemoteFourByteAsn);
        Assert.True(n.LocalFourByteAsn); // RFC 6793 §6 — AS_PATH encoding follows negotiated capability
        Assert.True(n.RemoteRouteRefresh);
        Assert.Equal(180, n.NegotiatedHoldTime);
        Assert.Equal(TimeSpan.FromSeconds(60), n.KeepAliveInterval); // max(180/3, 1) = 60
    }

    [Fact]
    public void TwoByteAsn_NegotiatesFourByteFalse()
    {
        var open = Open(asn: 65002, capabilities: []); // no 4-octet capability, no route refresh

        var n = Negotiate(open, expectedRemoteAsn: 65002);

        Assert.Equal(65002u, n.RemoteAsn);
        Assert.False(n.RemoteFourByteAsn);
        Assert.False(n.LocalFourByteAsn);
        Assert.False(n.RemoteRouteRefresh);
    }

    [Fact]
    public void HoldTime_Zero_Accepted_WithZeroKeepAlive()
    {
        var n = Negotiate(Open(holdTime: 0));

        Assert.Equal(0, n.NegotiatedHoldTime);
        Assert.Equal(TimeSpan.Zero, n.KeepAliveInterval);
    }

    [Fact]
    public void HoldTime_Three_Accepted_KeepAliveClampedToOne()
    {
        var n = Negotiate(Open(holdTime: 3));

        Assert.Equal(TimeSpan.FromSeconds(1), n.KeepAliveInterval); // max(3/3, 1) = 1
    }

    [Fact]
    public void UnsupportedVersion_Throws()
    {
        var ex = Assert.Throws<BgpNotificationException>(
            () => Negotiate(Open(version: 3)));

        Assert.Equal(BgpConstants.Error.OpenMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.UnsupportedVersion, ex.SubErrorCode);
    }

    [Fact]
    public void MalformedFourOctetAsnCapability_Throws()
    {
        var open = Open(capabilities:
        [
            new() { Code = BgpConstants.Capability.FourOctetAsn, Data = [0x01, 0x02, 0x03] } // 3 bytes, not 4
        ]);

        var ex = Assert.Throws<BgpNotificationException>(
            () => Negotiate(open, expectedRemoteAsn: null));

        Assert.Equal(BgpConstants.Error.OpenMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.UnsupportedCapability, ex.SubErrorCode);
        Assert.NotNull(ex.NotificationData); // carries the malformed capability TLV
    }

    [Fact]
    public void UnexpectedAsn_Throws_BadPeerAs()
    {
        var ex = Assert.Throws<BgpNotificationException>(
            () => Negotiate(Open(), expectedRemoteAsn: 65010));

        Assert.Equal(BgpConstants.SubError.BadPeerAs, ex.SubErrorCode);
    }

    [Fact]
    public void ExpectedAsn_Null_AcceptsAnyAsn() // auto-register / unknown-peer path
    {
        var n = Negotiate(Open(), expectedRemoteAsn: null);

        Assert.Equal(AsnFourOctet, n.RemoteAsn);
    }

    [Fact]
    public void HoldTime_TooLow_Throws_UnacceptableHoldTime()
    {
        var ex = Assert.Throws<BgpNotificationException>(
            () => Negotiate(Open(holdTime: 2)));

        Assert.Equal(BgpConstants.SubError.UnacceptableHoldTime, ex.SubErrorCode);
    }

    [Fact]
    public void RouterId_Zero_Throws_BadBgpIdentifier()
    {
        var ex = Assert.Throws<BgpNotificationException>(
            () => Negotiate(Open(routerId: 0)));

        Assert.Equal(BgpConstants.SubError.BadBgpIdentifier, ex.SubErrorCode);
    }

    [Fact]
    public void RouterId_CollisionWithLocal_Throws_BadBgpIdentifier()
    {
        var ex = Assert.Throws<BgpNotificationException>(
            () => Negotiate(Open(routerId: LocalRouterId)));

        Assert.Equal(BgpConstants.SubError.BadBgpIdentifier, ex.SubErrorCode);
    }

    // ---- #224: hold time negotiation = min(local, peer) per RFC 4271 §6.2.2 ----

    /// <summary>
    /// #224: when the peer proposes a SMALLER hold time than local, the negotiated value is the
    /// peer's (min). Keepalive is derived from the negotiated value, not the peer's raw value —
    /// so a peer that proposes 30 against a local 180 yields negotiated=30, keepalive=max(30/3,1)=10.
    /// </summary>
    [Fact]
    public void HoldTime_PeerSmallerThanLocal_NegotiatesToPeer()
    {
        var n = Negotiate(Open(holdTime: 30), localHoldTime: 180);

        Assert.Equal(30, n.NegotiatedHoldTime);
        Assert.Equal(TimeSpan.FromSeconds(10), n.KeepAliveInterval); // max(30/3, 1) = 10
    }

    /// <summary>
    /// #224: when the peer proposes a LARGER hold time than local, the negotiated value is the
    /// local (min). Guards against the previous behaviour of always taking the peer's value — a
    /// peer proposing 180 against a local 9 (the minimum useful hold time for fast dead-peer
    /// detection) must yield 9, not 180.
    /// </summary>
    [Fact]
    public void HoldTime_PeerLargerThanLocal_NegotiatesToLocal()
    {
        var n = Negotiate(Open(holdTime: 180), localHoldTime: 9);

        Assert.Equal(9, n.NegotiatedHoldTime);
        Assert.Equal(TimeSpan.FromSeconds(3), n.KeepAliveInterval); // max(9/3, 1) = 3
    }

    /// <summary>
    /// #224: equal local and peer hold times negotiate to that value (boundary of the min rule).
    /// </summary>
    [Fact]
    public void HoldTime_PeerEqualsLocal_NegotiatesToThatValue()
    {
        var n = Negotiate(Open(holdTime: 90), localHoldTime: 90);

        Assert.Equal(90, n.NegotiatedHoldTime);
        Assert.Equal(TimeSpan.FromSeconds(30), n.KeepAliveInterval);
    }

    /// <summary>
    /// #224: if the LOCAL side disables the timer (0), the negotiated hold time is 0 even when the
    /// peer proposes a positive value — the session runs without keepalive/hold timers. Matches the
    /// "either side disables" semantics (RFC 4271 §4.2) and the common implementation practice
    /// (Cisco/Juniper). Complements HoldTime_Zero_Accepted_WithZeroKeepAlive (peer=0 case).
    /// </summary>
    [Fact]
    public void HoldTime_LocalZero_DisablesTimer_EvenWhenPeerProposesPositive()
    {
        var n = Negotiate(Open(holdTime: 180), localHoldTime: 0);

        Assert.Equal(0, n.NegotiatedHoldTime);
        Assert.Equal(TimeSpan.Zero, n.KeepAliveInterval);
    }
}
