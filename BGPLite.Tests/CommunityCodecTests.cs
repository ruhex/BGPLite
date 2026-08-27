using BGPLite.Protocol;

namespace BGPLite.Tests;

public class CommunityCodecTests
{
    [Fact]
    public void Parse_PacksAsnAndValue()
    {
        Assert.Equal((65000u << 16) | 100u, CommunityCodec.Parse("65000:100"));
    }

    [Fact]
    public void Format_RoundTrips()
    {
        var packed = CommunityCodec.Parse("65444:1");
        Assert.Equal("65444:1", CommunityCodec.Format(packed));
    }

    [Fact]
    public void Parse_InvalidThrows()
    {
        Assert.Throws<FormatException>(() => CommunityCodec.Parse("nope"));
    }

    [Fact]
    public void Parse_ValueAbove65535_Throws()
    {
        // #328: the VALUE half used to be silently masked (131071 = 0x1FFFF → low 16 bits 0xFFFF),
        // corrupting operator-supplied tags without a trace — symmetric with the ASN check now.
        var ex = Assert.Throws<FormatException>(() => CommunityCodec.Parse("1:131071"));
        Assert.Contains("VALUE", ex.Message);
    }

    [Fact]
    public void Parse_MaxValue65535_Accepted()
    {
        Assert.Equal((65000u << 16) | 0xFFFFu, CommunityCodec.Parse("65000:65535"));
    }

    [Fact]
    public void Parse_NonNumericOrHugeParts_ThrowFormatException_NotOverflow()
    {
        // uint.Parse used to surface huge/negative parts as OverflowException, which the
        // FormatException filters in ConfigCommunityResolver do not catch (#328 review).
        Assert.Throws<FormatException>(() => CommunityCodec.Parse("65000:99999999999999999999"));
        Assert.Throws<FormatException>(() => CommunityCodec.Parse("-1:1"));
        Assert.Throws<FormatException>(() => CommunityCodec.Parse("65000:abc"));
    }

    [Fact]
    public void Parse_FourByteAsn_Throws()
    {
        // A 4-byte ASN would silently wrap to 0 if not validated (131072 << 16 == 0 in uint).
        Assert.Throws<FormatException>(() => CommunityCodec.Parse("131072:100"));
    }
}
