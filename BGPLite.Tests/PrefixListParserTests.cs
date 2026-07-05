using System.Net;
using BGPLite.Providers;
using BGPLite.Protocol;

namespace BGPLite.Tests;

public class PrefixListParserTests
{
    private static uint Ip(string s) => BgpConstants.IPAddressToUint(IPAddress.Parse(s));

    [Fact]
    public void ParsesSingleCidr()
    {
        var result = PrefixListParser.Parse("1.2.3.0/24");
        var single = Assert.Single(result);
        Assert.Equal((Ip("1.2.3.0"), (byte)24), single);
    }

    [Fact]
    public void SkipsBlankAndCommentLines()
    {
        var result = PrefixListParser.Parse("# header\n\n1.2.3.0/24\n   \n# tail\n");
        Assert.Single(result);
    }

    [Fact]
    public void SkipsMalformedLineWithoutSlash()
    {
        var result = PrefixListParser.Parse("garbage\n1.2.3.0/24");
        Assert.Single(result);
    }

    [Fact]
    public void SkipsIpv6()
    {
        var result = PrefixListParser.Parse("::1/128\n1.2.3.0/24");
        var single = Assert.Single(result);
        Assert.Equal((Ip("1.2.3.0"), (byte)24), single);
    }

    [Fact]
    public void SkipsBadLength()
    {
        var result = PrefixListParser.Parse("1.2.3.0/abc\n1.2.3.0/24");
        Assert.Single(result);
    }

    [Fact]
    public void EmptyInputReturnsEmpty() => Assert.Empty(PrefixListParser.Parse(""));

    [Fact]
    public void ParsesMultiple()
    {
        var result = PrefixListParser.Parse("10.0.0.0/8\n5.6.0.0/16");
        Assert.Equal(2, result.Count);
    }

    // --- #162: length validation + host-bit masking regression coverage ---

    [Fact]
    public void RejectsDefaultRoute_Length0()
    {
        // A route server must not originate a default — a stray 0.0.0.0/0 in a peer-supplied
        // URL list would otherwise advertise the entire IPv4 space (#147, #162).
        Assert.Empty(PrefixListParser.Parse("0.0.0.0/0"));
    }

    [Theory]
    [InlineData("1.2.3.0/33")]
    [InlineData("1.2.3.0/250")]
    [InlineData("1.2.3.0/255")]
    public void RejectsOutOfRangeLength(string line)
    {
        // byte.TryParse accepts 0..255; only 1..32 is a valid IPv4 prefix length.
        Assert.Empty(PrefixListParser.Parse(line));
    }

    [Fact]
    public void RejectsDefaultRoute_KeepsValidLines()
    {
        var result = PrefixListParser.Parse("0.0.0.0/0\n1.2.3.0/24");
        var single = Assert.Single(result);
        Assert.Equal((Ip("1.2.3.0"), (byte)24), single);
    }

    [Fact]
    public void MasksHostBits_ToNetworkAddress()
    {
        // 10.0.0.5/24 and 10.0.0.99/24 are the same network; both must normalize to 10.0.0.0/24
        // so downstream dedup keys them as one route instead of two distinct rows.
        var result = PrefixListParser.Parse("10.0.0.5/24\n10.0.0.99/24");
        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal((Ip("10.0.0.0"), (byte)24), r));
    }

    [Fact]
    public void MasksHostBits_NetworkAlreadyAligned_IsIdempotent()
    {
        var result = PrefixListParser.Parse("10.0.0.0/24");
        var single = Assert.Single(result);
        Assert.Equal((Ip("10.0.0.0"), (byte)24), single);
    }

    [Fact]
    public void MasksHostBits_BoundaryLengths()
    {
        // /1 masks all but the top bit; /32 (host route) masks nothing.
        var r1 = Assert.Single(PrefixListParser.Parse("255.255.255.255/1"));
        Assert.Equal((Ip("128.0.0.0"), (byte)1), r1);

        var r32 = Assert.Single(PrefixListParser.Parse("10.0.0.5/32"));
        Assert.Equal((Ip("10.0.0.5"), (byte)32), r32);
    }

    [Fact]
    public void SkipsUtf8Bom_FirstLine()
    {
        // A UTF-8 BOM (\uFEFF) is not stripped by string.Trim() — without explicit handling the
        // first line of a BOM-prefixed list is silently dropped (#162).
        var bom = "\uFEFF1.2.3.0/24";
        var result = PrefixListParser.Parse(bom);
        var single = Assert.Single(result);
        Assert.Equal((Ip("1.2.3.0"), (byte)24), single);
    }
}
