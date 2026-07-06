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
        Assert.Equal(BgpConstants.SubError.Unspecific, ex.SubErrorCode);
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
    public void MergeAsPathWithAs4Path_As4PathLongerThanAsPath_Throws()
    {
        var ex = Assert.Throws<BgpNotificationException>(() =>
            UpdateCodec.MergeAsPathWithAs4Path([65001u], [200000u, 300000u]));

        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.Unspecific, ex.SubErrorCode);
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
        Assert.Equal(BgpConstants.SubError.Unspecific, ex.SubErrorCode);
    }

    [Fact]
    public void ValidateAggregatorReconstruction_AsTransWithoutAs4Aggregator_Throws()
    {
        var ex = Assert.Throws<BgpNotificationException>(() =>
            UpdateCodec.ValidateAggregatorReconstruction(BgpConstants.AsPath.AsTrans, null));

        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.Unspecific, ex.SubErrorCode);
    }

    // --- #154: wire-format codec for AGGREGATOR (type 7) and AS4_AGGREGATOR (type 18) ---
    // The prior code had the lengths inverted (AGGREGATOR accepted 8 bytes on a 4-byte session,
    // AS4_AGGREGATOR expected 4) — every legal value was rejected. These tests pin the RFC 6793 §3
    // wire format so the regression cannot slip back.

    [Fact]
    public void ReadAggregatorAsn_LegacySixByteForm_ReadsTwoOctetAs()
    {
        // RFC 6793 §3: AGGREGATOR is unconditionally 6 octets (2 AS + 4 IPv4), independent of the
        // session's 4-byte-ASN capability. The fourByteAsn flag must NOT change the wire format.
        var data = new byte[] { 0xFD, 0xE9, 10, 0, 0, 1 }; // AS 65001, aggregator IP 10.0.0.1
        var attr = new PathAttribute { Flags = BgpConstants.Attribute.FlagTransitive, TypeCode = BgpConstants.Attribute.Aggregator, Data = data };

        Assert.Equal(65001u, AttributeHelper.ReadAggregatorAsn(attr, fourByteAsn: false));
        // The flag must be ignored — AGGREGATOR is always 6 bytes.
        Assert.Equal(65001u, AttributeHelper.ReadAggregatorAsn(attr, fourByteAsn: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadAggregatorAsn_WrongLength_Throws(bool fourByteAsn)
    {
        // The old bug: passing fourByteAsn=true expected 8 bytes and rejected the legal 6-byte form.
        var tooShort = new PathAttribute { TypeCode = BgpConstants.Attribute.Aggregator, Data = new byte[4] };
        var tooLong = new PathAttribute { TypeCode = BgpConstants.Attribute.Aggregator, Data = new byte[8] };

        Assert.Throws<BgpParseException>(() => AttributeHelper.ReadAggregatorAsn(tooShort, fourByteAsn));
        Assert.Throws<BgpParseException>(() => AttributeHelper.ReadAggregatorAsn(tooLong, fourByteAsn));
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
}
