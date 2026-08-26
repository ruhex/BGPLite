using System.Buffers.Binary;
using System.Net;
using System.Linq;
using BGPLite.Protocol;

namespace BGPLite.Tests;

public class BgpMessageTests
{
    [Fact]
    public void Keepalive_WriteThenRead_Roundtrip()
    {
        var buffer = new byte[64];
        var written = BgpMessageWriter.WriteMessage(BgpKeepaliveMessage.Instance, buffer);

        Assert.Equal(BgpConstants.MessageHeaderSize, written);

        var message = BgpMessageReader.ReadMessage(buffer.AsSpan(0, written));
        Assert.IsType<BgpKeepaliveMessage>(message);
    }

    [Fact]
    public void Open_WriteThenRead_Roundtrip()
    {
        var open = new BgpOpenMessage
        {
            Version = 4,
            Asn = 65444,
            HoldTime = 180,
            RouterId = 0x334B4214,
            Capabilities =
            [
                BgpCapabilityInfo.FourOctetAsn(65444),
                BgpCapabilityInfo.RouteRefresh(),
                BgpCapabilityInfo.MultiprotocolIpv4Unicast()
            ]
        };

        var buffer = new byte[512];
        var written = BgpMessageWriter.WriteMessage(open, buffer);

        var message = BgpMessageReader.ReadMessage(buffer.AsSpan(0, written));
        var readOpen = Assert.IsType<BgpOpenMessage>(message);

        Assert.Equal((byte)4, readOpen.Version);
        Assert.Equal((ushort)65444, readOpen.Asn);
        Assert.Equal((ushort)180, readOpen.HoldTime);
        Assert.Equal((uint)0x334B4214, readOpen.RouterId);
        Assert.Equal(3, readOpen.Capabilities.Count);
    }

    [Fact]
    public void Open_FourOctetAsn_CapabilityRoundtrip()
    {
        var asn = 200000u;
        var open = new BgpOpenMessage
        {
            Asn = 23456, // AS_TRANS
            HoldTime = 60,
            RouterId = 0x01020304,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(asn)]
        };

        var buffer = new byte[256];
        var written = BgpMessageWriter.WriteMessage(open, buffer);
        var readOpen = Assert.IsType<BgpOpenMessage>(BgpMessageReader.ReadMessage(buffer.AsSpan(0, written)));

