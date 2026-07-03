using BGPLite.Api;

namespace BGPLite.Tests;

/// <summary>
/// Tests for <see cref="ManagementApi.ParseCustomPrefix"/> (#100): custom-prefix CIDRs supplied
/// via the management API are validated (IPv4 only, mask 0..32) before reaching the store.
/// Previously the parser only checked for a '/' and ran <c>byte.Parse</c> on the tail, accepting
/// garbage like <c>1.2.3.4/250</c> (length 250) or non-IP prefixes, which corrupted export sets.
/// </summary>
public class CustomPrefixValidationTests
{
    [Theory]
    [InlineData("1.2.3.0/24", "1.2.3.0", 24)]
    [InlineData("10.0.0.0/8", "10.0.0.0", 8)]
    [InlineData("0.0.0.0/0", "0.0.0.0", 0)]   // boundary: default route
    [InlineData("255.255.255.255/32", "255.255.255.255", 32)] // boundary: single host
    public void Parses_Valid_IPv4_CIDR(string cidr, string expectedPrefix, byte expectedLength)
    {
        var result = ManagementApi.ParseCustomPrefix(cidr);

        Assert.NotNull(result);
        Assert.Equal(expectedPrefix, result!.Value.Prefix);
        Assert.Equal(expectedLength, result.Value.Length);
    }

    [Theory]
    [InlineData("1.2.3.4")]              // no slash
    [InlineData("not-an-ip/24")]         // non-IP prefix
    [InlineData("1.2.3.4/33")]           // length out of range (just over)
    [InlineData("1.2.3.4/250")]          // length far out of range — previously stored as 250
    [InlineData("1.2.3.4/-1")]           // negative length
    [InlineData("1.2.3.4/abc")]          // non-numeric length
    [InlineData("::1/128")]              // IPv6 prefix (rejected — IPv4 only)
    [InlineData("2001:db8::/32")]        // IPv6 prefix (rejected — IPv4 only)
    [InlineData("1.2.3.4/")]             // slash at end — empty length
    [InlineData("1.2.3.4/24/extra")]     // more than one slash
    [InlineData("/24")]                  // empty prefix
    [InlineData("")]                     // empty input
    public void Rejects_Invalid_CIDR(string cidr)
    {
        Assert.Null(ManagementApi.ParseCustomPrefix(cidr));
    }

    [Fact]
    public void Rejects_Null_Input()
    {
        Assert.Null(ManagementApi.ParseCustomPrefix(null));
    }

    [Fact]
    public void Rejects_Whitespace_Only_Input()
    {
        Assert.Null(ManagementApi.ParseCustomPrefix("   "));
    }
}
