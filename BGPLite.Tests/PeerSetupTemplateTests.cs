using BGPLite.Api;

namespace BGPLite.Tests;

/// <summary>
/// #14 phase 5: peer-setup templates gain IPv6 address-family variants. Peers may be IPv6 hosts
/// (validated at the API boundary), and the peer side needs its own address-family blocks to
/// exchange IPv6 routes with the server.
/// </summary>
public class PeerSetupTemplateTests
{
    [Fact]
    public void CiscoSetupV6_CarriesIpv6AddressFamily()
    {
        var lines = ManagementApi.BuildCiscoSetupV6(65001);
        Assert.Contains(lines, l => l.Trim() == "address-family ipv6 unicast");
        Assert.Contains(lines, l => l.Contains("neighbor <YOUR_IPv6> activate"));
        Assert.Contains(lines, l => l.Contains("remote-as 65001"));
    }

    [Fact]
    public void BirdSetupV6_CarriesIpv6Channel()
    {
        var lines = ManagementApi.BuildBirdSetupV6("2001:db8:cccc::10", 65001, 90);
        Assert.Contains(lines, l => l.Trim() == "ipv6 {");
        Assert.Contains(lines, l => l.Contains("neighbor 2001:db8:cccc::10 as 65001"));
        Assert.Contains(lines, l => l.Contains("hold time 90;"));
    }

    [Fact]
    public void BirdSetupV6_UnconfiguredServer_UsesPlaceholder()
    {
        var lines = ManagementApi.BuildBirdSetupV6(null, 65001, 90);
        Assert.Contains(lines, l => l.Contains("neighbor <SERVER_V6> as 65001"));
    }

    [Fact]
    public void MikrotikSetupV6_CarriesAfiIp6()
    {
        var lines = ManagementApi.BuildMikrotikSetupV6("2001:db8:cccc::10", 65001, 90);
        Assert.Contains(lines, l => l.Contains("afi=ip6"));
        Assert.Contains(lines, l => l.Contains("remote.address=2001:db8:cccc::10/128"));
        Assert.Contains(lines, l => l.Contains("remote.as=65001"));
    }

    [Fact]
    public void MikrotikSetupV6_UnconfiguredServer_UsesPlaceholder()
    {
        var lines = ManagementApi.BuildMikrotikSetupV6(null, 65001, 90);
        Assert.Contains(lines, l => l.Contains("remote.address=<SERVER_V6>/128"));
    }

    // ---- NormalizePeerIp: peers may be IPv6 (#14 phase 5) ----

    [Fact]
    public void NormalizePeerIp_Ipv6_AcceptedAndCanonical()
    {
        Assert.Equal("2001:db8:cccc::20", ManagementApi.NormalizePeerIp("2001:db8:cccc:0:0:0:0:20"));
        // #421: ::1 (loopback) is no longer accepted as a peer address — the compressed-form
        // canonicalization this test pins is covered by a non-special address instead.
        Assert.Equal("2001:db8:cccc::21", ManagementApi.NormalizePeerIp("2001:0db8:cccc:0000:0000:0000:0000:0021"));
    }

    [Fact]
    public void NormalizePeerIp_MappedIpv6_NormalizesToPlainIpv4()
    {
        // A mapped literal names the IPv4 address space — the row must match the address form
        // the dual-mode listener reports.
        Assert.Equal("10.0.0.1", ManagementApi.NormalizePeerIp("::ffff:10.0.0.1"));
    }

    [Fact]
    public void NormalizePeerIp_Ipv4_StillAccepted()
    {
        Assert.Equal("192.168.1.2", ManagementApi.NormalizePeerIp("192.168.1.2"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("not-an-address")]
    [InlineData("2001:db8::1/48")] // a prefix is not a host address
    public void NormalizePeerIp_Garbage_ReturnsNull(string? ip)
    {
        Assert.Null(ManagementApi.NormalizePeerIp(ip));
    }
}