        var effectiveAsn = CapabilityHelper.GetEffectiveAsn(readOpen);
        Assert.Equal(asn, effectiveAsn);
    }

    // Builds a raw OPEN message: marker + length + type + fixed 10-byte body + optional parameters.
    // Unlike BgpMessageWriter, this lets a test declare an optParamsLen that disagrees with the
    // bytes actually present — the malformed framing the #234 truncation cases need.
    private static byte[] BuildRawOpen(byte optParamsLen, byte[] optParams)
    {
        var length = BgpConstants.MessageHeaderSize + 10 + optParams.Length;
        var message = new byte[length];
        BgpConstants.Marker.CopyTo(message);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(16), (ushort)length);
        message[18] = (byte)BgpMessageType.Open;
        message[19] = 4; // version
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(20), (ushort)65000); // ASN
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(22), (ushort)180);   // hold time
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(24), 0x0A000001);    // router ID
        message[28] = optParamsLen;
        optParams.AsSpan().CopyTo(message.AsSpan(29));
        return message;
    }

    [Fact]
    public void Open_WellFormedRawOptParams_Parses()
    {
        // Capability parameter (type 2) carrying a Four-Octet-ASN TLV (code 65, len 4, ASN 200000).
        // Validates the raw builder so the truncation tests below fail for the right reason.
        var message = BuildRawOpen(8, [0x02, 0x06, 0x41, 0x04, 0x00, 0x03, 0x0D, 0x40]);

        var open = Assert.IsType<BgpOpenMessage>(BgpMessageReader.ReadMessage(message));

        var cap = Assert.Single(open.Capabilities);
        Assert.Equal(BgpConstants.Capability.FourOctetAsn, cap.Code);
        Assert.Equal(200000u, CapabilityHelper.GetEffectiveAsn(open));
    }

    [Fact]
    public void Open_OptParamsLengthExceedsMessage_Rejected()
    {
        // Declared optParamsLen=10 but only 4 parameter bytes present — previously parsing was
        // silently skipped and all capabilities dropped (#234).
        var message = BuildRawOpen(10, [0x02, 0x02, 0x41, 0x00]);

        var ex = Assert.Throws<BgpParseException>(() => BgpMessageReader.ReadMessage(message));
        Assert.Equal(BgpConstants.Error.OpenMessageError, ex.ErrorCode);
    }

    [Fact]
    public void Open_TruncatedOptParamHeader_Rejected()
    {
        // A complete capability param followed by one stray byte — the trailing 1-byte TLV header
        // needs 2 bytes; previously the loop just broke out of it.
        var message = BuildRawOpen(5, [0x02, 0x02, 0x41, 0x00, 0x02]);

        var ex = Assert.Throws<BgpParseException>(() => BgpMessageReader.ReadMessage(message));
        Assert.Equal(BgpConstants.Error.OpenMessageError, ex.ErrorCode);
    }

    [Fact]
    public void Open_TruncatedOptParamValue_Rejected()
    {
        // Parameter type 2 declares 4 bytes of capability data but only 2 follow.
        var message = BuildRawOpen(4, [0x02, 0x04, 0x41, 0x04]);

        var ex = Assert.Throws<BgpParseException>(() => BgpMessageReader.ReadMessage(message));
        Assert.Equal(BgpConstants.Error.OpenMessageError, ex.ErrorCode);
    }

    [Fact]
    public void Open_TruncatedCapabilityHeader_Rejected()
    {
        // Capability parameter whose payload is a single stray byte (a TLV header needs 2).
        var message = BuildRawOpen(3, [0x02, 0x01, 0x41]);

        var ex = Assert.Throws<BgpParseException>(() => BgpMessageReader.ReadMessage(message));
        Assert.Equal(BgpConstants.Error.OpenMessageError, ex.ErrorCode);
    }

    [Fact]
    public void Open_TruncatedFourOctetAsnCapability_Rejected()
    {
        // The #234 headline case: a Four-Octet-ASN TLV declaring 4 data bytes with only 2 present.
        // Previously the capability was silently dropped and the session downgraded to a 2-byte AS.
        var message = BuildRawOpen(6, [0x02, 0x04, 0x41, 0x04, 0x00, 0x00]);

        var ex = Assert.Throws<BgpParseException>(() => BgpMessageReader.ReadMessage(message));
        Assert.Equal(BgpConstants.Error.OpenMessageError, ex.ErrorCode);
    }

    [Fact]
    public void Open_SurplusBytesAfterOptParams_Rejected()
    {
        // optParamsLen declares 0 but one surplus byte follows — the declared length must match
        // the message exactly (RFC 4271 §4.2, CodeRabbit review on #244).
        var message = BuildRawOpen(0, [0x00]);

        var ex = Assert.Throws<BgpParseException>(() => BgpMessageReader.ReadMessage(message));
        Assert.Equal(BgpConstants.Error.OpenMessageError, ex.ErrorCode);
    }

    [Fact]
    public void Notification_WriteThenRead_Roundtrip()
    {
        var notif = new BgpNotificationMessage
        {
            ErrorCode = 2,
            SubErrorCode = 2
        };

        var buffer = new byte[64];
        var written = BgpMessageWriter.WriteMessage(notif, buffer);
        var readNotif = Assert.IsType<BgpNotificationMessage>(BgpMessageReader.ReadMessage(buffer.AsSpan(0, written)));

        Assert.Equal((byte)2, readNotif.ErrorCode);
        Assert.Equal((byte)2, readNotif.SubErrorCode);
        Assert.Null(readNotif.Data);
    }

    [Fact]
    public void Notification_WithData_Roundtrip()
    {
        var notif = new BgpNotificationMessage
        {
            ErrorCode = 2,
            SubErrorCode = 1,
            Data = [4]
        };

        var buffer = new byte[64];
        var written = BgpMessageWriter.WriteMessage(notif, buffer);
        var readNotif = Assert.IsType<BgpNotificationMessage>(BgpMessageReader.ReadMessage(buffer.AsSpan(0, written)));

        Assert.Equal((byte)2, readNotif.ErrorCode);
        Assert.Equal((byte)1, readNotif.SubErrorCode);
        Assert.Single(readNotif.Data!);
        Assert.Equal((byte)4, readNotif.Data[0]);
    }

    [Fact]
    public void Update_Empty_Roundtrip()
    {
        var update = new BgpUpdateMessage();

        var buffer = new byte[64];
        var written = BgpMessageWriter.WriteMessage(update, buffer);
        var readUpdate = Assert.IsType<BgpUpdateMessage>(BgpMessageReader.ReadMessage(buffer.AsSpan(0, written)));

        Assert.Empty(readUpdate.WithdrawnRoutes);
        Assert.Empty(readUpdate.PathAttributes);
        Assert.Empty(readUpdate.Nlri);
    }

    [Fact]
    public void Update_WithRoutesAndAttributes_Roundtrip()
    {
        var update = new BgpUpdateMessage
        {
            PathAttributes =
            [
                AttributeHelper.WriteOrigin(BgpOrigin.Igp),
                AttributeHelper.WriteAsPath([65444u, 65001u], fourByteAsn: true),
                AttributeHelper.WriteNextHop(0xC0A80101),
                AttributeHelper.WriteCommunities([0x0000FF01])
            ],
            Nlri =
            [
                new IpPrefix(0xC0A80000, 24), // 192.168.0.0/24
                new IpPrefix(0x0A000000, 8)   // 10.0.0.0/8
            ]
        };

        var buffer = new byte[512];
        var written = BgpMessageWriter.WriteMessage(update, buffer);
        var readUpdate = Assert.IsType<BgpUpdateMessage>(BgpMessageReader.ReadMessage(buffer.AsSpan(0, written)));

        Assert.Equal(2, readUpdate.Nlri.Count);
        Assert.Equal(0xC0A80000u, readUpdate.Nlri[0].Address);
        Assert.Equal((byte)24, readUpdate.Nlri[0].Length);
        Assert.Equal(0x0A000000u, readUpdate.Nlri[1].Address);
        Assert.Equal((byte)8, readUpdate.Nlri[1].Length);

        Assert.Equal(4, readUpdate.PathAttributes.Count);

        // Verify AS_PATH with 4-byte ASN
        var asPathAttr = readUpdate.PathAttributes.First(a => a.TypeCode == BgpConstants.Attribute.AsPath);
        var ases = AttributeHelper.ReadAsPath(asPathAttr, fourByteAsn: true);
        Assert.Equal([65444u, 65001u], ases);

        // Verify NEXT_HOP
        var nextHopAttr = readUpdate.PathAttributes.First(a => a.TypeCode == BgpConstants.Attribute.NextHop);
        Assert.Equal(0xC0A80101u, AttributeHelper.ReadNextHop(nextHopAttr));

        // Verify COMMUNITY
        var commAttr = readUpdate.PathAttributes.First(a => a.TypeCode == BgpConstants.Attribute.Community);
        var communities = AttributeHelper.ReadCommunities(commAttr);
        Assert.Single(communities);
        Assert.Equal(0x0000FF01u, communities[0]);
    }

    [Fact]
    public void Update_WithWithdrawals_Roundtrip()
    {
        var update = new BgpUpdateMessage
        {
            WithdrawnRoutes = [new IpPrefix(0xC0A80000, 24)]
        };

        var buffer = new byte[256];
        var written = BgpMessageWriter.WriteMessage(update, buffer);
        var readUpdate = Assert.IsType<BgpUpdateMessage>(BgpMessageReader.ReadMessage(buffer.AsSpan(0, written)));

        Assert.Single(readUpdate.WithdrawnRoutes);
        Assert.Equal(0xC0A80000u, readUpdate.WithdrawnRoutes[0].Address);
        Assert.Equal((byte)24, readUpdate.WithdrawnRoutes[0].Length);
    }

    [Fact]
    public void Marker_IsAllOnes()
    {
        for (var i = 0; i < 16; i++)
            Assert.Equal(0xFF, BgpConstants.Marker[i]);
    }

    [Fact]
    public void IpAddressToUint_Ipv6_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            BgpConstants.IPAddressToUint(IPAddress.Parse("2001:db8::1")));
    }

    [Fact]
    public void IpAddressToUint_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BgpConstants.IPAddressToUint(null!));
    }

    [Fact]
    public void IpAddressToUint_ValidIpv4_ReturnsExpectedValue()
    {
        var result = BgpConstants.IPAddressToUint(IPAddress.Parse("192.168.1.1"));
        Assert.Equal(0xC0A80101u, result);
    }

    [Fact]
    public void GetBufferSize_MatchesWriteSize()
    {
        var open = new BgpOpenMessage
        {
            Asn = 100,
            HoldTime = 60,
            RouterId = 0x01020304,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(100)]
        };

        var expectedSize = BgpMessageWriter.GetBufferSize(open);
        var buffer = new byte[expectedSize];
        var actualSize = BgpMessageWriter.WriteMessage(open, buffer);

        Assert.Equal(expectedSize, actualSize);
    }

    [Fact]
    public void ReadMessage_InvalidMarker_Throws()
    {
        var buffer = new byte[19];
        // All zeros — invalid marker
        Assert.Throws<BgpParseException>(() => BgpMessageReader.ReadMessage(buffer));
    }

    [Fact]
    public void ReadMessage_TooShort_Throws()
    {
        var buffer = new byte[10];
        Assert.Throws<BgpParseException>(() => BgpMessageReader.ReadMessage(buffer));
    }

    [Fact]
    public void RouteRefresh_RoundTrip()
    {
        var msg = new BgpRouteRefreshMessage
        {
            Afi = BgpConstants.Afi.IPv4,
            Reserved = 0,
            Safi = BgpConstants.Safi.Unicast
        };

        var buffer = new byte[64];
        var written = BgpMessageWriter.WriteMessage(msg, buffer);
        var read = BgpMessageReader.ReadMessage(buffer.AsSpan(0, written));

        var rr = Assert.IsType<BgpRouteRefreshMessage>(read);
        Assert.Equal(BgpConstants.Afi.IPv4, rr.Afi);
        Assert.Equal((byte)0, rr.Reserved);
        Assert.Equal(BgpConstants.Safi.Unicast, rr.Safi);
    }

    [Fact]
    public void RouteRefresh_InvalidLength_Throws()
    {
        var buffer = new byte[64];
        BgpConstants.Marker.CopyTo(buffer);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[16..], (ushort)(BgpConstants.MessageHeaderSize + 3));
        buffer[18] = (byte)BgpMessageType.RouteRefresh;
        buffer[19] = 0;
        buffer[20] = 1;
        buffer[21] = 1;

        Assert.Throws<BgpParseException>(() => BgpMessageReader.ReadMessage(buffer.AsSpan(0, 22)));
    }

    [Fact]
    public void Open_SingleCapabilityExceedingByte_Throws()
    {
        // Regression: a capability whose data length > 255 also makes the total
        // optional-params exceed 255, so the optParams-length guard in WriteOpen
        // fires first. Writer must fail loud instead of silently truncating.
        var bigData = new byte[300];
        var open = new BgpOpenMessage
        {
            Version = 4,
            Asn = 65000,
            HoldTime = 90,
            RouterId = 0x01020304,
            Capabilities = [new BgpCapabilityInfo { Code = 0xFF, Data = bigData }]
        };
        var buffer = new byte[1024];

        Assert.Throws<ArgumentOutOfRangeException>(() => BgpMessageWriter.WriteMessage(open, buffer));
    }

    [Fact]
    public void Open_TotalCapabilityDataExceedingByte_Throws()
    {
        // Regression: total optional-params (capabilities block) must fit in a single
        // byte per RFC 4271 §4.2. 40 capabilities × (2-byte header + 5-byte data) = 280
        // bytes of capability TLVs + 2-byte optional-params type/length = 282 total,
        // exceeding 255; writer must fail loud instead of silently truncating.
        var caps = new List<BgpCapabilityInfo>();
        // 40 capabilities * (2 header + 5 data) = 280 bytes of capability TLVs.
        for (var i = 0; i < 40; i++)
            caps.Add(new BgpCapabilityInfo { Code = (byte)(0x10 + i), Data = new byte[5] });

        var open = new BgpOpenMessage
        {
            Version = 4,
            Asn = 65000,
            HoldTime = 90,
            RouterId = 0x01020304,
            Capabilities = caps
        };
        var buffer = new byte[1024];

        Assert.Throws<ArgumentOutOfRangeException>(() => BgpMessageWriter.WriteMessage(open, buffer));
    }

    [Fact]
    public void Open_AtByteBoundary_Succeeds()
    {
        // Sanity: exactly 255 bytes of optional-params must encode without throwing.
        // optParamsLen = 2 (type+length) + Σ(2 + cap.Data.Length).
        // 27 caps of 7 bytes data each -> 27 * 9 = 243.
        // Last cap of 8 bytes data -> 2 + 8 = 10. Total = 2 + 243 + 10 = 255.
        var caps = new List<BgpCapabilityInfo>();
        for (var i = 0; i < 27; i++)
            caps.Add(new BgpCapabilityInfo { Code = (byte)(0x20 + i), Data = new byte[7] });
        caps.Add(new BgpCapabilityInfo { Code = 0x3F, Data = new byte[8] });

        var open = new BgpOpenMessage
        {
            Version = 4,
            Asn = 65000,
            HoldTime = 90,
            RouterId = 0x01020304,
            Capabilities = caps
        };
        var size = BgpMessageWriter.GetBufferSize(open);
        var buffer = new byte[size];
        var written = BgpMessageWriter.WriteMessage(open, buffer);

        Assert.Equal(size, written);
        // Optional-params length field sits at offset 19 (header) + 9 (fixed open payload).
        Assert.Equal(255, buffer[28]);
    }

    #region RFC 6793 — AS4_PATH tests

    [Fact]
    public void As4Path_WriteThenRead_Roundtrip()
    {
        // RFC 6793 §6: AS4_PATH (type 17) carries 4-byte ASN sequence for 2-byte-only peers
        var as4Path = AttributeHelper.WriteAs4Path([200000u, 300000u]);

        Assert.Equal(BgpConstants.Attribute.As4Path, as4Path.TypeCode);
        // RFC 6793: AS4_PATH is optional transitive (FlagOptional | FlagTransitive)
        Assert.Equal(BgpConstants.Attribute.FlagOptional | BgpConstants.Attribute.FlagTransitive, as4Path.Flags);
        // 2 (segment header) + 2 * 4 (two 4-byte ASNs) = 10 bytes
        Assert.Equal(10, as4Path.Data.Length);

        var readAses = AttributeHelper.ReadAs4Path(as4Path);
        Assert.Equal([200000u, 300000u], readAses);
    }

    [Fact]
    public void As4Path_OverlongSegment_Throws()
    {
        var asns = Enumerable.Range(0, 256).Select(i => (uint)i).ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(() => AttributeHelper.WriteAs4Path(asns));
    }

    [Fact]
    public void AsPath_OverlongSegment_Throws()
    {
        var asns = Enumerable.Range(0, 256).Select(i => (uint)i).ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(() => AttributeHelper.WriteAsPath(asns, fourByteAsn: false));
    }

    [Fact]
    public void AsPath_TruncatedSegment_Throws()
    {
        var attr = new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagTransitive,
            TypeCode = BgpConstants.Attribute.AsPath,
            Data = new byte[] { BgpConstants.AsPath.AsSequence, 2, 0x00, 0x01 }
        };

        var ex = Assert.Throws<BgpParseException>(() => AttributeHelper.ReadAsPath(attr, fourByteAsn: false));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.MalformedAsPath, ex.SubErrorCode);
    }

    [Fact]
    public void AsPath_InvalidSegmentType_Throws()
    {
        var attr = new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagTransitive,
            TypeCode = BgpConstants.Attribute.AsPath,
            Data = new byte[] { 0x7F, 1, 0x00, 0x01 }
        };

        var ex = Assert.Throws<BgpParseException>(() => AttributeHelper.ReadAsPath(attr, fourByteAsn: false));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.MalformedAsPath, ex.SubErrorCode);
    }

    [Fact]
    public void As4Path_TruncatedSegment_Throws()
    {
        var attr = new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagOptional | BgpConstants.Attribute.FlagTransitive,
            TypeCode = BgpConstants.Attribute.As4Path,
            Data = new byte[] { BgpConstants.AsPath.AsSequence, 2, 0x00, 0x00, 0x00, 0x01 }
        };

        var ex = Assert.Throws<BgpParseException>(() => AttributeHelper.ReadAs4Path(attr));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.MalformedAsPath, ex.SubErrorCode);
    }

    [Fact]
    public void AsPath_EmptySegment_Throws()
    {
        // RFC 4271 §4.3: a path segment value contains "one or more AS numbers" — a zero-length
        // segment is malformed (#238).
        var attr = new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagTransitive,
            TypeCode = BgpConstants.Attribute.AsPath,
            Data = new byte[] { BgpConstants.AsPath.AsSequence, 0 }
        };

        var ex = Assert.Throws<BgpParseException>(() => AttributeHelper.ReadAsPath(attr, fourByteAsn: false));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.MalformedAsPath, ex.SubErrorCode);
    }

    [Fact]
    public void As4Path_WithAsTrans_Throws()
    {
        // AS_TRANS (23456) is the 2-octet placeholder for a non-mappable 4-octet AS — meaningless
        // inside the 4-octet-encoded AS4_PATH (#238 defensive check).
        var attr = new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagOptional | BgpConstants.Attribute.FlagTransitive,
            TypeCode = BgpConstants.Attribute.As4Path,
            Data = new byte[] { BgpConstants.AsPath.AsSequence, 1, 0x00, 0x00, 0x5B, 0xA0 } // AS 23456
        };

        var ex = Assert.Throws<BgpParseException>(() => AttributeHelper.ReadAs4Path(attr));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.MalformedAsPath, ex.SubErrorCode);
    }

    [Fact]
    public void AsPath_Empty_RoundtripsAsZeroLengthAttribute()
    {
        // RFC 4271 §4.3: an empty path is a ZERO-LENGTH attribute — a zero-length SEGMENT is
        // malformed. The writer must not emit what the reader rejects (#248 review).
        var attr = AttributeHelper.WriteAsPath([], fourByteAsn: true);

        Assert.Empty(attr.Data);
        Assert.Equal([], AttributeHelper.ReadAsPath(attr, fourByteAsn: true));
    }

    [Fact]
    public void As4Path_Write_WithAsTrans_Throws()
    {
        // Write-side symmetry with the reader's AS_TRANS rejection (#248 review).
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AttributeHelper.WriteAs4Path([BgpConstants.AsPath.AsTrans]));
    }

    [Fact]
    public void Update_Writer_SortsAttributesByTypeCode_OnWire()
    {
        // #272 / epic #6: RFC 4271 §5 — well-known attributes ordered by type code on the wire,
        // regardless of the order the caller supplied.
        var update = new BgpUpdateMessage
        {
            WithdrawnRoutes = [],
            PathAttributes =
            [
                AttributeHelper.WriteNextHop(0x0A000001),                  // type 3
                AttributeHelper.WriteOrigin(BgpOrigin.Igp),                // type 1
                AttributeHelper.WriteAsPath([65001u], fourByteAsn: false), // type 2
            ],
            Nlri = [new IpPrefix(0xC0A80000, 24)]
        };

        var buffer = new byte[512];
        var written = BgpMessageWriter.WriteMessage(update, buffer);
        var read = Assert.IsType<BgpUpdateMessage>(BgpMessageReader.ReadMessage(buffer.AsSpan(0, written)));

        var typeCodes = read.PathAttributes.Select(a => a.TypeCode).ToArray();
        Assert.Equal(
        [
            BgpConstants.Attribute.Origin,
            BgpConstants.Attribute.AsPath,
            BgpConstants.Attribute.NextHop
        ], typeCodes);
    }

    [Fact]
    public void Update_Writer_SortIsStable_ForEqualTypeCodes()
    {
        // #273 review: equal type codes must keep their caller-supplied relative order — the
        // sort must be stable (List.Sort is not; OrderBy is).
        var first = AttributeHelper.WriteCommunities([0x11111111u]);
        var second = AttributeHelper.WriteCommunities([0x22222222u]);
        var update = new BgpUpdateMessage
        {
            WithdrawnRoutes = [],
            PathAttributes = [second, AttributeHelper.WriteOrigin(BgpOrigin.Igp), first],
            Nlri = []
        };

        var buffer = new byte[512];
        var written = BgpMessageWriter.WriteMessage(update, buffer);
        var read = Assert.IsType<BgpUpdateMessage>(BgpMessageReader.ReadMessage(buffer.AsSpan(0, written)));

        // Stable: of the two COMMUNITY attributes (equal type code), `second` was supplied
        // before `first`, so it must remain first among the equals.
        Assert.Equal([0x22222222u], AttributeHelper.ReadCommunities(read.PathAttributes[1]));
        Assert.Equal([0x11111111u], AttributeHelper.ReadCommunities(read.PathAttributes[2]));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    public void Communities_NonMultipleOf4Length_Rejected(int length)
    {
        // RFC 1997 §3: every community is exactly 4 octets (#272) — mirrors the % 12 rule of
        // ReadLargeCommunities.
        var attr = new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagOptional | BgpConstants.Attribute.FlagTransitive,
            TypeCode = BgpConstants.Attribute.Community,
            Data = new byte[length]
        };

        var ex = Assert.Throws<BgpParseException>(() => AttributeHelper.ReadCommunities(attr));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.OptionalAttributeError, ex.SubErrorCode);
    }

    [Fact]
    public void AsPath_TrailingByteAfterSegment_Throws()
    {
        // #235: a complete 1-ASN segment followed by one stray byte — offset != data.Length.
        var attr = new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagTransitive,
            TypeCode = BgpConstants.Attribute.AsPath,
            Data = new byte[] { BgpConstants.AsPath.AsSequence, 1, 0x00, 0x01, 0xFF }
        };

        var ex = Assert.Throws<BgpParseException>(() => AttributeHelper.ReadAsPath(attr, fourByteAsn: false));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.MalformedAsPath, ex.SubErrorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Origin_ValidValues_Accepted(byte value)
    {
        // RFC 4271 §5.1.2: IGP (0), EGP (1), INCOMPLETE (2) are the only defined origins.
        var attr = new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagTransitive,
            TypeCode = BgpConstants.Attribute.Origin,
            Data = [value]
        };

        Assert.Equal((BgpOrigin)value, AttributeHelper.ReadOrigin(attr));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(255)]
    public void Origin_InvalidValues_Rejected(byte value)
    {
        // #233: ORIGIN outside {0,1,2} is a malformed UPDATE — Invalid ORIGIN Attribute (§6.3
        // subcode 6), surfaced through treat-as-withdraw instead of being silently accepted.
        var attr = new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagTransitive,
            TypeCode = BgpConstants.Attribute.Origin,
            Data = [value]
        };

        var ex = Assert.Throws<BgpParseException>(() => AttributeHelper.ReadOrigin(attr));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.InvalidOriginAttribute, ex.SubErrorCode);
    }

    [Fact]
    public void AsPath_2Byte_WithAsTrans_Roundtrip()
    {
        // 2-byte-only peer: AS_PATH with AS_TRANS (23456) for ASN > 65535
        var asPath = AttributeHelper.WriteAsPath([23456u], fourByteAsn: false);

        Assert.Equal(BgpConstants.Attribute.AsPath, asPath.TypeCode);
        // 2 (segment header) + 2 (one 2-byte ASN) = 4 bytes
        Assert.Equal(4, asPath.Data.Length);

        var readAses = AttributeHelper.ReadAsPath(asPath, fourByteAsn: false);
        Assert.Equal([23456u], readAses);
    }

    [Fact]
    public void AsPath_2Byte_RegularAsn_Roundtrip()
    {
        // 2-byte-only peer with regular 2-byte ASN
        var asPath = AttributeHelper.WriteAsPath([65001u], fourByteAsn: false);

        var readAses = AttributeHelper.ReadAsPath(asPath, fourByteAsn: false);
        Assert.Equal([65001u], readAses);
    }

    [Fact]
    public void Update_2BytePeer_WithAs4Path_Roundtrip()
    {
        // RFC 6793 §6: 2-byte-only peer receives 2-byte AS_PATH + AS4_PATH
        var update = new BgpUpdateMessage
        {
            PathAttributes =
            [
                AttributeHelper.WriteOrigin(BgpOrigin.Igp),
                AttributeHelper.WriteAsPath([23456u], fourByteAsn: false), // AS_TRANS
                AttributeHelper.WriteNextHop(0xC0A80101),
                AttributeHelper.WriteAs4Path([200000u]) // true 4-byte ASN
            ],
            Nlri = [new IpPrefix(0xC0A80000, 24)]
        };

        var buffer = new byte[512];
        var written = BgpMessageWriter.WriteMessage(update, buffer);
        var readUpdate = Assert.IsType<BgpUpdateMessage>(BgpMessageReader.ReadMessage(buffer.AsSpan(0, written)));

        // Verify AS_PATH contains AS_TRANS
        var asPathAttr = readUpdate.PathAttributes.First(a => a.TypeCode == BgpConstants.Attribute.AsPath);
        var asPathAses = AttributeHelper.ReadAsPath(asPathAttr, fourByteAsn: false);
        Assert.Equal([23456u], asPathAses);

        // Verify AS4_PATH contains true 4-byte ASN
        var as4PathAttr = readUpdate.PathAttributes.First(a => a.TypeCode == BgpConstants.Attribute.As4Path);
        var as4PathAses = AttributeHelper.ReadAs4Path(as4PathAttr);
        Assert.Single(as4PathAses);
        Assert.Equal(200000u, as4PathAses[0]);
    }

    [Fact]
    public void Update_4BytePeer_NoAs4Path()
    {
        // 4-byte peer: only AS_PATH in 4-byte form, no AS4_PATH
        var update = new BgpUpdateMessage
        {
            PathAttributes =
            [
                AttributeHelper.WriteOrigin(BgpOrigin.Igp),
                AttributeHelper.WriteAsPath([200000u], fourByteAsn: true),
                AttributeHelper.WriteNextHop(0xC0A80101)
            ],
            Nlri = [new IpPrefix(0xC0A80000, 24)]
        };

        var buffer = new byte[512];
        var written = BgpMessageWriter.WriteMessage(update, buffer);
        var readUpdate = Assert.IsType<BgpUpdateMessage>(BgpMessageReader.ReadMessage(buffer.AsSpan(0, written)));

        var asPathAttr = readUpdate.PathAttributes.First(a => a.TypeCode == BgpConstants.Attribute.AsPath);
        var asPathAses = AttributeHelper.ReadAsPath(asPathAttr, fourByteAsn: true);
        Assert.Equal([200000u], asPathAses);

        // AS4_PATH should not be present
        var as4PathAttr = readUpdate.PathAttributes.FirstOrDefault(a => a.TypeCode == BgpConstants.Attribute.As4Path);
        Assert.Null(as4PathAttr);
    }

    [Fact]
    public void AsPath_AsTrans_Constant_Is23456()
    {
        // RFC 6793: AS_TRANS = 23456
        Assert.Equal(23456u, BgpConstants.AsPath.AsTrans);
    }

    #endregion

    [Fact]
    public void Open_Overflow_DoesNotMutateBuffer()
    {
        // Regression: failed validation must not partially mutate the caller's
        // buffer. Fill the buffer with a sentinel pattern, trigger an overflow
        // (single cap with 300-byte data), and assert every byte is still the
        // sentinel afterwards.
        var bigData = new byte[300];
        var open = new BgpOpenMessage
        {
            Version = 4,
            Asn = 65000,
            HoldTime = 90,
            RouterId = 0x01020304,
            Capabilities = [new BgpCapabilityInfo { Code = 0xFF, Data = bigData }]
        };
        var buffer = new byte[1024];
        var sentinel = 0x5A;
        Array.Fill(buffer, (byte)sentinel);

        Assert.Throws<ArgumentOutOfRangeException>(() => BgpMessageWriter.WriteMessage(open, buffer));
        Assert.All(buffer, b => Assert.Equal(sentinel, b));
    }

    [Fact]
    public void Open_TotalOverflow_DoesNotMutateBuffer()
    {
        // Same regression for the total-overflow path (40 caps * 7 bytes = 280
        // bytes of capability TLVs, optParamsLen > 255).
        var caps = new List<BgpCapabilityInfo>();
        for (var i = 0; i < 40; i++)
            caps.Add(new BgpCapabilityInfo { Code = (byte)(0x10 + i), Data = new byte[5] });

        var open = new BgpOpenMessage
        {
            Version = 4,
            Asn = 65000,
            HoldTime = 90,
            RouterId = 0x01020304,
            Capabilities = caps
        };
        var buffer = new byte[1024];
        var sentinel = 0xA5;
        Array.Fill(buffer, (byte)sentinel);

        Assert.Throws<ArgumentOutOfRangeException>(() => BgpMessageWriter.WriteMessage(open, buffer));
        Assert.All(buffer, b => Assert.Equal(sentinel, b));
    }

    [Fact]
    public void WriteHeader_ExceedsMaxMessageSize_Throws()
    {
        // Writer and reader must agree on the message size envelope
        // (BgpConstants.MaxMessageSize = 4096). Build an UPDATE whose total
        // length exceeds the cap: 1019 withdrawn /24 routes = 4076 bytes of
        // NLRI, plus 2 (withdrawn-len) + 2 (path-attrs-len) = 4080 bytes of
        // payload, plus 19 (header) = 4099 bytes — three over the cap.
        var tooManyPrefixes = Enumerable.Range(0, 1019)
            .Select(_ => new IpPrefix(0xC0A80000, 24))
            .ToList();
        var update = new BgpUpdateMessage
        {
            WithdrawnRoutes = tooManyPrefixes
        };
        var buffer = new byte[8192];
        var sentinel = 0x77;
        Array.Fill(buffer, (byte)sentinel);

        // GetBufferSize does NOT cap; only WriteHeader enforces MaxMessageSize.
        // Assert the writer rejects it without mutating buffer.
        Assert.Throws<ArgumentOutOfRangeException>(() => BgpMessageWriter.WriteMessage(update, buffer));
        Assert.All(buffer, b => Assert.Equal(sentinel, b));
    }

    // ---- #291: Extended Length flag must match the length field actually written ----

    /// <summary>
    /// RFC 4271 §4.3 lets the Extended Length bit select the length-field width independently of the
    /// value length — it is required above 255 octets, not forbidden below. The writer emitted the
    /// caller's flags byte verbatim but derived the field width from <c>Data.Length &gt; 255</c>, so an
    /// attribute arriving with 0x10 already set and a short value produced a TLV whose flags declared
    /// a two-octet length followed by a one-octet one — a frame BGPLite's own reader rejects (#291).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(1000)]
    public void WriteAttribute_CallerSetExtendedLengthFlag_RoundTrips(int dataLength)
    {
        var attr = new PathAttribute
        {
            Flags = (byte)(BgpConstants.Attribute.FlagOptional
                         | BgpConstants.Attribute.FlagTransitive
                         | BgpConstants.Attribute.FlagExtendedLength),
            TypeCode = BgpConstants.Attribute.Community,
            Data = new byte[dataLength],
        };
        var update = new BgpUpdateMessage { PathAttributes = [attr] };

        var buffer = new byte[BgpMessageWriter.GetBufferSize(update)];
        var written = BgpMessageWriter.WriteMessage(update, buffer);

        // GetBufferSize must agree with what WriteMessage actually emitted.
        Assert.Equal(buffer.Length, written);

        var read = Assert.IsType<BgpUpdateMessage>(BgpMessageReader.ReadMessage(buffer.AsSpan(0, written)));
        var roundTripped = Assert.Single(read.PathAttributes);
        Assert.Equal(BgpConstants.Attribute.Community, roundTripped.TypeCode);
        Assert.Equal(dataLength, roundTripped.Data.Length);
        Assert.NotEqual(0, roundTripped.Flags & BgpConstants.Attribute.FlagExtendedLength);
    }

    /// <summary>
    /// The complementary direction: without the flag a short attribute still uses the one-octet
    /// field, and above 255 octets the writer sets the bit itself even when the caller left it clear.
    /// </summary>
    [Theory]
    [InlineData(4, false)]
    [InlineData(255, false)]
    [InlineData(256, true)]
    public void WriteAttribute_WithoutCallerFlag_PicksFieldWidthByLength(int dataLength, bool expectExtendedBit)

    // ---- #300: RFC 4271 §6.1 header validation + RFC 7607 AS 0 ----

    /// <summary>
    /// RFC 4271 §6.1: "if the Length field of a KEEPALIVE message is not equal to 19 ... then the
    /// Error Subcode MUST be set to Bad Message Length." Type 4 previously mapped straight to the
    /// singleton, so a padded KEEPALIVE was accepted with its trailing bytes silently ignored.
    /// </summary>
    [Theory]
    [InlineData(20)]
    [InlineData(23)]
    [InlineData(100)]
    public void ReadMessage_KeepaliveWithWrongLength_BadMessageLength(int totalLength)
    {
        var frame = new byte[totalLength];
        BgpConstants.Marker.CopyTo(frame.AsSpan(0, BgpConstants.MarkerSize));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), (ushort)totalLength);
        frame[18] = (byte)BgpMessageType.Keepalive;

        var ex = Assert.Throws<BgpParseException>(() => BgpMessageReader.ReadMessage(frame));
        Assert.Equal(BgpConstants.SubError.BadMessageLength, ex.SubErrorCode);
        // ErrorCode stays null: this is a fixed-header failure, which BgpSession.ReadLoopAsync must
        // propagate (tear down) rather than route to treat-as-withdraw.
        Assert.Null(ex.ErrorCode);
        Assert.Equal([(byte)(totalLength >> 8), (byte)totalLength], ex.NotificationData);
    }

    [Fact]
    public void ReadMessage_KeepaliveWithExactLength_StillParses()
    {
        var frame = new byte[BgpConstants.MessageHeaderSize];
        BgpConstants.Marker.CopyTo(frame.AsSpan(0, BgpConstants.MarkerSize));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), BgpConstants.MessageHeaderSize);
        frame[18] = (byte)BgpMessageType.Keepalive;

        Assert.IsType<BgpKeepaliveMessage>(BgpMessageReader.ReadMessage(frame));
    }

    /// <summary>
    /// RFC 4271 §6.1: an out-of-range Length carries Bad Message Length "and the Data field MUST
    /// contain the erroneous Length field".
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(18)]
    [InlineData(4097)]
    [InlineData(65535)]
    public void ReadMessage_LengthOutOfRange_BadMessageLengthWithData(int declaredLength)
    {
        var frame = new byte[BgpConstants.MessageHeaderSize];
        BgpConstants.Marker.CopyTo(frame.AsSpan(0, BgpConstants.MarkerSize));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), (ushort)declaredLength);
        frame[18] = (byte)BgpMessageType.Keepalive;

        var ex = Assert.Throws<BgpParseException>(() => BgpMessageReader.ReadMessage(frame));
        Assert.Null(ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.BadMessageLength, ex.SubErrorCode);
        Assert.Equal([(byte)(declaredLength >> 8), (byte)declaredLength], ex.NotificationData);
    }

    /// <summary>
    /// RFC 4271 §6.1: "the Error Subcode MUST be set to Bad Message Type. The Data field MUST
    /// contain the erroneous Message Type field."
    /// </summary>
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)6)]
    [InlineData((byte)255)]
    public void ReadMessage_UnknownMessageType_BadMessageTypeWithData(byte type)
    {
        var frame = new byte[BgpConstants.MessageHeaderSize];
        BgpConstants.Marker.CopyTo(frame.AsSpan(0, BgpConstants.MarkerSize));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), BgpConstants.MessageHeaderSize);
        frame[18] = type;

        var ex = Assert.Throws<BgpParseException>(() => BgpMessageReader.ReadMessage(frame));
        Assert.Null(ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.BadMessageType, ex.SubErrorCode);
        Assert.Equal([type], ex.NotificationData);
    }

    /// <summary>
    /// RFC 7607 §2: "An UPDATE message that contains the AS number of zero in the AS_PATH ... MUST
    /// be considered as malformed and be handled by the procedures specified in [RFC7606]" — i.e.
    /// treat-as-withdraw via Malformed AS_PATH (RFC 7606 §7.2). Covers AS4_PATH through the same
    /// shared segment reader.
    /// </summary>
    [Fact]
    public void ReadAsPath_ContainingAsZero_MalformedAsPath()
    {
        // AS_SEQUENCE of 2 four-octet ASNs: 100, then 0.
        var attr = new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagTransitive,
            TypeCode = BgpConstants.Attribute.AsPath,
            Data = [0x02, 0x02, 0x00, 0x00, 0x00, 0x64, 0x00, 0x00, 0x00, 0x00],
        };

        var ex = Assert.Throws<BgpParseException>(() => AttributeHelper.ReadAsPath(attr, fourByteAsn: true));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.MalformedAsPath, ex.SubErrorCode);
    }

    [Fact]
    public void ReadAs4Path_ContainingAsZero_MalformedAsPath()
    {
        var attr = new PathAttribute
        {
            Flags = (byte)(BgpConstants.Attribute.FlagOptional | BgpConstants.Attribute.FlagTransitive),
            TypeCode = BgpConstants.Attribute.Community,
            Data = new byte[dataLength],
        };
        var update = new BgpUpdateMessage { PathAttributes = [attr] };

        var buffer = new byte[BgpMessageWriter.GetBufferSize(update)];
        var written = BgpMessageWriter.WriteMessage(update, buffer);
        Assert.Equal(buffer.Length, written);

        var read = Assert.IsType<BgpUpdateMessage>(BgpMessageReader.ReadMessage(buffer.AsSpan(0, written)));
        var roundTripped = Assert.Single(read.PathAttributes);
        Assert.Equal(dataLength, roundTripped.Data.Length);
        Assert.Equal(expectExtendedBit, (roundTripped.Flags & BgpConstants.Attribute.FlagExtendedLength) != 0);
    }

    /// <summary>
    /// The exact frame from #291: flags 0xD0 with a 4-octet COMMUNITY. The reader used to read the
    /// length as 0x0400 = 1024 and throw Attribute Length Error on the writer's own output.
    /// </summary>
    [Fact]
    public void WriteAttribute_ExtendedFlagShortValue_EmitsTwoOctetLengthField()
    {
        var attr = new PathAttribute
        {
            Flags = 0xD0,
            TypeCode = BgpConstants.Attribute.Community,
            Data = [0x00, 0x00, 0x00, 0x01],
        };
        var update = new BgpUpdateMessage { PathAttributes = [attr] };

        var buffer = new byte[BgpMessageWriter.GetBufferSize(update)];
        var written = BgpMessageWriter.WriteMessage(update, buffer);

        // header(19) + withdrawn-len(2) + attrs-len(2) + flags(1) + type(1) + length(2) + value(4)
        Assert.Equal(31, written);
        var attrStart = BgpConstants.MessageHeaderSize + 4;
        Assert.Equal(0xD0, buffer[attrStart]);
        Assert.Equal(BgpConstants.Attribute.Community, buffer[attrStart + 1]);
        Assert.Equal(0x00, buffer[attrStart + 2]); // two-octet length, high byte
        Assert.Equal(0x04, buffer[attrStart + 3]); // two-octet length, low byte
    }

    /// <summary>
    /// RFC 4271 §4.3: flag bit 0x08 is reserved and MUST be zero. The reader rejects it (#272), so
    /// emitting it would make the writer produce a frame its own reader refuses — the same
    /// round-trip break this PR fixes for the Extended Length bit (#291 review).
    /// </summary>
    [Theory]
    [InlineData((byte)0x08)]                      // reserved alone
    [InlineData((byte)0xC8)]                      // optional | transitive | reserved
    [InlineData((byte)0xD8)]                      // ...also extended length
    public void WriteAttribute_ReservedFlagBitSet_Throws(byte flags)
    {
        var update = new BgpUpdateMessage
        {
            PathAttributes = [new PathAttribute { Flags = flags, TypeCode = BgpConstants.Attribute.Community, Data = [0, 0, 0, 1] }],
        };
        var buffer = new byte[BgpMessageWriter.GetBufferSize(update)];

        Assert.Throws<ArgumentOutOfRangeException>(() => BgpMessageWriter.WriteMessage(update, buffer));
    }

    [Fact]
    public void WriteAttribute_ReservedFlagBitSet_LeavesBufferUntouched()
    {
        // The guard runs before any byte is written, so a rejected attribute cannot leave a
        // half-serialized frame in the caller's span.
        var update = new BgpUpdateMessage
        {
            PathAttributes = [new PathAttribute { Flags = 0xC8, TypeCode = BgpConstants.Attribute.Community, Data = [0, 0, 0, 1] }],
        };
        var buffer = new byte[BgpMessageWriter.GetBufferSize(update)];
        var canary = new byte[buffer.Length];
        Array.Fill(canary, (byte)0xEE);
        canary.CopyTo(buffer, 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => BgpMessageWriter.WriteMessage(update, buffer));
        // The header is written by WriteUpdate before the attribute loop, so only assert that the
        // attribute region — everything past the fixed UPDATE header — is untouched.
        var attrRegion = BgpConstants.MessageHeaderSize + 4;
        Assert.Equal(canary.AsSpan(attrRegion).ToArray(), buffer.AsSpan(attrRegion).ToArray());
            TypeCode = BgpConstants.Attribute.As4Path,
            Data = [0x02, 0x01, 0x00, 0x00, 0x00, 0x00],
        };

        var ex = Assert.Throws<BgpParseException>(() => AttributeHelper.ReadAs4Path(attr));
        Assert.Equal(BgpConstants.SubError.MalformedAsPath, ex.SubErrorCode);
    }

    [Fact]
    public void ReadAsPath_TwoOctetAsZero_MalformedAsPath()
    {
        // The 2-octet encoding path must reject AS 0 too.
        var attr = new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagTransitive,
            TypeCode = BgpConstants.Attribute.AsPath,
            Data = [0x02, 0x02, 0x00, 0x64, 0x00, 0x00],
        };

        var ex = Assert.Throws<BgpParseException>(() => AttributeHelper.ReadAsPath(attr, fourByteAsn: false));
        Assert.Equal(BgpConstants.SubError.MalformedAsPath, ex.SubErrorCode);
    }

    [Fact]
    public void ReadAsPath_WithoutAsZero_StillParses()
    {
        var attr = AttributeHelper.WriteAsPath([65001u, 200000u], fourByteAsn: true);
        Assert.Equal([65001u, 200000u], AttributeHelper.ReadAsPath(attr, fourByteAsn: true));
    }
}
