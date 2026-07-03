using BGPLite.Api;

namespace BGPLite.Tests;

public class ManagementApiClientIpTests
{
    [Fact]
    public void ResolveClientIp_Prefers_XRealIp()
    {
        var ip = ManagementApi.ResolveClientIp(
            "198.51.100.10",
            "203.0.113.10, 203.0.113.11",
            "10.0.0.1");

        Assert.Equal("198.51.100.10", ip);
    }

    [Fact]
    public void ResolveClientIp_Uses_FirstForwardedForHop()
    {
        var ip = ManagementApi.ResolveClientIp(
            null,
            "198.51.100.10, 203.0.113.11",
            "10.0.0.1");

        Assert.Equal("198.51.100.10", ip);
    }

    [Fact]
    public void ResolveClientIp_FallsBack_ToRemoteEndpoint()
    {
        var ip = ManagementApi.ResolveClientIp(null, null, "10.0.0.1");

        Assert.Equal("10.0.0.1", ip);
    }
}
