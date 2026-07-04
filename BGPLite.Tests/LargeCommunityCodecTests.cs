using BGPLite.Protocol;
using BGPLite.Server;

namespace BGPLite.Tests;

/// <summary>
/// Covers the RFC 8092 Large Communities codec (AttributeHelper), its recognition as a known
/// attribute, the outbound attribute-emission helper, and the receive→Route storage path.
/// </summary>
public class LargeCommunityCodecTests
{
    [Fact]
    public void WriteAndRead_RoundtripsTriplets()
    {
        var large = new (uint, uint, uint)[]
        {
            (2914u, 1u, 2u),
            (65000u, 100u, 200u),
            (4294967295u, 0u, 4294967294u), // boundary values
        };

        var attr = AttributeHelper.WriteLargeCommunities(large);

        Assert.Equal(BgpConstants.Attribute.LargeCommunity, attr.TypeCode);
        Assert.Equal(BgpConstants.Attribute.FlagOptional | BgpConstants.Attribute.FlagTransitive, attr.Flags);
        Assert.Equal(large.Length * 12, attr.Data.Length);
        Assert.Equal(large, AttributeHelper.ReadLargeCommunities(attr));
    }

    [Fact]
    public void Write_Empty_ProducesZeroLength()
    {
        var attr = AttributeHelper.WriteLargeCommunities([]);
        Assert.Empty(attr.Data);
        // RFC 8092 §2: a zero-length payload is malformed (non-zero multiple of 12 required) → reject on decode.
        Assert.Throws<BgpParseException>(() => AttributeHelper.ReadLargeCommunities(attr));
    }

    [Fact]
    public void Write_EncodesFieldsBigEndian()
    {
        // 0x01020304 : 0x05060708 : 0x090A0B0C — verifies each 4-octet field is big-endian.
        var attr = AttributeHelper.WriteLargeCommunities([(0x01020304u, 0x05060708u, 0x090A0B0Cu)]);
        Assert.Equal(
        [
            0x01, 0x02, 0x03, 0x04,
            0x05, 0x06, 0x07, 0x08,
            0x09, 0x0A, 0x0B, 0x0C
        ], attr.Data);
    }

    [Fact]
    public void Read_ZeroLength_Throws()
    {
        // RFC 8092 §2: the attribute length MUST be a non-zero multiple of 12 — zero is malformed.
        var attr = new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagOptional | BgpConstants.Attribute.FlagTransitive,
            TypeCode = BgpConstants.Attribute.LargeCommunity,
            Data = []
        };
        Assert.Throws<BgpParseException>(() => AttributeHelper.ReadLargeCommunities(attr));
    }

    [Theory]
    [InlineData(1)]   // trailing byte after one triplet
    [InlineData(13)]  // one triplet + one byte
    [InlineData(24 + 5)] // two triplets + 5 bytes
    public void Read_NonMultipleOf12_Throws(int length)
    {
        var attr = new PathAttribute
        {
            Flags = BgpConstants.Attribute.FlagOptional | BgpConstants.Attribute.FlagTransitive,
            TypeCode = BgpConstants.Attribute.LargeCommunity,
            Data = new byte[length]
        };
        var ex = Assert.Throws<BgpParseException>(() => AttributeHelper.ReadLargeCommunities(attr));
        Assert.Contains("multiple of 12", ex.Message);
    }

    [Fact]
    public void Format_RendersCanonicalText()
    {
        Assert.Equal("2914:1:2", AttributeHelper.FormatLargeCommunity((2914u, 1u, 2u)));
        Assert.Equal("0:0:0", AttributeHelper.FormatLargeCommunity((0u, 0u, 0u)));
        Assert.Equal("4294967295:1:4294967294",
            AttributeHelper.FormatLargeCommunity((4294967295u, 1u, 4294967294u)));
    }

    [Fact]
    public void IsKnownAttribute_RecognizesLargeCommunity()
    {
        Assert.True(AttributeHelper.IsKnownAttribute(BgpConstants.Attribute.LargeCommunity));
    }

    [Fact]
    public void WithLargeCommunityAttribute_Empty_ReturnsBaseInstance()
    {
        var baseAttrs = BgpSession.BuildUpdateAttributes(200000u, localFourByteAsn: true, 0x0A000001u, [1234u]);

        var result = BgpSession.WithLargeCommunityAttribute(baseAttrs, []);

        Assert.Same(baseAttrs, result);
    }

    [Fact]
    public void WithLargeCommunityAttribute_NonEmpty_AppendsWithoutMutatingBase()
    {
        var baseAttrs = BgpSession.BuildUpdateAttributes(200000u, localFourByteAsn: true, 0x0A000001u, [1234u]);
        var baseCount = baseAttrs.Count;
        var large = new (uint, uint, uint)[] { (2914u, 1u, 2u), (65000u, 9u, 9u) };

        var result = BgpSession.WithLargeCommunityAttribute(baseAttrs, large);

        Assert.NotSame(baseAttrs, result);
        Assert.Equal(baseCount + 1, result.Count);
        Assert.Equal(baseCount, baseAttrs.Count); // base list untouched (cache stays correct)

        var attr = Assert.Single(result, a => a.TypeCode == BgpConstants.Attribute.LargeCommunity);
        Assert.Equal(BgpConstants.Attribute.FlagOptional | BgpConstants.Attribute.FlagTransitive, attr.Flags);
        Assert.Equal(large, AttributeHelper.ReadLargeCommunities(attr));
    }

    [Fact]
    public void WithLargeCommunityAttribute_DoesNotMutateSharedCacheBase()
    {
        // Models the send path: the #87 cache hands out ONE base list for a regular-community
        // set, used by several batches that each carry a DIFFERENT large-community set. Each
        // batch must observe the un-augmented base, never the previous batch's appended attr.
        var cache = BgpSession.CreateUpdateAttributeCache();
        var communities = new uint[] { 1234u };

        var baseAttrs = BgpSession.GetCachedUpdateAttributes(200000u, localFourByteAsn: true, 0x0A000001u, communities, cache);

        var firstWithLarge = BgpSession.WithLargeCommunityAttribute(baseAttrs, [(1u, 1u, 1u)]);
        var secondWithLarge = BgpSession.WithLargeCommunityAttribute(baseAttrs, [(2u, 2u, 2u)]);

        // The cache entry itself never gained a LARGE_COMMUNITY attribute.
        Assert.DoesNotContain(baseAttrs, a => a.TypeCode == BgpConstants.Attribute.LargeCommunity);
        // Each per-group list carries only its own large-community set.
        Assert.Equal([(1u, 1u, 1u)], AttributeHelper.ReadLargeCommunities(
            Assert.Single(firstWithLarge, a => a.TypeCode == BgpConstants.Attribute.LargeCommunity)));
        Assert.Equal([(2u, 2u, 2u)], AttributeHelper.ReadLargeCommunities(
            Assert.Single(secondWithLarge, a => a.TypeCode == BgpConstants.Attribute.LargeCommunity)));
    }
}
