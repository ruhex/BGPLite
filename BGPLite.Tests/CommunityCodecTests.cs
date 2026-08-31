using BGPLite.Api;
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

/// <summary>
/// #266 item 3: the API-boundary contract for peer-supplied source communities — the exact rule
/// CommunityCodec enforces (#328), so an out-of-range half or garbage is a 400 at save time
/// instead of a silently-masked or auto-substituted tag at send time.
/// </summary>
public class AddSourceCommunityValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("65000:100")]
    [InlineData("0:0")]
    [InlineData("65535:65535")]
    public void IsValidCommunity_Accepts(string? community)
        => Assert.True(ManagementApi.IsValidCommunity(community));

    [Theory]
    [InlineData("65000:70000")]   // VALUE above 65535 — the #328 case the old codec masked to 4464
    [InlineData("70000:100")]     // ASN half
    [InlineData("65000")]         // no colon
    [InlineData("a:b")]
    [InlineData("65000:100:1")]   // large-community shape is NOT a valid small community
    [InlineData("-1:5")]
    public void IsValidCommunity_Rejects(string community)
        => Assert.False(ManagementApi.IsValidCommunity(community));
}
