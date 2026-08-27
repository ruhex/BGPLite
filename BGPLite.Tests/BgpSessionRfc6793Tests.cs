using BGPLite.Protocol;
using BGPLite.Server;

namespace BGPLite.Tests;

public class BgpSessionRfc6793Tests
{
    [Fact]
    public void WriteAndRead_As4Path_Roundtrip()
    {
        var attr = AttributeHelper.WriteAs4Path([200000u, 300000u]);

        Assert.Equal(BgpConstants.Attribute.As4Path, attr.TypeCode);
        Assert.Equal([200000u, 300000u], AttributeHelper.ReadAs4Path(attr));
    }

    [Fact]
    public void MergeAsPathWithAs4Path_ReconstructsTrueSequence()
    {
        var merged = UpdateCodec.MergeAsPathWithAs4Path([65010u, BgpConstants.AsPath.AsTrans, 65001u], [200000u, 65001u]);

        Assert.Equal([65010u, 200000u, 65001u], merged);
    }

    [Fact]
    public void MergeAsPathWithAs4Path_UnresolvedAsTrans_Throws()
    {
        var ex = Assert.Throws<BgpNotificationException>(() =>
            UpdateCodec.MergeAsPathWithAs4Path([65010u, BgpConstants.AsPath.AsTrans, 65001u], [200000u]));

        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.MalformedAsPath, ex.SubErrorCode);
    }

    [Fact]
    public void MergeAsPathWithAs4Path_EmptyAs4Path_ReturnsAsPathUnchanged()
    {
        var merged = UpdateCodec.MergeAsPathWithAs4Path([65010u, 65001u], []);

        Assert.Equal([65010u, 65001u], merged);
    }

    [Fact]
    public void MergeAsPathWithAs4Path_EqualLengths_ReturnsAs4Path()
    {
        var merged = UpdateCodec.MergeAsPathWithAs4Path([BgpConstants.AsPath.AsTrans, 65001u], [200000u, 65001u]);

        Assert.Equal([200000u, 65001u], merged);
    }

    [Fact]
    public void MergeAsPathWithAs4Path_As4PathLongerThanAsPath_IgnoresAs4Path()
    {
        // RFC 6793 §4.2.3: "If the number of AS numbers in the AS_PATH attribute is less than
        // the number of AS numbers in the AS4_PATH attribute, then the AS4_PATH attribute SHALL
        // be ignored, and the AS_PATH attribute SHALL be taken as the AS path information."
        // Previously this threw and discarded the route (#245 review finding).
        var merged = UpdateCodec.MergeAsPathWithAs4Path([65001u], [200000u, 300000u]);

        Assert.Equal([65001u], merged);
    }

    [Fact]
    public void ParseRouteAttributes_HappyPath_ParsesAndMerges()
    {
        // #270: the inbound pipeline through the public protocol API — 2-byte session with
        // AS4_PATH reconstruction, communities and large communities.
        var update = new BgpUpdateMessage
        {
            PathAttributes =
            [
                AttributeHelper.WriteOrigin(BgpOrigin.Igp),
                AttributeHelper.WriteAsPath([65010u, BgpConstants.AsPath.AsTrans, 65001u], fourByteAsn: false),
                AttributeHelper.WriteAs4Path([200000u, 65001u]),
                AttributeHelper.WriteNextHop(0x0A000001),
                AttributeHelper.WriteCommunities([65000u, 100u]),
                AttributeHelper.WriteLargeCommunities([(64512u, 1u, 2u)]),
            ],
            Nlri = []
        };

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: false);

        Assert.Equal([65010u, 200000u, 65001u], attrs.AsPath); // AS4_PATH trailing reconstruction
        Assert.Equal(0x0A000001u, attrs.NextHop);
        Assert.Equal([65000u, 100u], attrs.Communities);
        Assert.Equal([(64512u, 1u, 2u)], attrs.LargeCommunities);
    }

    [Fact]
    public void ParseRouteAttributes_MissingMandatoryAttribute_ThrowsSubcode3()
    {
        // ORIGIN + AS_PATH but no NEXT_HOP.
        var update = new BgpUpdateMessage
        {
            PathAttributes =
            [
                AttributeHelper.WriteOrigin(BgpOrigin.Igp),
                AttributeHelper.WriteAsPath([65001u], fourByteAsn: true),
            ],
            Nlri = []
        };

        var ex = Assert.Throws<BgpNotificationException>(() => UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: true));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.MissingWellKnownAttribute, ex.SubErrorCode);
    }

    [Fact]
    public void ParseRouteAttributes_UnrecognizedWellKnownAttribute_ThrowsSubcode2()
    {
        // RFC 4271 §6.3 / #322: an Optional=0 attribute of an unknown type code must be rejected
        // (subcode 2) — routes must not install with unknown well-known semantics attached.
        var update = new BgpUpdateMessage
        {
            PathAttributes =
            [
                AttributeHelper.WriteOrigin(BgpOrigin.Igp),
                AttributeHelper.WriteAsPath([65001u], fourByteAsn: true),
                AttributeHelper.WriteNextHop(0x0A000001),
                new PathAttribute { Flags = BgpConstants.Attribute.FlagTransitive, TypeCode = 99, Data = [0x01] },
            ],
            Nlri = []
        };

        var ex = Assert.Throws<BgpNotificationException>(() => UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: true));

        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.UnrecognizedWellKnownAttribute, ex.SubErrorCode);
    }

    [Fact]
    public void ParseRouteAttributes_UnrecognizedOptionalAttribute_StillParses()
    {
        // RFC 4271 §6.3: only unrecognized WELL-KNOWN attributes are rejected; an unrecognized
        // optional attribute is ignored (BGPLite re-originates everything it advertises, so there
        // is nothing to propagate with the Partial bit).
        var update = new BgpUpdateMessage
        {
            PathAttributes =
            [
                AttributeHelper.WriteOrigin(BgpOrigin.Igp),
                AttributeHelper.WriteAsPath([65001u], fourByteAsn: true),
                AttributeHelper.WriteNextHop(0x0A000001),
                new PathAttribute { Flags = BgpConstants.Attribute.FlagOptional | BgpConstants.Attribute.FlagTransitive, TypeCode = 99, Data = [0x01] },
            ],
            Nlri = []
        };

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: true);

        Assert.Equal([65001u], attrs.AsPath);
    }

    [Fact]
    public void ParseRouteAttributes_KnownButUnreadWellKnownAttributes_StillAccepted()
    {
        // #290 guard for the #322 check: LOCAL_PREF (type 5) and ATOMIC_AGGREGATE (type 6) are
        // well-known attributes this codec never reads — they must not start being rejected.
        var update = new BgpUpdateMessage
        {
            PathAttributes =
            [
                AttributeHelper.WriteOrigin(BgpOrigin.Igp),
                AttributeHelper.WriteAsPath([65001u], fourByteAsn: true),
                AttributeHelper.WriteNextHop(0x0A000001),
                new PathAttribute { Flags = BgpConstants.Attribute.FlagTransitive, TypeCode = BgpConstants.Attribute.LocalPref, Data = [0x00, 0x00, 0x00, 0x64] },
                new PathAttribute { Flags = BgpConstants.Attribute.FlagTransitive, TypeCode = BgpConstants.Attribute.AtomicAggregate, Data = [] },
            ],
            Nlri = []
        };

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: true);

        Assert.Equal([65001u], attrs.AsPath);
    }

    [Fact]
    public void ParseRouteAttributes_InvalidOriginValue_ThrowsSubcode6()
    {
        var update = new BgpUpdateMessage
        {
            PathAttributes =
            [
                new PathAttribute { Flags = BgpConstants.Attribute.FlagTransitive, TypeCode = BgpConstants.Attribute.Origin, Data = [7] },
                AttributeHelper.WriteAsPath([65001u], fourByteAsn: true),
                AttributeHelper.WriteNextHop(0x0A000001),
            ],
            Nlri = []
        };

        var ex = Assert.Throws<BgpNotificationException>(() => UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: true));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.InvalidOriginAttribute, ex.SubErrorCode);
    }

    [Fact]
    public void ValidateAggregatorReconstruction_AsTransWithAs4Aggregator_DoesNotThrow()
    {
        UpdateCodec.ValidateAggregatorReconstruction(BgpConstants.AsPath.AsTrans, 200000u);
    }

    [Fact]
    public void ValidateAggregatorReconstruction_NonAsTransAggregator_DoesNotThrow()
    {
        UpdateCodec.ValidateAggregatorReconstruction(65001u, null);
    }

    [Fact]
    public void ValidateAggregatorReconstruction_NullAggregator_DoesNotThrow()
    {
        UpdateCodec.ValidateAggregatorReconstruction(null, null);
    }

    [Fact]
    public void ValidateAggregatorReconstruction_NullAggregatorWithAs4Aggregator_Throws()
    {
        var ex = Assert.Throws<BgpNotificationException>(() =>
            UpdateCodec.ValidateAggregatorReconstruction(null, 200000u));

        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.OptionalAttributeError, ex.SubErrorCode);
    }

    [Fact]
    public void ValidateAggregatorReconstruction_AsTransWithoutAs4Aggregator_Throws()
    {
        var ex = Assert.Throws<BgpNotificationException>(() =>
            UpdateCodec.ValidateAggregatorReconstruction(BgpConstants.AsPath.AsTrans, null));

        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.OptionalAttributeError, ex.SubErrorCode);
    }

    // --- #154: wire-format codec for AGGREGATOR (type 7) and AS4_AGGREGATOR (type 18) ---
    // The prior code had the lengths inverted (AGGREGATOR accepted 8 bytes on a 4-byte session,
    // AS4_AGGREGATOR expected 4) — every legal value was rejected. These tests pin the RFC 6793 §3
    // wire format so the regression cannot slip back.

    [Fact]
    public void ReadAggregatorAsn_TwoOctetSession_SixByteForm_ReadsTwoOctetAs()
    {
        // RFC 6793 §3: AGGREGATOR carries a two-octet AS (6 octets total) when an OLD
        // (2-octet-AS) speaker is involved.
        var data = new byte[] { 0xFD, 0xE9, 10, 0, 0, 1 }; // AS 65001, aggregator IP 10.0.0.1
        var attr = new PathAttribute { Flags = BgpConstants.Attribute.FlagTransitive, TypeCode = BgpConstants.Attribute.Aggregator, Data = data };

        Assert.Equal(65001u, AttributeHelper.ReadAggregatorAsn(attr, fourByteAsn: false));
    }

    [Fact]
    public void ReadAggregatorAsn_FourOctetSession_EightByteForm_ReadsFourOctetAs()
    {
        // RFC 6793 §3: "The same applies to the AGGREGATOR attribute -- the same attribute is
        // used between NEW BGP speakers, except that the AS number carried in the attribute is
        // encoded as a four-octet entity" — 8 octets total on a 4-octet-AS session. The #154
        // code required 6 bytes unconditionally, rejecting every legal peer AGGREGATOR (#245
        // review finding).
        var data = new byte[] { 0x00, 0x03, 0x0D, 0x40, 10, 0, 0, 1 }; // AS 200000, IP 10.0.0.1
        var attr = new PathAttribute { Flags = BgpConstants.Attribute.FlagTransitive, TypeCode = BgpConstants.Attribute.Aggregator, Data = data };

        Assert.Equal(200000u, AttributeHelper.ReadAggregatorAsn(attr, fourByteAsn: true));
    }

    [Theory]
    [InlineData(false, 4)]
    [InlineData(false, 8)] // the 4-octet form is AS4_AGGREGATOR's job on a 2-octet session
    [InlineData(true, 4)]
    [InlineData(true, 6)]  // the 2-octet form is wrong on a 4-octet-AS session
    public void ReadAggregatorAsn_WrongLengthForSession_Throws(bool fourByteAsn, int len)
    {
        var attr = new PathAttribute { TypeCode = BgpConstants.Attribute.Aggregator, Data = new byte[len] };

        var ex = Assert.Throws<BgpParseException>(() => AttributeHelper.ReadAggregatorAsn(attr, fourByteAsn));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.OptionalAttributeError, ex.SubErrorCode);
    }

    [Fact]
    public void ReadAs4AggregatorAsn_EightByteForm_ReadsFourOctetAs()
    {
        // RFC 6793 §3: AS4_AGGREGATOR is 8 octets (4 AS + 4 IPv4). The prior code expected exactly
        // 4 bytes (#31 regression), rejecting every well-formed AS4_AGGREGATOR.
        var data = new byte[] { 0x00, 0x03, 0x0D, 0x40, 10, 0, 0, 1 }; // AS 200000, IP 10.0.0.1
        var attr = new PathAttribute { Flags = BgpConstants.Attribute.FlagTransitive, TypeCode = BgpConstants.Attribute.As4Aggregator, Data = data };

        Assert.Equal(200000u, AttributeHelper.ReadAs4AggregatorAsn(attr));
    }

    [Theory]
    [InlineData(4)]  // the length the buggy code accepted
    [InlineData(6)]  // AGGREGATOR's length — wrong attribute
    [InlineData(12)]
    public void ReadAs4AggregatorAsn_WrongLength_Throws(int len)
    {
        var attr = new PathAttribute { TypeCode = BgpConstants.Attribute.As4Aggregator, Data = new byte[len] };
        Assert.Throws<BgpParseException>(() => AttributeHelper.ReadAs4AggregatorAsn(attr));
    }

    [Fact]
    public void ValidateMandatoryAttributes_MissingRequiredAttribute_Throws()
    {
        var ex = Assert.Throws<BgpNotificationException>(() =>
            UpdateCodec.ValidateMandatoryAttributes(false, true, true));

        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.MissingWellKnownAttribute, ex.SubErrorCode);
    }

    [Fact]
    public void ValidateMandatoryAttributes_MissingAsPath_Throws()
    {
        var ex = Assert.Throws<BgpNotificationException>(() =>
            UpdateCodec.ValidateMandatoryAttributes(true, false, true));

        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.MissingWellKnownAttribute, ex.SubErrorCode);
    }

    [Fact]
    public void ValidateMandatoryAttributes_MissingNextHop_Throws()
    {
        var ex = Assert.Throws<BgpNotificationException>(() =>
            UpdateCodec.ValidateMandatoryAttributes(true, true, false));

        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.MissingWellKnownAttribute, ex.SubErrorCode);
    }

    [Fact]
    public void GetMalformedFourOctetAsnCapabilityData_ReturnsMalformedCapabilityTlv()
    {
        var open = new BgpOpenMessage
        {
            Capabilities = [new BgpCapabilityInfo { Code = BgpConstants.Capability.FourOctetAsn, Data = [0x01, 0x02, 0x03] }]
        };

        Assert.Equal([BgpConstants.Capability.FourOctetAsn, 3, 0x01, 0x02, 0x03], UpdateCodec.GetMalformedFourOctetAsnCapabilityData(open));
    }

    [Fact]
    public void GetMalformedFourOctetAsnCapabilityData_EmptyData_ReturnsZeroLengthTlv()
    {
        var open = new BgpOpenMessage
        {
            Capabilities = [new BgpCapabilityInfo { Code = BgpConstants.Capability.FourOctetAsn, Data = [] }]
        };

        Assert.Equal([BgpConstants.Capability.FourOctetAsn, 0], UpdateCodec.GetMalformedFourOctetAsnCapabilityData(open));
    }

    [Fact]
    public void GetMalformedFourOctetAsnCapabilityData_MultipleCapabilities_ReturnsFirstMalformed()
    {
        var open = new BgpOpenMessage
        {
            Capabilities =
            [
                BgpCapabilityInfo.FourOctetAsn(65001),
                new BgpCapabilityInfo { Code = BgpConstants.Capability.FourOctetAsn, Data = [0x01, 0x02] }
            ]
        };

        Assert.Equal([BgpConstants.Capability.FourOctetAsn, 2, 0x01, 0x02], UpdateCodec.GetMalformedFourOctetAsnCapabilityData(open));
    }

    /// <summary>
    /// RFC 7606 §3, which revises RFC 4271 §6.3: "If any other attribute (whether recognized or
    /// unrecognized) appears more than once in an UPDATE message, then all the occurrences of the
    /// attribute other than the first one SHALL be discarded and the UPDATE message will continue
    /// to be processed." The switch in ParseRouteAttributes assigned unconditionally, so the LAST
    /// occurrence won (#287).
    /// </summary>
    [Fact]
    public void ParseRouteAttributes_DuplicateNextHop_FirstOccurrenceWins()
    {
        var update = new BgpUpdateMessage
        {
            PathAttributes =
            [
                AttributeHelper.WriteOrigin(BgpOrigin.Igp),
                AttributeHelper.WriteAsPath([65001u], fourByteAsn: true),
                AttributeHelper.WriteNextHop(0xC0000201), // 192.0.2.1 — the one that must win
                AttributeHelper.WriteNextHop(0x0A0A0A0A), // 10.10.10.10 — discarded
            ],
            Nlri = []
        };

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: true);

        Assert.Equal(0xC0000201u, attrs.NextHop);
    }

    [Fact]
    public void ParseRouteAttributes_DuplicateAsPath_FirstOccurrenceWins()
    {
        var update = new BgpUpdateMessage
        {
            PathAttributes =
            [
                AttributeHelper.WriteOrigin(BgpOrigin.Igp),
                AttributeHelper.WriteAsPath([65001u], fourByteAsn: true),
                AttributeHelper.WriteAsPath([64512u, 65002u], fourByteAsn: true), // discarded
                AttributeHelper.WriteNextHop(0x0A000001),
            ],
            Nlri = []
        };

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: true);

        Assert.Equal([65001u], attrs.AsPath);
    }

    [Fact]
    public void ParseRouteAttributes_DuplicateCommunity_FirstOccurrenceWins()
    {
        var update = new BgpUpdateMessage
        {
            PathAttributes =
            [
                AttributeHelper.WriteOrigin(BgpOrigin.Igp),
                AttributeHelper.WriteAsPath([65001u], fourByteAsn: true),
                AttributeHelper.WriteNextHop(0x0A000001),
                AttributeHelper.WriteCommunities([65000u, 100u]),
                AttributeHelper.WriteCommunities([65000u, 999u]), // discarded
            ],
            Nlri = []
        };

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: true);

        Assert.Equal([65000u, 100u], attrs.Communities);
    }

    /// <summary>
    /// A discarded duplicate must not be validated either — RFC 7606 §3 says the later occurrences
    /// are discarded, not "discarded but still checked". A valid first ORIGIN followed by an invalid
    /// second one is accepted; before #287 the second was read and threw Invalid ORIGIN (subcode 6).
    /// </summary>
    [Fact]
    public void ParseRouteAttributes_DuplicateOrigin_SecondIsNotValidated()
    {
        var update = new BgpUpdateMessage
        {
            PathAttributes =
            [
                AttributeHelper.WriteOrigin(BgpOrigin.Igp),
                new PathAttribute
                {
                    Flags = BgpConstants.Attribute.FlagTransitive,
                    TypeCode = BgpConstants.Attribute.Origin,
                    Data = [7], // out of range (RFC 4271 §5.1.2 defines 0/1/2) — must be discarded
                },
                AttributeHelper.WriteAsPath([65001u], fourByteAsn: true),
                AttributeHelper.WriteNextHop(0x0A000001),
            ],
            Nlri = []
        };

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: true);

        Assert.Equal(0x0A000001u, attrs.NextHop);
    }

    /// <summary>
    /// The inverse guard: an invalid FIRST occurrence is still rejected. The duplicate rule must not
    /// become a way to smuggle a malformed attribute past validation by prefixing a valid one.
    /// </summary>
    [Fact]
    public void ParseRouteAttributes_InvalidFirstOrigin_StillRejected()
    {
        var update = new BgpUpdateMessage
        {
            PathAttributes =
            [
                new PathAttribute
                {
                    Flags = BgpConstants.Attribute.FlagTransitive,
                    TypeCode = BgpConstants.Attribute.Origin,
                    Data = [7],
                },
                AttributeHelper.WriteOrigin(BgpOrigin.Igp),
                AttributeHelper.WriteAsPath([65001u], fourByteAsn: true),
                AttributeHelper.WriteNextHop(0x0A000001),
            ],
            Nlri = []
        };

        var ex = Assert.Throws<BgpNotificationException>(
            () => UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: true));
        Assert.Equal(BgpConstants.SubError.InvalidOriginAttribute, ex.SubErrorCode);
    }

    // ---- #290: attribute flags vs type code, and fixed lengths ----

    /// <summary>
    /// Builds a minimal valid announcing UPDATE (ORIGIN + AS_PATH + NEXT_HOP) with
    /// <paramref name="replacement"/> substituted for the attribute sharing its type code, or
    /// appended when it is a new type.
    /// </summary>
    private static BgpUpdateMessage UpdateWith(PathAttribute replacement, bool fourByteAsn = true)
    {
        // The base AS_PATH must match the session encoding the caller will parse with, or it throws
        // Malformed AS_PATH before the loop ever reaches the attribute under test.
        var attrs = new List<PathAttribute>
        {
            AttributeHelper.WriteOrigin(BgpOrigin.Igp),
            AttributeHelper.WriteAsPath([65001u], fourByteAsn),
            AttributeHelper.WriteNextHop(0x0A000001),
        };
        var i = attrs.FindIndex(a => a.TypeCode == replacement.TypeCode);
        if (i >= 0) attrs[i] = replacement; else attrs.Add(replacement);
        return new BgpUpdateMessage { PathAttributes = attrs, Nlri = [] };
    }

    /// <summary>
    /// RFC 7606 §3: "If the value of either the Optional or Transitive bits in the Attribute Flags
    /// is in conflict with their specified values, then the attribute MUST be treated as malformed
    /// and the 'treat-as-withdraw' approach used." Nothing checked this before #290.
    /// </summary>
    [Theory]
    [InlineData(BgpConstants.Attribute.Origin, (byte)0x80)]          // well-known marked Optional
    [InlineData(BgpConstants.Attribute.Origin, (byte)0x00)]          // Transitive cleared
    [InlineData(BgpConstants.Attribute.AsPath, (byte)0x00)]          // well-known non-transitive
    [InlineData(BgpConstants.Attribute.AsPath, (byte)0xC0)]          // marked Optional
    [InlineData(BgpConstants.Attribute.NextHop, (byte)0x80)]
    [InlineData(BgpConstants.Attribute.Community, (byte)0x40)]       // optional marked well-known
    [InlineData(BgpConstants.Attribute.Community, (byte)0x80)]       // Transitive cleared
    [InlineData(BgpConstants.Attribute.LargeCommunity, (byte)0x40)]
    public void ParseRouteAttributes_FlagsConflictWithTypeCode_AttributeFlagsError(byte typeCode, byte flags)
    {
        var data = typeCode switch
        {
            BgpConstants.Attribute.Origin => new byte[] { 0x00 },
            BgpConstants.Attribute.AsPath => [0x02, 0x01, 0x00, 0x00, 0xFD, 0xE9],
            BgpConstants.Attribute.NextHop => [0x0A, 0x00, 0x00, 0x01],
            BgpConstants.Attribute.Community => [0x00, 0x00, 0x00, 0x01],
            BgpConstants.Attribute.LargeCommunity => new byte[12],
            BgpConstants.Attribute.As4Path => [0x02, 0x01, 0x00, 0x03, 0x0D, 0x40],
            _ => throw new InvalidOperationException(),
        };
        var update = UpdateWith(new PathAttribute { Flags = flags, TypeCode = typeCode, Data = data });

        var ex = Assert.Throws<BgpNotificationException>(
            () => UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: true));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.AttributeFlagsError, ex.SubErrorCode);
    }

    /// <summary>
    /// AS4_PATH / AS4_AGGREGATOR exist only to tunnel 4-octet ASNs across a 2-octet session
    /// (RFC 6793 §3), so their shape IS validated on a 2-octet session — where the parser reads
    /// them.
    /// </summary>
    [Theory]
    [InlineData(BgpConstants.Attribute.As4Path, (byte)0x40)]   // marked well-known
    [InlineData(BgpConstants.Attribute.As4Path, (byte)0x80)]   // Transitive cleared
    public void ParseRouteAttributes_As4FlagsConflict_OnTwoOctetSession_AttributeFlagsError(byte typeCode, byte flags)
    {
        var update = UpdateWith(new PathAttribute
        {
            Flags = flags,
            TypeCode = typeCode,
            Data = [0x02, 0x01, 0x00, 0x03, 0x0D, 0x40], // AS_SEQUENCE [200000]
        }, fourByteAsn: false);

        var ex = Assert.Throws<BgpNotificationException>(
            () => UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: false));
        Assert.Equal(BgpConstants.SubError.AttributeFlagsError, ex.SubErrorCode);
    }

    /// <summary>
    /// ...and is NOT validated on a 4-octet session, where the parser ignores those attributes
    /// outright (`case ... when !fourByteAsnSession`). Validating an attribute the codec never
    /// reads would withdraw an UPDATE's routes over something that has no effect on the result —
    /// the same over-rejection the shape table avoids for MED/LOCAL_PREF/ATOMIC_AGGREGATE (#290 review).
    /// </summary>
    [Theory]
    [InlineData(BgpConstants.Attribute.As4Path, (byte)0x40)]
    [InlineData(BgpConstants.Attribute.As4Path, (byte)0x00)]
    [InlineData(BgpConstants.Attribute.As4Aggregator, (byte)0x40)]
    public void ParseRouteAttributes_As4FlagsConflict_OnFourOctetSession_IsIgnored(byte typeCode, byte flags)
    {
        var update = UpdateWith(new PathAttribute
        {
            Flags = flags,
            TypeCode = typeCode,
            Data = [0x02, 0x01, 0x00, 0x03, 0x0D, 0x40],
        });

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: true);

        Assert.Equal(0x0A000001u, attrs.NextHop);
        Assert.Equal([65001u], attrs.AsPath); // the ignored AS4_PATH did not affect the result
    }

    /// <summary>
    /// The Partial bit is deliberately NOT checked. RFC 7606 §3 narrows the flags check to the
    /// Optional and Transitive bits; RFC 4271 §5's "Partial MUST be 0 for well-known attributes"
    /// is not re-stated as an error condition, and rejecting on it would drop routes that
    /// conformant implementations accept.
    /// </summary>
    [Theory]
    [InlineData(BgpConstants.Attribute.Origin, (byte)0x60)]          // Transitive | Partial
    [InlineData(BgpConstants.Attribute.Community, (byte)0xE0)]       // Optional | Transitive | Partial
    public void ParseRouteAttributes_PartialBitSet_IsAccepted(byte typeCode, byte flags)
    {
        var data = typeCode == BgpConstants.Attribute.Origin
            ? new byte[] { 0x00 }
            : [0x00, 0x00, 0x00, 0x01];
        var update = UpdateWith(new PathAttribute { Flags = flags, TypeCode = typeCode, Data = data });

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: true);

        Assert.Equal(0x0A000001u, attrs.NextHop);
    }

    /// <summary>
    /// RFC 7606 §7.1 (ORIGIN) and §7.3 (NEXT_HOP): "considered malformed if its length is not 1"
    /// / "not 4". The previous checks were lower bounds (&lt; 1 / &lt; 4), so an over-long ORIGIN was
    /// accepted and an 8-octet NEXT_HOP was silently truncated to its first four octets.
    /// </summary>
    [Theory]
    [InlineData(BgpConstants.Attribute.Origin, 0)]
    [InlineData(BgpConstants.Attribute.Origin, 2)]
    [InlineData(BgpConstants.Attribute.Origin, 4)]
    [InlineData(BgpConstants.Attribute.NextHop, 0)]
    [InlineData(BgpConstants.Attribute.NextHop, 3)]
    [InlineData(BgpConstants.Attribute.NextHop, 8)]  // an IPv6-shaped value must not be truncated
    [InlineData(BgpConstants.Attribute.NextHop, 16)]
    public void ParseRouteAttributes_FixedLengthMismatch_AttributeLengthError(byte typeCode, int length)
    {
        var update = UpdateWith(new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagTransitive,
            TypeCode = typeCode,
            Data = new byte[length],
        });

        var ex = Assert.Throws<BgpNotificationException>(
            () => UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: true));
        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.AttributeLengthError, ex.SubErrorCode);
    }

    /// <summary>
    /// An unrecognized OPTIONAL attribute is not shape-checked — RFC 7606 §3 leaves those to the
    /// optional/transitive propagation rules. Validating attributes the codec never reads would
    /// only create new ways to reject an UPDATE a conformant implementation accepts. The
    /// well-known half of an unknown type code is different since #322: Optional=0 now rejects
    /// with subcode 2 (see <see cref="ParseRouteAttributes_UnrecognizedWellKnownAttribute_ThrowsSubcode2"/>).
    /// </summary>
    [Fact]
    public void ParseRouteAttributes_UnrecognizedAttribute_IsNotShapeChecked()
    {
        var update = UpdateWith(new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagOptional | BgpConstants.Attribute.FlagTransitive,
            TypeCode = 200,        // unassigned, but OPTIONAL — ignored, never shape-checked
            Data = [1, 2, 3],
        });

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: true);

        Assert.Equal(0x0A000001u, attrs.NextHop);
    }

    /// <summary>Correct flags and lengths still parse — guards against over-rejecting.</summary>
    [Fact]
    public void ParseRouteAttributes_CanonicalFlagsAndLengths_StillParse()
    {
        var update = new BgpUpdateMessage
        {
            PathAttributes =
            [
                AttributeHelper.WriteOrigin(BgpOrigin.Igp),
                AttributeHelper.WriteAsPath([65001u], fourByteAsn: true),
                AttributeHelper.WriteNextHop(0x0A000001),
                AttributeHelper.WriteCommunities([65000u, 100u]),
                AttributeHelper.WriteLargeCommunities([(64512u, 1u, 2u)]),
            ],
            Nlri = []
        };

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: true);

        Assert.Equal(0x0A000001u, attrs.NextHop);
        Assert.Equal([65000u, 100u], attrs.Communities);
        Assert.Equal([(64512u, 1u, 2u)], attrs.LargeCommunities);
    }

    /// <summary>
    /// The interaction between the duplicate rule (#287) and shape validation (#290), which only
    /// exists once both are in place: the guard runs FIRST, so a discarded later occurrence is never
    /// shape-checked. RFC 7606 §3 says those occurrences are discarded, not "discarded but still
    /// validated" — rejecting on the flags of an attribute that has no effect on the result would
    /// drop an UPDATE a conformant implementation accepts.
    /// </summary>
    [Fact]
    public void ParseRouteAttributes_DuplicateWithConflictingFlags_IsDiscardedNotRejected()
    {
        var update = new BgpUpdateMessage
        {
            PathAttributes =
            [
                AttributeHelper.WriteOrigin(BgpOrigin.Igp),
                AttributeHelper.WriteAsPath([65001u], fourByteAsn: true),
                AttributeHelper.WriteNextHop(0x0A000001),
                AttributeHelper.WriteCommunities([65000u, 100u]),   // first — wins
                new PathAttribute
                {
                    Flags = BgpConstants.Attribute.FlagTransitive,  // well-known: conflicts with COMMUNITY
                    TypeCode = BgpConstants.Attribute.Community,
                    Data = [0x00, 0x00, 0x03, 0xE7],
                },
            ],
            Nlri = []
        };

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: true);

        Assert.Equal([65000u, 100u], attrs.Communities);
        Assert.Equal(0x0A000001u, attrs.NextHop);
    }

    /// <summary>
    /// The inverse: a conflicting-flags attribute in FIRST position is still rejected, so the
    /// duplicate rule cannot be used to smuggle a malformed attribute past shape validation.
    /// </summary>
    [Fact]
    public void ParseRouteAttributes_ConflictingFlagsOnFirstOccurrence_StillRejected()
    {
        var update = new BgpUpdateMessage
        {
            PathAttributes =
            [
                AttributeHelper.WriteOrigin(BgpOrigin.Igp),
                AttributeHelper.WriteAsPath([65001u], fourByteAsn: true),
                AttributeHelper.WriteNextHop(0x0A000001),
                new PathAttribute
                {
                    Flags = BgpConstants.Attribute.FlagTransitive,  // conflicting — and first
                    TypeCode = BgpConstants.Attribute.Community,
                    Data = [0x00, 0x00, 0x03, 0xE7],
                },
                AttributeHelper.WriteCommunities([65000u, 100u]),
            ],
            Nlri = []
        };

        var ex = Assert.Throws<BgpNotificationException>(
            () => UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: true));
        Assert.Equal(BgpConstants.SubError.AttributeFlagsError, ex.SubErrorCode);
    }
}
