using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging;
using BGPLite.Contracts;

namespace BGPLite.Tests;

/// <summary>
/// Regression tests for #222 (malformed path attribute / NLRI tore down the session via
/// ArgumentOutOfRangeException that escaped the read loop) and #223 (BgpParseException was
/// mapped to MessageHeaderError regardless of which body — OPEN/UPDATE — failed to parse).
/// These tests build malformed message frames at the byte level (so they hit the codec, not
/// the post-parse attribute validators already covered by #94) and assert the session stays
/// Established and the NOTIFICATION carries the RFC-correct error code.
/// </summary>
public class MalformedUpdateResilienceTests
{
    [Fact]
    public void ParseAttribute_TruncatedHeaderValue_ThrowsBgpParseException_NotAOORE()
    {
        // #222: a path attribute declaring more bytes than the buffer holds previously threw
        // ArgumentOutOfRangeException out of Span.Slice, escaping ReadLoopAsync and tearing down
        // the session. Now it must surface as BgpParseException so treat-as-withdraw applies.
        // One attribute header: flags=0, type=ORIGIN(1), length byte=5 — but only 1 data byte
        // follows, so the declared 5 bytes overshoot the buffer.
        var attrBytes = new byte[] { 0x00, 0x01, 0x05, 0xFF };

        var ex = Assert.Throws<BgpParseException>(() => BgpMessageReader.ReadMessage(BuildUpdateFrame([.. attrBytes])));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.AttributeLengthError, ex.SubErrorCode);
    }

    [Fact]
    public void ParseAttribute_ExtendedLengthFlag_TooFewHeaderBytes_ThrowsBgpParseException()
    {
        // #222: Extended-Length flag set (0x10) but only the 2 fixed header bytes present — the
        // 2-byte length read would index past the buffer. Must be BgpParseException, not AOORE.
        var attrBytes = new byte[] { 0x10, 0x02 };

        var ex = Assert.Throws<BgpParseException>(() => BgpMessageReader.ReadMessage(BuildUpdateFrame([.. attrBytes])));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.MalformedAttributeList, ex.SubErrorCode);
    }

    [Theory]
    [InlineData(0x08)]
    [InlineData(0x18)] // ExtendedLength | reserved
    public void ParseAttribute_ReservedFlagBitSet_RejectedWithAttributeFlagsError(byte flags)
    {
        // RFC 4271 §4.3: bit 0x08 is reserved and MUST be zero (#272, epic #6).
        var attrBytes = new byte[] { flags, 0x01, 0x01, 0x00 };

        var ex = Assert.Throws<BgpParseException>(() => BgpMessageReader.ReadMessage(BuildUpdateFrame([.. attrBytes])));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.AttributeFlagsError, ex.SubErrorCode);
    }

    [Fact]
    public void ParseUpdate_WithdrawnLengthExceedsPayload_ThrowsBgpParseException()
    {
        // #222: withdrawn-routes length field larger than the UPDATE payload — stream-level
        // corruption that previously threw AOORE out of the Slice in the withdrawn-routes loop.
        // Build the UPDATE payload directly (not via BuildUpdateFrame, which wraps bytes as attrs):
        // withdrawn_len=0xFFFF, attrs_len=0 — the declared 65535 withdrawn bytes overshoot the
        // 4-byte payload.
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(payload, 0xFFFF); // withdrawn length = 65535
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), 0); // attrs length = 0
        var frame = BuildMessage(BgpMessageType.Update, payload);

        var ex = Assert.Throws<BgpParseException>(() => BgpMessageReader.ReadMessage(frame));
        // RFC 4271 §6.3: an oversized Withdrawn Routes Length (or Total Attribute Length) MUST
        // carry subcode 1 (Malformed Attribute List) — #245 review finding.
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.MalformedAttributeList, ex.SubErrorCode);
    }

    [Theory]
    // withdrawn-routes section ends exactly at the payload end, so the 2 bytes the sender meant as
    // the Total Path Attribute Length were consumed as prefix data.
    [InlineData(new byte[] { 0x00, 0x02, 0x08, 0x0A }, BgpConstants.SubError.MalformedAttributeList)]
    // a /24 inside a 2-byte section: the declared 3 value bytes cross withdrawnEnd.
    [InlineData(new byte[] { 0x00, 0x02, 0x18, 0x0A, 0x00, 0x00 }, BgpConstants.SubError.InvalidNetworkField)]
    // a /32 inside a 2-byte section, with a well-formed attribute section behind it: previously
    // parsed as "withdraw 10.0.0.1/32" — a prefix the peer never sent — with no error at all.
    [InlineData(new byte[] { 0x00, 0x02, 0x20, 0x0A, 0x00, 0x00, 0x01, 0x00, 0x04, 0x40, 0x01, 0x01, 0x00 }, BgpConstants.SubError.InvalidNetworkField)]
    // a 1-byte withdrawn section leaves a single byte where the attribute-length field must be.
    [InlineData(new byte[] { 0x00, 0x01, 0x00, 0x00 }, BgpConstants.SubError.MalformedAttributeList)]
    public void ParseUpdate_WithdrawnSectionOverrun_ThrowsBgpParseException_NotAOORE(byte[] payload, byte expectedSubError)
    {
        // #284: the withdrawn-routes loop decoded against the payload end instead of the declared
        // end of its own section, and nothing bounds-checked the Total Path Attribute Length read.
        // Both escaped as ArgumentOutOfRangeException — not a BgpParseException, so ReadLoopAsync's
        // treat-as-withdraw filter never caught them and the session was torn down (cf. #222).
        var ex = Assert.Throws<BgpParseException>(
            () => BgpMessageReader.ReadMessage(BuildMessage(BgpMessageType.Update, payload)));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(expectedSubError, ex.SubErrorCode);
    }

    [Fact]
    public void ParseUpdate_WithdrawnSectionExactlyConsumed_StillParses()
    {
        // Guard the fix from over-rejecting: a well-formed withdrawn section that ends exactly on
        // its declared boundary must keep parsing (this is the shape the overrun cases mimic).
        var payload = new byte[] { 0x00, 0x02, 0x08, 0x0A, 0x00, 0x00 };

        var update = Assert.IsType<BgpUpdateMessage>(
            BgpMessageReader.ReadMessage(BuildMessage(BgpMessageType.Update, payload)));

        var prefix = Assert.Single(update.WithdrawnRoutes);
        Assert.Equal(0x0A000000u, prefix.Address);
        Assert.Equal(8, prefix.Length);
        Assert.Empty(update.PathAttributes);
        Assert.Empty(update.Nlri);
    }

    [Fact]
    public async Task WithdrawnSectionOverrun_OnWire_KeepsSessionEstablished()
    {
        // #284 end-to-end: the smallest frame that used to kill an Established session — a 23-byte
        // UPDATE, the RFC minimum. It must now take the treat-as-withdraw path like any other
        // malformed-body UPDATE (#222) and leave the session up.
        var (server, client) = ConnectedPair();
        using var clientSock = client;
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        var metrics = new BgpMetrics();
        using var session = new BgpSession(
            new SocketBgpConnection(server),
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            new RouteTable(),
            AllowAllFilter.Instance,
            metrics,
            new NopLogger<BgpSession>());

        var runTask = await EstablishSessionAsync(session, client, bgpConfig);

        // withdrawn_len=2, then a /8 prefix that consumes both remaining bytes — the attribute
        // length field is gone.
        var malformedFrame = BuildMessage(BgpMessageType.Update, [0x00, 0x02, 0x08, 0x0A]);
        Assert.Equal(BgpConstants.MinMessageSize + 4, malformedFrame.Length);
        client.Send(malformedFrame, 0, malformedFrame.Length, SocketFlags.None);

        for (var i = 0; i < 20 && metrics.UpdatesRejected == 0; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(50));

        Assert.True(session.IsEstablished, "session must survive a truncated withdrawn-routes section (#284)");
        Assert.True(metrics.UpdatesRejected >= 1, "the malformed UPDATE must be counted as rejected");

        var sent = await DrainAsync(client, TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(sent, m => m is BgpNotificationMessage);

        session.MarkSilentClose();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MalformedOpen_InEstablished_IsFsmError_SessionResets()
    {
        // #427 (RFC 4271 §8.2.2): an OPEN received in Established is an FSM error REGARDLESS of
        // body validity — Established accepts only UPDATE/KEEPALIVE/NOTIFICATION/ROUTE_REFRESH
        // and a conformant speaker never parses the body. Pre-fix the body-error filter applied
        // the UPDATE treatment (D17): the session stayed up with a warning and no NOTIFICATION.
        var (server, client) = ConnectedPair();
        using var clientSock = client;
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        var metrics = new BgpMetrics();
        using var session = new BgpSession(
            new SocketBgpConnection(server),
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            new RouteTable(),
            AllowAllFilter.Instance,
            metrics,
            new NopLogger<BgpSession>());

        var runTask = await EstablishSessionAsync(session, client, bgpConfig);

        // Well-framed OPEN, body-invalid: the 10-byte minimum payload declares optParamsLen=5,
        // which cannot fit — ParseOpen throws Open Message Error before the FSM switch saw the type.
        var payload = new byte[10];
        payload[0] = 4;                       // version
        payload[1] = 0xFD; payload[2] = 0xE8; // My AS 65002
        payload[9] = 5;                       // optional-parameters length: mismatches the 0 present
        var open = BuildMessage(BgpMessageType.Open, payload);
        client.Send(open, 0, open.Length, SocketFlags.None);

        var sent = await DrainAsync(client, TimeSpan.FromSeconds(2));
        var notification = Assert.Single(sent.OfType<BgpNotificationMessage>());
        Assert.Equal(BgpConstants.Error.FiniteStateMachineError, notification.ErrorCode);
        Assert.Equal(BgpConstants.SubError.Unspecific, notification.SubErrorCode);
        Assert.False(session.IsEstablished, "an OPEN in Established must reset the session (RFC 4271 §8.2.2)");

        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WellFormedOpen_InEstablished_IsFsmError_Too()
    {
        // Control for #427: both OPEN classes in Established — well-formed and malformed — take
        // the same FSM-error teardown; only the parse-failure path changed.
        var (server, client) = ConnectedPair();
        using var clientSock = client;
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        var metrics = new BgpMetrics();
        using var session = new BgpSession(
            new SocketBgpConnection(server),
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            new RouteTable(),
            AllowAllFilter.Instance,
            metrics,
            new NopLogger<BgpSession>());

        var runTask = await EstablishSessionAsync(session, client, bgpConfig);

        var open = new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = 0,
            RouterId = 0x0A000002,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)]
        };
        var buffer = new byte[BgpMessageWriter.GetBufferSize(open)];
        var written = BgpMessageWriter.WriteMessage(open, buffer);
        client.Send(buffer, 0, written, SocketFlags.None);

        var sent = await DrainAsync(client, TimeSpan.FromSeconds(2));
        Assert.Equal(BgpConstants.Error.FiniteStateMachineError,
            Assert.Single(sent.OfType<BgpNotificationMessage>()).ErrorCode);
        Assert.False(session.IsEstablished);

        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DuplicateNextHop_OnWire_InstallsTheFirstOccurrence()
    {
        // #287 / RFC 7606 §3: a duplicated attribute is not an error — all occurrences after the
        // first are discarded and the UPDATE is still processed. Before the fix the switch in
        // ParseRouteAttributes assigned unconditionally, so the SECOND next hop landed in the route
        // table while anything reading the first (collector, looking glass, packet capture) saw the
        // other one.
        var (server, client) = ConnectedPair();
        using var clientSock = client;
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        var metrics = new BgpMetrics();
        var routeTable = new RouteTable();
        using var session = new BgpSession(
            new SocketBgpConnection(server),
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            routeTable,
            AllowAllFilter.Instance,
            metrics,
            new NopLogger<BgpSession>());

        var runTask = await EstablishSessionAsync(session, client, bgpConfig);

        // EstablishSessionAsync negotiates the 4-octet-ASN capability, so AS_PATH is 4-octet encoded.
        var attrs = new List<byte>
        {
            0x40, 0x01, 0x01, 0x00,                                     // ORIGIN igp
            0x40, 0x02, 0x06, 0x02, 0x01, 0x00, 0x00, 0x00, 0x64,       // AS_PATH seq [100]
            0x40, 0x03, 0x04, 0xC0, 0x00, 0x02, 0x01,                   // NEXT_HOP 192.0.2.1  <- wins
            0x40, 0x03, 0x04, 0x0A, 0x0A, 0x0A, 0x0A,                   // NEXT_HOP 10.10.10.10 <- discarded
        };
        var payload = new List<byte> { 0x00, 0x00, (byte)(attrs.Count >> 8), (byte)attrs.Count };
        payload.AddRange(attrs);
        payload.AddRange([8, 0x0A]); // NLRI 10.0.0.0/8
        var frame = BuildMessage(BgpMessageType.Update, [.. payload]);
        client.Send(frame, 0, frame.Length, SocketFlags.None);

        for (var i = 0; i < 40 && routeTable.Count == 0; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(50));

        var route = routeTable.Get(0x0A000000, 8);
        Assert.NotNull(route);
        Assert.Equal(0xC0000201u, route!.NextHop); // 192.0.2.1 — the FIRST occurrence
        Assert.True(session.IsEstablished, "a duplicated attribute must not tear the session down");
        Assert.Equal(0, metrics.UpdatesRejected); // RFC 7606 §3: processed, not rejected

        session.MarkSilentClose();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TreatAsWithdraw_RemovesTheNlriFromTheRouteTable()
    {
        // #288 / RFC 7606 §2: "the UPDATE message containing the path attribute in question MUST be
        // treated as though all contained routes had been withdrawn ... thus causing them to be
        // removed from the Adj-RIB-In." Only the "treat" half was implemented: the UPDATE was
        // discarded and the session kept, but its NLRI stayed installed carrying the attributes of
        // the PREVIOUS announcement.
        var (server, client) = ConnectedPair();
        using var clientSock = client;
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        var metrics = new BgpMetrics();
        var routeTable = new RouteTable();
        using var session = new BgpSession(
            new SocketBgpConnection(server),
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            routeTable,
            AllowAllFilter.Instance,
            metrics,
            new NopLogger<BgpSession>());

        var runTask = await EstablishSessionAsync(session, client, bgpConfig);

        // 1) A well-formed announcement of 10.0.0.0/8 via 192.0.2.1. EstablishSessionAsync
        //    negotiates the 4-octet-ASN capability, so AS_PATH is 4-octet encoded.
        var good = new List<byte>
        {
            0x40, 0x01, 0x01, 0x00,                                 // ORIGIN igp
            0x40, 0x02, 0x06, 0x02, 0x01, 0x00, 0x00, 0x00, 0x64,   // AS_PATH seq [100]
            0x40, 0x03, 0x04, 0xC0, 0x00, 0x02, 0x01,               // NEXT_HOP 192.0.2.1
        };
        SendAll(client, AnnounceFrame(good));

        for (var i = 0; i < 40 && routeTable.Count == 0; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.Equal(0xC0000201u, routeTable.Get(0x0A000000, 8)?.NextHop);

        // 2) The same NLRI re-announced with NEXT_HOP omitted — a missing mandatory attribute, so
        //    the pipeline rejects it and treat-as-withdraw applies.
        var bad = new List<byte>
        {
            0x40, 0x01, 0x01, 0x00,
            0x40, 0x02, 0x06, 0x02, 0x01, 0x00, 0x00, 0x00, 0x64,
        };
        SendAll(client, AnnounceFrame(bad));

        for (var i = 0; i < 40 && metrics.UpdatesRejected == 0; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(50));

        Assert.Null(routeTable.Get(0x0A000000, 8));  // RFC 7606 §2 — must be gone, not stale
        Assert.True(session.IsEstablished, "treat-as-withdraw keeps the session up");
        Assert.Equal(1, metrics.UpdatesRejected);

        var sent = await DrainAsync(client, TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(sent, m => m is BgpNotificationMessage);

        session.MarkSilentClose();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UnknownWellKnownAttribute_TreatedAsWithdraw_KeepsSessionAlive()
    {
        // #322 / RFC 4271 §6.3: an UPDATE carrying an unrecognized WELL-KNOWN attribute (Optional
        // bit clear, unknown type code) must be rejected with subcode 2 and handled by
        // treat-as-withdraw: the UPDATE's NLRI leaves the table, the session survives, no
        // NOTIFICATION. Previously the attribute was silently ignored and the route installed.
        var (server, client) = ConnectedPair();
        using var clientSock = client;
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        var metrics = new BgpMetrics();
        var routeTable = new RouteTable();
        using var session = new BgpSession(
            new SocketBgpConnection(server),
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            routeTable,
            AllowAllFilter.Instance,
            metrics,
            new NopLogger<BgpSession>());

        var runTask = await EstablishSessionAsync(session, client, bgpConfig);

        // 1) A well-formed announcement of 10.0.0.0/8 via 192.0.2.1 (4-octet session → AS_PATH
        //    is 4-octet encoded).
        var good = new List<byte>
        {
            0x40, 0x01, 0x01, 0x00,                                 // ORIGIN igp
            0x40, 0x02, 0x06, 0x02, 0x01, 0x00, 0x00, 0x00, 0x64,   // AS_PATH seq [100]
            0x40, 0x03, 0x04, 0xC0, 0x00, 0x02, 0x01,               // NEXT_HOP 192.0.2.1
        };
        SendAll(client, AnnounceFrame(good));

        for (var i = 0; i < 40 && routeTable.Count == 0; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.NotNull(routeTable.Get(0x0A000000, 8));

        // 2) The same NLRI re-announced with an unrecognized well-known attribute (type 99,
        //    flags 0x40) attached — the UPDATE is rejected and treat-as-withdraw removes the prefix.
        var bad = new List<byte>
        {
            0x40, 0x01, 0x01, 0x00,
            0x40, 0x02, 0x06, 0x02, 0x01, 0x00, 0x00, 0x00, 0x64,
            0x40, 0x03, 0x04, 0xC0, 0x00, 0x02, 0x01,
            0x40, 0x63, 0x01, 0x00,                                 // type 99, well-known (Optional=0), 1 data byte
        };
        SendAll(client, AnnounceFrame(bad));

        for (var i = 0; i < 40 && metrics.UpdatesRejected == 0; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(50));

        Assert.Null(routeTable.Get(0x0A000000, 8));  // the re-announced NLRI was withdrawn, not installed
        Assert.True(session.IsEstablished, "an unrecognized well-known attribute must not reset the session");
        Assert.Equal(1, metrics.UpdatesRejected);

        var sent = await DrainAsync(client, TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(sent, m => m is BgpNotificationMessage);

        session.MarkSilentClose();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Writes the whole frame. <see cref="Socket.Send(byte[], SocketFlags)"/> may accept fewer bytes
    /// than requested, which would deliver a truncated UPDATE and fail the test somewhere other than
    /// the behaviour under test (#288 review). Loopback frames of this size never actually short-write,
    /// but a test that can fail for a reason unrelated to its subject is worth two lines to prevent —
    /// #302 is what that costs when it happens.
    /// </summary>
    private static void SendAll(Socket socket, byte[] frame)
    {
        var sent = 0;
        while (sent < frame.Length)
            sent += socket.Send(frame, sent, frame.Length - sent, SocketFlags.None);
    }

    /// <summary>Wraps path-attribute bytes into an UPDATE announcing 10.0.0.0/8.</summary>
    private static byte[] AnnounceFrame(List<byte> attributeBytes)
    {
        var payload = new List<byte> { 0x00, 0x00, (byte)(attributeBytes.Count >> 8), (byte)attributeBytes.Count };
        payload.AddRange(attributeBytes);
        payload.AddRange([8, 0x0A]); // NLRI 10.0.0.0/8
        return BuildMessage(BgpMessageType.Update, [.. payload]);
    }

    [Fact]
    public void ParseUpdate_AttributeValueCrossingAttrsEnd_Rejected()
    {
        // #245 review finding: an attribute TLV whose declared value length reaches past the
        // declared end of the attribute section must be rejected — previously the parser sliced
        // to the payload end, silently consuming NLRI bytes as attribute data.
        // Layout: withdrawn_len=0, attrs_len=4, attr TLV [flags=0, type=ORIGIN(1), len=5] with
        // only 1 byte left in the attrs section, followed by 8 NLRI bytes the old code would eat.
        var payload = new byte[4 + 4 + 8];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), 0); // withdrawn len
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), 4); // attrs len
        payload[4] = 0x00; // flags
        payload[5] = 0x01; // type = ORIGIN
        payload[6] = 0x05; // declared length 5 — crosses attrsEnd (only 1 byte left there)
        payload[7] = 0xFF;
        var frame = BuildMessage(BgpMessageType.Update, payload);

        var ex = Assert.Throws<BgpParseException>(() => BgpMessageReader.ReadMessage(frame));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.AttributeLengthError, ex.SubErrorCode);
    }

    [Fact]
    public void ParseUpdate_AttrsLengthExceedsPayload_ThrowsMalformedAttributeList()
    {
        // #235: a declared path-attributes length that runs past the payload is a malformed
        // attribute list (RFC 4271 §6.3 subcode 1), not Unspecific.
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(payload, 0); // withdrawn length = 0
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), 0xFFFF); // attrs length = 65535
        var frame = BuildMessage(BgpMessageType.Update, payload);

        var ex = Assert.Throws<BgpParseException>(() => BgpMessageReader.ReadMessage(frame));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.MalformedAttributeList, ex.SubErrorCode);
    }

    [Fact]
    public void ParseOpen_TooShort_PropagatesOpenMessageErrorCode()
    {
        // #223: an OPEN body too short to even read the fixed fields must report Open Message
        // Error (2), not MessageHeaderError (1).
        var payload = new byte[5]; // OPEN fixed part is 10 bytes
        var frame = BuildMessage(BgpMessageType.Open, payload);

        var ex = Assert.Throws<BgpParseException>(() => BgpMessageReader.ReadMessage(frame));
        Assert.Equal(BgpConstants.Error.OpenMessageError, ex.ErrorCode);
    }

    [Fact]
    public void ParseOpen_UnsupportedVersion_PropagatesOpenMessageErrorSubcode1()
    {
        // #223: version != 4 → Open Message Error, subcode 1 (Unsupported Version).
        var payload = new byte[10];
        payload[0] = 5; // version = 5
        var frame = BuildMessage(BgpMessageType.Open, payload);

        var ex = Assert.Throws<BgpParseException>(() => BgpMessageReader.ReadMessage(frame));
        Assert.Equal(BgpConstants.Error.OpenMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.UnsupportedVersion, ex.SubErrorCode);
    }

    [Fact]
    public async Task MalformedUpdate_OnWire_KeepsSessionEstablished()
    {
        // End-to-end regression for #222: a malformed UPDATE frame sent on a live socket must
        // NOT tear the session down. The read loop catches BgpParseException (treat-as-withdraw)
        // and continues; no NOTIFICATION is emitted.
        var (server, client) = ConnectedPair();
        using var clientSock = client;
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        var metrics = new BgpMetrics();
        using var session = new BgpSession(
            new SocketBgpConnection(server),
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            new RouteTable(),
            AllowAllFilter.Instance,
            metrics,
            new NopLogger<BgpSession>());

        var runTask = await EstablishSessionAsync(session, client, bgpConfig);

        // Build a truncated-attribute UPDATE frame by hand: valid header, valid withdrawn/attrs
        // length, but an attribute that declares 5 bytes of value and provides only 1.
        var malformedFrame = BuildUpdateFrame(0x00, 0x01, 0x05, 0xFF); // flags, ORIGIN, len=5, 1 byte
        client.Send(malformedFrame, 0, malformedFrame.Length, SocketFlags.None);

        // Give the read loop a bounded window to receive + reject the bad frame, with a fallback
        // re-check so the test is not purely timing-dependent.
        for (var i = 0; i < 20 && metrics.UpdatesRejected == 0; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(50));

        Assert.True(session.IsEstablished, "session must survive a malformed-attribute UPDATE (#222)");
        Assert.True(metrics.UpdatesRejected >= 1, "the malformed UPDATE must be counted as rejected");

        // No NOTIFICATION on the wire — we keep the session (RFC 7606: no notify on treat-as-withdraw).
        var sent = await DrainAsync(client, TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(sent, m => m is BgpNotificationMessage);

        session.MarkSilentClose();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Complement to <see cref="MalformedUpdate_OnWire_KeepsSessionEstablished"/>: a STREAM-LEVEL
    /// corruption (invalid marker/length/type in the fixed 19-byte header — BgpParseException with
    /// <c>ErrorCode == null</c>) MUST still tear the session down with NOTIFICATION(MessageHeaderError),
    /// per RFC 4271 §6.1. The treat-as-withdraw catch in ReadLoopAsync filters on
    /// <c>ErrorCode is not null</c>, so fixed-header failures propagate to RunAsync. This test guards
    /// against a regression where that filter is dropped (which would also desync the byte stream:
    /// the payload of the bad frame would be read as the next header).
    /// <para>
    /// Parameterized over HoldTime: <c>0</c> takes the direct <c>ReadLoopAsync</c> path in
    /// <c>RunEstablishedAsync</c>; <c>60</c> takes the <c>Task.WhenAny</c>/<c>AwaitLoopTaskAsync</c>
    /// path. The latter has its own propagation gap (AwaitLoopTaskAsync's generic catch used to swallow
    /// the BgpParseException and fall back to the finally-block Cease), so both paths are covered.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0)]   // holdTime=0: ReadLoopAsync awaited directly → propagates
    [InlineData(60)]  // holdTime>0: Task.WhenAny → AwaitLoopTaskAsync → must rethrow BgpParseException
    public async Task InvalidHeaderLength_OnWire_TearsDownSession_With_MessageHeaderError(int holdTime)
    {
        var (server, client) = ConnectedPair();
        using var clientSock = client;
        var bgpConfig = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = holdTime, KeepAlive = Math.Max(1, holdTime / 3) };
        using var session = new BgpSession(
            new SocketBgpConnection(server),
            new PeerConfig { Address = "127.0.0.1" },
            bgpConfig,
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            new NopLogger<BgpSession>());

        var runTask = await EstablishSessionAsync(session, client, bgpConfig);

        // A frame whose declared length is below the BGP minimum (19): valid marker + a 2-byte
        // length of 5 + a type byte. ReceiveMessageAsync throws BgpParseException (ErrorCode == null)
        // AFTER reading the 19-byte header but BEFORE reading any payload.
        var badHeader = new byte[BgpConstants.MessageHeaderSize];
        BgpConstants.Marker.CopyTo(badHeader.AsSpan(0, BgpConstants.MarkerSize));
        BinaryPrimitives.WriteUInt16BigEndian(badHeader.AsSpan(16, 2), 5); // length < MinMessageSize
        badHeader[18] = (byte)BgpMessageType.Update;
        client.Send(badHeader, 0, badHeader.Length, SocketFlags.None);

        // The session must tear down: RunAsync unwinds, finally transitions to Idle.
        for (var i = 0; i < 40 && session.IsEstablished; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(session.IsEstablished, "session must tear down on a fixed-header error (RFC 4271 §6.1)");

        // And a MessageHeaderError NOTIFICATION must be on the wire (not a generic Cease — that would
        // indicate AwaitLoopTaskAsync swallowed the BgpParseException and the finally-block fired).
        var sent = await DrainAsync(client, TimeSpan.FromSeconds(2));
        var notif = sent.OfType<BgpNotificationMessage>().SingleOrDefault();
        Assert.NotNull(notif);
        Assert.Equal(BgpConstants.Error.MessageHeaderError, notif!.ErrorCode);
        // #300: RFC 4271 §6.1 also mandates the subcode and the Data field — "the Error Subcode
        // MUST be set to Bad Message Length ... The Data field MUST contain the erroneous Length
        // field." Previously this emitted 1/0 with no Data, giving the peer's operator nothing.
        Assert.Equal(BgpConstants.SubError.BadMessageLength, notif.SubErrorCode);
        Assert.NotNull(notif.Data);
        Assert.Equal([0x00, 0x05], notif.Data!); // the declared length that was rejected

        try { await runTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { /* session torn down */ }
        catch (IOException) { /* client socket closed by teardown */ }
    }

    // ---- helpers ----

    private static byte[] BuildUpdateFrame(params byte[] attributeBytes)
    {
        // withdrawn_len=0, attrs_len=len(attr bytes), the attr bytes, no NLRI.
        var payload = new byte[4 + attributeBytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), 0); // withdrawn len
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), (ushort)attributeBytes.Length); // attrs len
        Array.Copy(attributeBytes, 0, payload, 4, attributeBytes.Length);
        return BuildMessage(BgpMessageType.Update, payload);
    }

    private static byte[] BuildMessage(BgpMessageType type, byte[] payload)
    {
        var frame = new byte[BgpConstants.MessageHeaderSize + payload.Length];
        BgpConstants.Marker.CopyTo(frame.AsSpan(0, BgpConstants.MarkerSize));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), (ushort)frame.Length);
        frame[18] = (byte)type;
        Array.Copy(payload, 0, frame, BgpConstants.MessageHeaderSize, payload.Length);
        return frame;
    }

    private static (Socket server, Socket client) ConnectedPair()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(listener.LocalEndPoint!);
        return (listener.Accept(), client);
    }

    private static void ReadExact(Socket s, byte[] buf, int offset, int count)
    {
        var got = 0;
        while (got < count)
        {
            var n = s.Receive(buf, offset + got, count - got, SocketFlags.None);
            if (n == 0) throw new IOException("socket closed");
            got += n;
        }
    }

    private static async Task<Task> EstablishSessionAsync(BgpSession session, Socket client, BgpConfig bgpConfig)
    {
        var runTask = session.RunAsync();
        // Send OPEN + KEEPALIVE; drain the server's OPEN/KEEPALIVE so the session reaches Established.
        var open = new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = (ushort)bgpConfig.HoldTime,
            RouterId = 0x0A000002,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)]
        };
        Send(client, open);
        var keepalive = new BgpKeepaliveMessage();
        Send(client, keepalive);
        // Drain server-side OPEN + KEEPALIVE (the handshake response) so the session advances.
        await DrainAsync(client, TimeSpan.FromSeconds(5));
        // Wait until Established (initial route dump is async on holdTime=0 → ReadLoopAsync).
        for (var i = 0; i < 50 && !session.IsEstablished; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        Assert.True(session.IsEstablished, "session must reach Established before injecting the malformed frame");
        return runTask;
    }

    private static void Send(Socket s, BgpMessage msg)
    {
        var buf = new byte[BgpMessageWriter.GetBufferSize(msg)];
        var n = BgpMessageWriter.WriteMessage(msg, buf);
        s.Send(buf, 0, n, SocketFlags.None);
    }

    private static async Task<List<BgpMessage>> DrainAsync(Socket client, TimeSpan timeout)
    {
        var sent = new List<BgpMessage>();
        client.ReceiveTimeout = (int)timeout.TotalMilliseconds;
        var buf = new byte[4096];
        var deadline = DateTime.UtcNow + timeout;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                if (client.Poll(100_000, SelectMode.SelectRead)) // 100ms
                {
                    var available = client.Available;
                    if (available == 0) break; // peer closed
                    var got = client.Receive(buf, 0, Math.Min(available, buf.Length), SocketFlags.None);
                    if (got == 0) break;
                    ParseAll(buf, got, sent);
                }
            }
        }
        catch (SocketException) { /* timeout / peer closed */ }
        return sent;
    }

    private static void ParseAll(byte[] buffer, int length, List<BgpMessage> into)
    {
        var offset = 0;
        while (offset + BgpConstants.MessageHeaderSize <= length)
        {
            var msgLen = BgpMessageReader.GetMessageLength(buffer.AsSpan(offset, length - offset));
            if (msgLen <= 0 || offset + msgLen > length) break;
            try { into.Add(BgpMessageReader.ReadMessage(buffer.AsSpan(offset, msgLen))); }
            catch (BgpParseException) { /* ignore unparseable outbound frame */ }
            offset += msgLen;
        }
    }

    private sealed class NopLogger<T> : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NopDisposable.Instance;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
        private sealed class NopDisposable : IDisposable
        {
            public static readonly NopDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
