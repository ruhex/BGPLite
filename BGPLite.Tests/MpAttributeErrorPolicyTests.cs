using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// #467: the MP_REACH_NLRI/MP_UNREACH_NLRI error policy. RFC 7606 leaves these attributes
/// explicitly OUTSIDE the keep-alive revision, so the D17 discard-and-keep-alive treatment
/// must not swallow them:
/// <list type="bullet">
/// <item>a DUPLICATE MP attribute → exactly one NOTIFICATION 3/1 and a session reset
/// (RFC 7606 §3(g) MUST);</item>
/// <item>an MP flags conflict (RFC 4760 §4: optional non-transitive) → NOTIFICATION 3/4 and a
/// session reset (RFC 4271 §6.3 baseline);</item>
/// <item>an UNPARSEABLE MP value → the §3(j) "AFI/SAFI disable" choice: the peer's accepted
/// IPv6 routes are withdrawn, the family is ignored for the rest of the session, the session
/// stays up (recorded in D17) — scoped to the SUPPORTED tuple: an unsupported AFI/SAFI only
/// discards its UPDATE, and a value too short to name its tuple resets the session;</item>
/// <item>a non-global MP_REACH next hop (RFC 2545 §3) → that attribute's routes are excluded,
/// route-level, session up.</item>
/// </list>
/// Driven through the <c>IBgpConnection</c> seam with scripted frames, like the #289 tests.
/// </summary>
public class MpAttributeErrorPolicyTests
{
    // RFC 4271 §4.3: Optional = 0x80, Transitive = 0x40. MP_REACH/MP_UNREACH are optional
    // NON-transitive (RFC 4760 §4) → 0x80; the conflict case adds the Transitive bit.
    private const byte MpOptionalNonTransitive = BgpConstants.Attribute.FlagOptional;
    private const byte MpOptionalTransitive = BgpConstants.Attribute.FlagOptional | BgpConstants.Attribute.FlagTransitive;

    // 2001:db8::/32 with next hop 2001:db8::1 — a global unicast next hop.
    private static readonly UInt128 DocumentedPrefix = (UInt128)0x20010DB8 << 96;
    private static readonly UInt128 GlobalNextHop = ((UInt128)0x20010DB8 << 96) | 1;

