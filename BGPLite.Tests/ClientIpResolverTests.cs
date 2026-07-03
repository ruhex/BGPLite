using System.Net;
using BGPLite.Api;

namespace BGPLite.Tests;

/// <summary>
/// Unit tests for <see cref="ManagementApi.ResolveClientIp"/> (#91): forwarding headers are honored
/// only when the immediate peer is a configured trusted proxy, and X-Forwarded-For is walked
/// right-to-left past trusted hops so client injection on the left is defeated.
/// </summary>
public class ClientIpResolverTests
{
    private static readonly IPNetwork[] Proxy =
    [
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8")
    ];

    [Fact]
    public void DirectClient_Ignores_XForwardedFor() =>
        Assert.Equal("203.0.113.9",
            ManagementApi.ResolveClientIp(IPAddress.Parse("203.0.113.9"), "198.51.100.5", null, Proxy));

    [Fact]
    public void DirectClient_Ignores_XRealIp() =>
        Assert.Equal("203.0.113.9",
            ManagementApi.ResolveClientIp(IPAddress.Parse("203.0.113.9"), null, "198.51.100.5", Proxy));

    [Fact]
    public void TrustedProxy_Uses_XForwardedFor_Client() =>
        Assert.Equal("198.51.100.5",
            ManagementApi.ResolveClientIp(IPAddress.Parse("127.0.0.1"), "198.51.100.5", null, Proxy));

    [Fact]
    public void TrustedProxy_LeftInjection_Is_Defeated()
    {
        // Attacker spoofs a left entry; the proxy appends the real client on the right.
        // Right-to-left walk returns the rightmost untrusted hop = the real client.
        Assert.Equal("198.51.100.5",
            ManagementApi.ResolveClientIp(IPAddress.Parse("127.0.0.1"), "spoofed, 198.51.100.5", null, Proxy));
    }

    [Fact]
    public void TrustedProxy_Walks_Past_Trusted_Intermediate()
    {
        // client -> trusted proxy (10.x) -> us (127.x). XFF = "client, 10.0.0.1" — 10.0.0.1 is
        // trusted (skipped), so the client is returned.
        Assert.Equal("198.51.100.5",
            ManagementApi.ResolveClientIp(IPAddress.Parse("127.0.0.1"), "198.51.100.5, 10.0.0.1", null, Proxy));
    }

    [Fact]
    public void TrustedProxy_FallsBack_To_XRealIp() =>
        Assert.Equal("198.51.100.5",
            ManagementApi.ResolveClientIp(IPAddress.Parse("127.0.0.1"), null, "198.51.100.5", Proxy));

    [Fact]
    public void TrustedProxy_Malformed_XRealIp_FallsBack_To_Remote()
    {
        // A garbage X-Real-IP (e.g. with log-forging newlines) must not be returned verbatim.
        Assert.Equal("127.0.0.1",
            ManagementApi.ResolveClientIp(IPAddress.Parse("127.0.0.1"), null, "not-an-ip\nFAKE", Proxy));
    }

    [Fact]
    public void NoTrustedProxies_Always_Returns_Remote() =>
        Assert.Equal("203.0.113.9",
            ManagementApi.ResolveClientIp(IPAddress.Parse("203.0.113.9"), "198.51.100.5", "1.2.3.4", Array.Empty<IPNetwork>()));

    [Fact]
    public void Null_Remote_Returns_Unknown() =>
        Assert.Equal("unknown",
            ManagementApi.ResolveClientIp(null, "198.51.100.5", null, Proxy));

    [Fact]
    public void IPv4MappedIPv6_Remote_Normalized_For_TrustCheck()
    {
        // Linux dual-stack HttpListener (http://+) reports IPv4 peers as ::ffff:x.x.x.x; the
        // address must be normalized to IPv4 before matching against IPv4 trusted-proxy CIDRs,
        // else the proxy is never trusted and XFF is ignored (CodeRabbit).
        var mapped = IPAddress.Parse("::ffff:127.0.0.1");
        Assert.True(mapped.IsIPv4MappedToIPv6);
        Assert.Equal("198.51.100.5",
            ManagementApi.ResolveClientIp(mapped, "198.51.100.5", null, Proxy));
    }
}
