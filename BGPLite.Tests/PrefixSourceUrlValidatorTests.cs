using System.Net;
using BGPLite.Providers;

namespace BGPLite.Tests;

/// <summary>
/// Tests for <see cref="PrefixSourceUrlValidator"/> (#144): SSRF defense — validates that URLs
/// resolve to public IPs (not private/loopback/link-local/cloud-metadata).
/// </summary>
public class PrefixSourceUrlValidatorTests
{
    [Theory]
    [InlineData("127.0.0.1")]        // loopback
    [InlineData("10.0.0.1")]         // private (RFC 1918)
    [InlineData("172.17.0.1")]       // private (Docker bridge!)
    [InlineData("192.168.1.1")]      // private
    [InlineData("169.254.169.254")]  // cloud metadata
    [InlineData("0.0.0.0")]          // unspecified
    [InlineData("::1")]              // IPv6 loopback
    [InlineData("100.64.0.1")]      // CGNAT (RFC 6598)
    [InlineData("198.18.0.1")]      // benchmarking
    [InlineData("224.0.0.1")]       // multicast
    [InlineData("240.0.0.1")]       // reserved
    [InlineData("fc00::1")]          // IPv6 unique-local
    [InlineData("fe80::1")]          // IPv6 link-local
    public void IsBlockedAddress_Rejects_Internal(string ip)
        => Assert.True(PrefixSourceUrlValidator.IsBlockedAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("93.184.216.34")]  // example.com
    [InlineData("8.8.8.8")]        // Google DNS
    [InlineData("1.1.1.1")]        // Cloudflare DNS
    [InlineData("45.148.244.55")]  // BGPLite peer
    public void IsBlockedAddress_Accepts_Public(string ip)
        => Assert.False(PrefixSourceUrlValidator.IsBlockedAddress(IPAddress.Parse(ip)));

    [Fact]
    public void IsBlockedAddress_Normalizes_IPv4Mapped_IPv6()
    {
        var mapped = IPAddress.Parse("::ffff:127.0.0.1");
        Assert.True(PrefixSourceUrlValidator.IsBlockedAddress(mapped));
    }

    [Fact]
    public async Task ValidateUrlAsync_Rejects_Loopback()
    {
        var (isValid, error) = await PrefixSourceUrlValidator.ValidateUrlAsync(
            "http://localhost/api/secret",
            (_, _) => ValueTask.FromResult<IPAddress[]>([IPAddress.Loopback]));
        Assert.False(isValid);
        Assert.Contains("blocked", error);
    }

    [Fact]
    public async Task ValidateUrlAsync_Rejects_CloudMetadata()
    {
        var (isValid, error) = await PrefixSourceUrlValidator.ValidateUrlAsync(
            "http://169.254.169.254/latest/meta-data/",
            (_, _) => ValueTask.FromResult<IPAddress[]>([IPAddress.Parse("169.254.169.254")]));
        Assert.False(isValid);
        Assert.Contains("blocked", error);
    }

    [Fact]
    public async Task ValidateUrlAsync_Accepts_PublicHost()
    {
        var (isValid, error) = await PrefixSourceUrlValidator.ValidateUrlAsync(
            "https://example.com/list.txt",
            (_, _) => ValueTask.FromResult<IPAddress[]>([IPAddress.Parse("93.184.216.34")]));
        Assert.True(isValid);
        Assert.Null(error);
    }

    [Fact]
    public async Task ValidateUrlAsync_Rejects_NonHttpScheme()
    {
        var (isValid, error) = await PrefixSourceUrlValidator.ValidateUrlAsync(
            "ftp://example.com/list.txt");
        Assert.False(isValid);
        Assert.Contains("scheme", error);
    }

    [Fact]
    public async Task ValidateUrlAsync_Rejects_MalformedUrl()
    {
        var (isValid, error) = await PrefixSourceUrlValidator.ValidateUrlAsync("not-a-url");
        Assert.False(isValid);
        Assert.Contains("Invalid URL", error);
    }
}
