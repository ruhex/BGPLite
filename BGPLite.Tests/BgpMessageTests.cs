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

        Assert.Throws<BgpParseException>(() => AttributeHelper.ReadAsPath(attr, fourByteAsn: false));
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

        Assert.Throws<BgpParseException>(() => AttributeHelper.ReadAsPath(attr, fourByteAsn: false));
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

        Assert.Throws<BgpParseException>(() => AttributeHelper.ReadAs4Path(attr));
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
}