    [Fact]
    public async Task DuplicateMpReach_TearsDownSession_WithExactlyOneNotification_3_1()
    {
        var routeTable = new RouteTable();
        var (session, run, conn) = await EstablishAsync(routeTable);

        var value = ReachValue(GlobalNextHop, 32, 0x20, 0x01, 0x0D, 0xB8);
        conn.EnqueueFrame(UpdateFrame(MpReachAttribute(MpOptionalNonTransitive, value),
                                      MpReachAttribute(MpOptionalNonTransitive, value)));

        await AssertResetAsync(session, conn, expectedSubError: BgpConstants.SubError.MalformedAttributeList);
        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task TransitiveMpReachFlags_TearDownSession_WithNotification_3_4()
    {
        var routeTable = new RouteTable();
        var (session, run, conn) = await EstablishAsync(routeTable);

        // RFC 4760 §4/§5: MP_REACH/MP_UNREACH are optional NON-transitive; the flags conflict is
        // a session-reset error per the RFC 4271 §6.3 baseline (RFC 7606 does not revise MP).
        var value = ReachValue(GlobalNextHop, 32, 0x20, 0x01, 0x0D, 0xB8);
        conn.EnqueueFrame(UpdateFrame(MpReachAttribute(MpOptionalTransitive, value)));

        await AssertResetAsync(session, conn, expectedSubError: BgpConstants.SubError.AttributeFlagsError);
        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task PartialMpReachFlags_TearDownSession_WithNotification_3_4()
    {
        // #472 review: the Partial bit is equally invalid on a non-transitive attribute — nothing
        // ever re-advertises it un-understood — and joins the RFC 4271 §6.3 baseline reset.
        var routeTable = new RouteTable();
        var (session, run, conn) = await EstablishAsync(routeTable);

        var value = ReachValue(GlobalNextHop, 32, 0x20, 0x01, 0x0D, 0xB8);
        conn.EnqueueFrame(UpdateFrame(MpReachAttribute(BgpConstants.Attribute.FlagOptional | BgpConstants.Attribute.FlagPartial, value)));

        await AssertResetAsync(session, conn, expectedSubError: BgpConstants.SubError.AttributeFlagsError);
        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task UnsupportedMpFamily_DiscardsTheUpdateOnly_SessionAndFamilyStayUp()
    {
        // #472 review: an AFI/SAFI tuple that was never negotiated (RFC 4760 §8) is not a parse
        // failure of a SUPPORTED family — the whole UPDATE is discarded through the D17
        // keep-alive path and neither the session nor IPv6/Unicast is touched.
        var routeTable = new RouteTable();
        var (session, run, conn) = await EstablishAsync(routeTable);

        conn.EnqueueFrame(UpdateFrame(
            OriginAsPathAttributes,
            MpReachAttribute(MpOptionalNonTransitive, ReachValue(GlobalNextHop, 32, 0x20, 0x01, 0x0D, 0xB8))));
        await SettleAsync(conn, () => routeTable.Count == 1);

        conn.EnqueueFrame(UpdateFrame(MpReachAttribute(MpOptionalNonTransitive,
            [0x00, 0x03, 0x01, 0x04, 0xC0, 0x00, 0x02, 0x01, 0x00]))); // AFI=3/SAFI=1 — unsupported
        await SettleAsync(conn);

        Assert.True(session.IsEstablished, "an unsupported tuple is a keep-alive body error, not a reset");
        Assert.NotNull(routeTable.Get(DocumentedPrefix, 32, isIpv4: false)); // the accepted route survives

        // The supported family is still enabled: a valid announcement still installs.
        conn.EnqueueFrame(UpdateFrame(
            OriginAsPathAttributes,
            MpReachAttribute(MpOptionalNonTransitive, ReachValue(GlobalNextHop, 24, 0x20, 0x01, 0x0D))));
        await SettleAsync(conn, () => routeTable.Count == 2);

        await TeardownAsync(session, run);
    }

    // ---- #466: MP_REACH/MP_UNREACH AFI=1/SAFI=1 (IPv4/Unicast) ----

    /// <summary>MP_REACH_NLRI (AFI=1/SAFI=1) value: AFI(2) + SAFI(1) + NH-Len(4) + next hop +
    /// Reserved(1) + classic IPv4 NLRI (length byte + significant address bytes).</summary>
    private static byte[] ReachValueV4(uint nextHop, byte prefixLength, params byte[] addressBytes)
    {
        var value = new List<byte> { 0x00, 0x01, 0x01, 0x04 };
        value.AddRange([(byte)(nextHop >> 24), (byte)(nextHop >> 16), (byte)(nextHop >> 8), (byte)nextHop]);
        value.Add(0x00); // reserved
        value.Add(prefixLength);
        value.AddRange(addressBytes);
        return [.. value];
    }

    [Fact]
    public async Task MpReachV4_Announcement_InstallsLikeClassicNlri()
    {
        // #466 final state: an IPv4 announcement riding MP_REACH installs through the same
        // pipeline as the classic NLRI field — BIRD negotiates MP_IPV4 by default and may
        // carry IPv4 unicast this way.
        var routeTable = new RouteTable();
        var (session, run, conn) = await EstablishAsync(routeTable);

        conn.EnqueueFrame(UpdateFrame(
            OriginAsPathAttributes,
            MpReachAttribute(MpOptionalNonTransitive, ReachValueV4(0xC0000201, 24, 0xC0, 0x00, 0x02))));
        await SettleAsync(conn, () => routeTable.Count == 1);

        Assert.NotNull(routeTable.Get(0xC0000200, 24, isIpv4: true));
        Assert.True(session.IsEstablished);

        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task MpReachV4_InvalidNextHop_TreatAsWithdraw_KeepsSession()
    {
        // RFC 4271 §6.3/§6.8 + RFC 7606 §7.3: the MP-carried IPv4 next hop obeys the classic
        // NEXT_HOP semantics — a multicast value treats the announcement as withdrawn and
        // keeps the session up.
        var routeTable = new RouteTable();
        var (session, run, conn) = await EstablishAsync(routeTable);

        conn.EnqueueFrame(UpdateFrame(
            OriginAsPathAttributes,
            MpReachAttribute(MpOptionalNonTransitive, ReachValueV4(0xE0000001, 24, 0xC0, 0x00, 0x02))));
        await SettleAsync(conn);

        Assert.Equal(0, routeTable.Count);
        Assert.True(session.IsEstablished, "treat-as-withdraw keeps the session up");

        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task MpUnreachV4_WithdrawsTheRoute()
    {
        var routeTable = new RouteTable();
        var (session, run, conn) = await EstablishAsync(routeTable);

        conn.EnqueueFrame(UpdateFrame(
            OriginAsPathAttributes,
            MpReachAttribute(MpOptionalNonTransitive, ReachValueV4(0xC0000201, 24, 0xC0, 0x00, 0x02))));
        await SettleAsync(conn, () => routeTable.Count == 1);

        // MP_UNREACH (AFI=1/SAFI=1): AFI(2) + SAFI(1) + the withdrawn NLRI.
        conn.EnqueueFrame(UpdateFrame(
            MpUnreachAttribute(MpOptionalNonTransitive, [0x00, 0x01, 0x01, 0x18, 0xC0, 0x00, 0x02])));
        await SettleAsync(conn, () => routeTable.Count == 0);

        Assert.Null(routeTable.Get(0xC0000200, 24, isIpv4: true));
        Assert.True(session.IsEstablished);

        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task AfterFamilyDisable_ThePrefixCapBudgetIsReturned()
    {
        // #472 review: RemoveAllOwnedBy does not raise EntryOwnershipLost (the session is
        // discarding its OWN keys), so DisablePeerMpV6 must drop the family's keys from the
        // per-peer prefix set itself — otherwise the cap count drifts and phantom IPv6 keys
        // consume the budget of real IPv4 announcements.
        var routeTable = new RouteTable();
        var (session, run, conn) = await EstablishAsync(routeTable, maxPrefixes: 2);

        conn.EnqueueFrame(UpdateFrame(
            OriginAsPathAttributes,
            MpReachAttribute(MpOptionalNonTransitive, ReachValue(GlobalNextHop, 32, 0x20, 0x01, 0x0D, 0xB8))));
        conn.EnqueueFrame(UpdateFrame(
            OriginAsPathAttributes,
            MpReachAttribute(MpOptionalNonTransitive, ReachValue(GlobalNextHop, 24, 0x20, 0x01, 0x0D))));
        await SettleAsync(conn, () => routeTable.Count == 2);

        conn.EnqueueFrame(UpdateFrame(MpReachAttribute(MpOptionalNonTransitive,
            [0x00, 0x02, 0x01, 0x10, 0x20, 0x01]))); // supported tuple, truncated → disable
        await SettleAsync(conn, () => routeTable.Count == 0);

        // The two IPv4 announcements must fit the cap of 2 — the withdrawn IPv6 keys must not
        // be occupying it.
        conn.EnqueueFrame(AnnounceFrame(24, 0xC0, 0x00, 0x02));
        conn.EnqueueFrame(AnnounceFrame(24, 0xC0, 0x00, 0x01));
        await SettleAsync(conn, () => routeTable.Count == 2);

        Assert.True(session.IsEstablished, "reaching the cap through phantom keys would have reset the session");

        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task MalformedMpReach_WithdrawsAcceptedV6Routes_KeepsSessionUp()
    {
        var routeTable = new RouteTable();
        var (session, run, conn) = await EstablishAsync(routeTable);

        // First, a valid MP_REACH announcement (ORIGIN + AS_PATH + MP_REACH, RFC 4760 §5 shape)
        // installs the route.
        conn.EnqueueFrame(UpdateFrame(
            OriginAsPathAttributes,
            MpReachAttribute(MpOptionalNonTransitive,
                ReachValue(GlobalNextHop, 32, 0x20, 0x01, 0x0D, 0xB8))));
        await SettleAsync(conn, () => routeTable.Count == 1);
        Assert.NotNull(routeTable.Get(DocumentedPrefix, 32, isIpv4: false));

        // Now an UPDATE whose MP_REACH VALUE cannot be parsed — AFI=2/SAFI=1 (the SUPPORTED
        // tuple) with a truncated body. RFC 7606 §3(j): session reset OR AFI/SAFI disable;
        // BGPLite disables the family — the stale route must NOT survive the malformed update.
        conn.EnqueueFrame(UpdateFrame(MpReachAttribute(MpOptionalNonTransitive,
            [0x00, 0x02, 0x01, 0x10, 0x20, 0x01]))); // our AFI/SAFI, truncated next hop
        await SettleAsync(conn, () => routeTable.Count == 0);

        Assert.Null(routeTable.Get(DocumentedPrefix, 32, isIpv4: false));
        Assert.True(session.IsEstablished, "the §3(j) disable choice keeps the session up");
        Assert.DoesNotContain(await SentNotificationsAsync(conn),
            n => n.ErrorCode == BgpConstants.Error.UpdateMessageError);

        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task AfterFamilyDisable_SubsequentMpPayloadsAreIgnored_ClassicNlriStillProcessed()
    {
        var routeTable = new RouteTable();
        var (session, run, conn) = await EstablishAsync(routeTable);

        conn.EnqueueFrame(UpdateFrame(MpReachAttribute(MpOptionalNonTransitive,
            [0x00, 0x02, 0x01, 0x10, 0x20, 0x01]))); // supported tuple, truncated → disable
        await SettleAsync(conn);

        // A later VALID MP_REACH announcement is ignored: the family is disabled.
        conn.EnqueueFrame(UpdateFrame(MpReachAttribute(MpOptionalNonTransitive,
            ReachValue(GlobalNextHop, 32, 0x20, 0x01, 0x0D, 0xB8))));
        await SettleAsync(conn);
        Assert.Equal(0, routeTable.Count);

        // ...while the classic IPv4 half of the pipeline keeps processing normally.
        conn.EnqueueFrame(AnnounceFrame(24, 0xC0, 0x00, 0x02));
        await SettleAsync(conn, () => routeTable.Count == 1);
        Assert.Equal(1, routeTable.Count);

        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task UnreadableMpTuple_ResetsSession_CannotBeScopedToAFamily()
    {
        // #472 review: a value too short to even name its AFI/SAFI cannot be scoped to a
        // family, so the RFC 7606 §3(j) fallback is the session reset (NOTIFICATION 3/1).
        var routeTable = new RouteTable();
        var (session, run, conn) = await EstablishAsync(routeTable);

        conn.EnqueueFrame(UpdateFrame(MpReachAttribute(MpOptionalNonTransitive, [0x00, 0x02])));

        await AssertResetAsync(session, conn, expectedSubError: BgpConstants.SubError.MalformedAttributeList);
        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task NonGlobalMpReachNextHop_RoutesExcluded_SessionStaysUp()
    {
        var routeTable = new RouteTable();
        var (session, run, conn) = await EstablishAsync(routeTable);

        // RFC 2545 §3: the next hop must be a GLOBAL IPv6 address. :: is not. The attribute's
        // routes are excluded route-level (like the AS-loop rule); the session stays up.
        conn.EnqueueFrame(UpdateFrame(
            OriginAsPathAttributes,
            MpReachAttribute(MpOptionalNonTransitive, ReachValue(0, 32, 0x20, 0x01, 0x0D, 0xB8))));
        await SettleAsync(conn);

        Assert.Equal(0, routeTable.Count);
        Assert.True(session.IsEstablished, "a route-level exclusion is not a protocol error");

        await TeardownAsync(session, run);
    }

    // ---- frame builders ----

    private static byte[] Frame(BgpMessageType type, params byte[] payload)
    {
        var frame = new byte[BgpConstants.MessageHeaderSize + payload.Length];
        BgpConstants.Marker.CopyTo(frame.AsSpan(0, BgpConstants.MarkerSize));
        frame[16] = (byte)(frame.Length >> 8);
        frame[17] = (byte)frame.Length;
        frame[18] = (byte)type;
        payload.CopyTo(frame, BgpConstants.MessageHeaderSize);
        return frame;
    }

    private static byte[] MpReachAttribute(byte flags, byte[] value)
    {
        var attr = new byte[3 + value.Length];
        attr[0] = flags;
        attr[1] = MpReachCodec.MpReachNlriType;
        attr[2] = (byte)value.Length;
        value.CopyTo(attr, 3);
        return attr;
    }

    private static byte[] MpUnreachAttribute(byte flags, byte[] value)
    {
        var attr = new byte[3 + value.Length];
        attr[0] = flags;
        attr[1] = MpReachCodec.MpUnreachNlriType;
        attr[2] = (byte)value.Length;
        value.CopyTo(attr, 3);
        return attr;
    }

    /// <summary>
    /// MP_REACH_NLRI (AFI=2/SAFI=1) value: AFI(2) + SAFI(1) + NH-len(16) + next hop + reserved
    /// + one NLRI (length byte + significant address bytes).
    /// </summary>
    private static byte[] ReachValue(UInt128 nextHop, byte prefixLength, params byte[] addressBytes)
    {
        var value = new List<byte> { 0x00, 0x02, 0x01, 0x10 };
        for (var i = 0; i < 16; i++)
            value.Add((byte)(nextHop >> (120 - i * 8)));
        value.Add(0x00); // reserved
        value.Add(prefixLength);
        value.AddRange(addressBytes);
        return [.. value];
    }

    private static byte[] UpdateFrame(params byte[][] attributes)
    {
        var attrsLen = attributes.Sum(a => a.Length);
        var payload = new List<byte> { 0x00, 0x00, (byte)(attrsLen >> 8), (byte)attrsLen };
        foreach (var attr in attributes)
            payload.AddRange(attr);
        return Frame(BgpMessageType.Update, [.. payload]);
    }

    // ORIGIN + AS_PATH (no NEXT_HOP — the MP_REACH attribute supplies it, RFC 4760 §5).
    private static readonly byte[] OriginAsPathAttributes =
    [
        0x40, 0x01, 0x01, 0x00,                                 // ORIGIN igp
        0x40, 0x02, 0x06, 0x02, 0x01, 0x00, 0x00, 0x00, 0x64,   // AS_PATH seq [100]
    ];

    // ORIGIN + AS_PATH + NEXT_HOP for classic IPv4 NLRI announcements.
    private static readonly byte[] ClassicAttributes =
    [
        0x40, 0x01, 0x01, 0x00,                                 // ORIGIN igp
        0x40, 0x02, 0x06, 0x02, 0x01, 0x00, 0x00, 0x00, 0x64,   // AS_PATH seq [100]
        0x40, 0x03, 0x04, 0xC0, 0x00, 0x02, 0x01,               // NEXT_HOP 192.0.2.1
    ];

    private static byte[] AnnounceFrame(byte prefixLength, params byte[] addressBytes)
    {
        var payload = new List<byte>
        {
            0x00, 0x00,
            (byte)(ClassicAttributes.Length >> 8), (byte)ClassicAttributes.Length,
        };
        payload.AddRange(ClassicAttributes);
        payload.Add(prefixLength);
        payload.AddRange(addressBytes);
        return Frame(BgpMessageType.Update, [.. payload]);
    }

    // ---- session lifecycle (the #289 test pattern) ----

    private static async Task<(BgpSession Session, Task Run, ScriptedConnection Conn)> EstablishAsync(
        RouteTable routeTable, uint routerId = 0x0A000002, int? maxPrefixes = null)
    {
        var conn = new ScriptedConnection();
        var config = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0, MaxPrefixesPerPeer = maxPrefixes ?? 0 };
        var session = new BgpSession(
            conn,
            new PeerConfig { Address = "127.0.0.1" },
            config,
            routeTable,
            AllowAllFilter.Instance,
            new BgpMetrics(),
            NullLogger<BgpSession>.Instance);

        var run = session.RunAsync();
        conn.EnqueueMessage(new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = 0,
            RouterId = 0x0A000002,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)],
        });
        conn.EnqueueMessage(BgpKeepaliveMessage.Instance);

        for (var i = 0; i < 200 && !session.IsEstablished; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        Assert.True(session.IsEstablished, "session must reach Established");
        return (session, run, conn);
    }

    private static async Task SettleAsync(ScriptedConnection conn, Func<bool>? until = null)
    {
        var reached = false;
        for (var i = 0; i < 200; i++)
        {
            if (conn.Drained)
            {
                reached = until?.Invoke() ?? true;
                if (reached && (until is not null || i > 5)) break;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        Assert.True(conn.Drained, "the scripted frame was never consumed by the read loop");
        if (until is not null)
            Assert.True(reached, "the expected state was not reached in time");
    }

    /// <summary>
    /// Waits for the session reset driven by the scripted frame, then asserts exactly one
    /// NOTIFICATION with the expected error/sub-error pair was emitted before Idle.
    /// </summary>
    private static async Task AssertResetAsync(BgpSession session, ScriptedConnection conn, byte expectedSubError)
    {
        for (var i = 0; i < 200 && session.IsEstablished; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        Assert.False(session.IsEstablished, "the session must tear down");

        var notifications = await SentNotificationsAsync(conn);
        var notification = Assert.Single(notifications);
        Assert.Equal(BgpConstants.Error.UpdateMessageError, notification.ErrorCode);
        Assert.Equal(expectedSubError, notification.SubErrorCode);
    }

    private static async Task<IEnumerable<BgpNotificationMessage>> SentNotificationsAsync(ScriptedConnection conn)
    {
        // Give the teardown NOTIFICATION's send a moment to land in the script before reading.
        for (var i = 0; i < 100; i++)
        {
            if (conn.Sent.Select(f => BgpMessageReader.ReadMessage(f)).OfType<BgpNotificationMessage>().Any())
                break;
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
        return conn.Sent.Select(f => BgpMessageReader.ReadMessage(f)).OfType<BgpNotificationMessage>();
    }

    private static async Task TeardownAsync(BgpSession session, Task run)
    {
        session.MarkSilentClose();
        try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { /* expected */ }
        catch (IOException) { /* expected */ }
        session.Dispose();
    }
}
