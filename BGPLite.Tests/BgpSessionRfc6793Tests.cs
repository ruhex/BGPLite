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
        var merged = BgpSession.MergeAsPathWithAs4Path([65010u, BgpConstants.AsPath.AsTrans, 65001u], [200000u, 65001u]);

        Assert.Equal([65010u, 200000u, 65001u], merged);
    }

    [Fact]
    public void MergeAsPathWithAs4Path_UnresolvedAsTrans_Throws()
    {
        var ex = Assert.Throws<BgpNotificationException>(() =>
            BgpSession.MergeAsPathWithAs4Path([65010u, BgpConstants.AsPath.AsTrans, 65001u], [200000u]));

        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.Unspecific, ex.SubErrorCode);
    }

    [Fact]
    public void MergeAsPathWithAs4Path_EmptyAs4Path_ReturnsAsPathUnchanged()
    {
        var merged = BgpSession.MergeAsPathWithAs4Path([65010u, 65001u], []);

        Assert.Equal([65010u, 65001u], merged);
    }

    [Fact]
    public void MergeAsPathWithAs4Path_EqualLengths_ReturnsAs4Path()
    {
        var merged = BgpSession.MergeAsPathWithAs4Path([BgpConstants.AsPath.AsTrans, 65001u], [200000u, 65001u]);

        Assert.Equal([200000u, 65001u], merged);
    }

    [Fact]
    public void MergeAsPathWithAs4Path_As4PathLongerThanAsPath_Throws()
    {
        var ex = Assert.Throws<BgpNotificationException>(() =>
            BgpSession.MergeAsPathWithAs4Path([65001u], [200000u, 300000u]));

        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.Unspecific, ex.SubErrorCode);
    }

    [Fact]
    public void ValidateAggregatorReconstruction_AsTransWithAs4Aggregator_DoesNotThrow()
    {
        BgpSession.ValidateAggregatorReconstruction(BgpConstants.AsPath.AsTrans, 200000u);
    }

    [Fact]
    public void ValidateAggregatorReconstruction_NonAsTransAggregator_DoesNotThrow()
    {
        BgpSession.ValidateAggregatorReconstruction(65001u, null);
    }

    [Fact]
    public void ValidateAggregatorReconstruction_NullAggregator_DoesNotThrow()
    {
        BgpSession.ValidateAggregatorReconstruction(null, null);
    }

    [Fact]
    public void ValidateAggregatorReconstruction_NullAggregatorWithAs4Aggregator_Throws()
    {
        var ex = Assert.Throws<BgpNotificationException>(() =>
            BgpSession.ValidateAggregatorReconstruction(null, 200000u));

        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.Unspecific, ex.SubErrorCode);
    }

    [Fact]
    public void ValidateAggregatorReconstruction_AsTransWithoutAs4Aggregator_Throws()
    {
        var ex = Assert.Throws<BgpNotificationException>(() =>
            BgpSession.ValidateAggregatorReconstruction(BgpConstants.AsPath.AsTrans, null));

        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.Unspecific, ex.SubErrorCode);
    }

    [Fact]
    public void ValidateMandatoryAttributes_MissingRequiredAttribute_Throws()
    {
        var ex = Assert.Throws<BgpNotificationException>(() =>
            BgpSession.ValidateMandatoryAttributes(false, true, true));

        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.MissingWellKnownAttribute, ex.SubErrorCode);
    }

    [Fact]
    public void ValidateMandatoryAttributes_MissingAsPath_Throws()
    {
        var ex = Assert.Throws<BgpNotificationException>(() =>
            BgpSession.ValidateMandatoryAttributes(true, false, true));

        Assert.Equal(BgpConstants.Error.UpdateMessageError, ex.ErrorCode);
        Assert.Equal(BgpConstants.SubError.MissingWellKnownAttribute, ex.SubErrorCode);
    }

    [Fact]
    public void ValidateMandatoryAttributes_MissingNextHop_Throws()
    {
        var ex = Assert.Throws<BgpNotificationException>(() =>
            BgpSession.ValidateMandatoryAttributes(true, true, false));

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

        Assert.Equal([BgpConstants.Capability.FourOctetAsn, 3, 0x01, 0x02, 0x03], BgpSession.GetMalformedFourOctetAsnCapabilityData(open));
    }

    [Fact]
    public void GetMalformedFourOctetAsnCapabilityData_EmptyData_ReturnsZeroLengthTlv()
    {
        var open = new BgpOpenMessage
        {
            Capabilities = [new BgpCapabilityInfo { Code = BgpConstants.Capability.FourOctetAsn, Data = [] }]
        };

        Assert.Equal([BgpConstants.Capability.FourOctetAsn, 0], BgpSession.GetMalformedFourOctetAsnCapabilityData(open));
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

        Assert.Equal([BgpConstants.Capability.FourOctetAsn, 2, 0x01, 0x02], BgpSession.GetMalformedFourOctetAsnCapabilityData(open));
    }
}
