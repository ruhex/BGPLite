using System.Net;
using BGPLite.Protocol;

namespace BGPLite.Tests;

/// <summary>
/// #15 phase 1: the family-aware 128-bit address model. IPv4 lives in the low 32 bits
/// (IsIpv4), IPv6 in the full UInt128; the constructor masks host bits; ToString renders
/// RFC 5952 for IPv6; Afi.IPv6 = 2 and the 128-bit (de)serializers reject wrong-family
/// conversion instead of truncating (resolves #13 at the model level).
/// </summary>
public class IpPrefixModelTests
{
    private static UInt128 V6(string hex) => BgpConstants.ToUInt128(IPAddress.Parse(hex));

    [Fact]
    public void Afi_IPv6_IsTwo()
    {
        Assert.Equal(2, BgpConstants.Afi.IPv6);
        Assert.Equal(1, BgpConstants.Afi.IPv4);
    }

    [Theory]
    [InlineData("::", 0)]
    [InlineData("2001:db8::", 32)]
    [InlineData("2001:db8:1:2::", 64)]        // network address at /64 (host bits masked anyway)
    [InlineData("2001:db8::1", 128)]
    public void V6_Roundtrip_AddrLenString(string text, byte length)
    {
        var addr = IPAddress.Parse(text);
        var prefix = new IpPrefix(addr, length);

        Assert.False(prefix.IsIpv4);
        Assert.Equal($"{text}/{length}", prefix.ToString());
    }

    [Fact]
    public void V4_Behavior_Unchanged()
    {
        var prefix = new IpPrefix((uint)IPAddress.Parse("10.0.0.0").GetAddressBytes()[0] << 24, 8);
        Assert.True(prefix.IsIpv4);
        Assert.Equal("10.0.0.0/8", prefix.ToString());

        var direct = new IpPrefix(IPAddress.Parse("192.168.1.255"), 24);
        Assert.True(direct.IsIpv4);
        Assert.Equal("192.168.1.0/24", direct.ToString()); // host bits masked
    }

    [Fact]
    public void V4_LengthAbove32_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IpPrefix((uint)1, 33));
    }

    [Fact]
    public void V6_LengthAbove128_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IpPrefix(V6("2001:db8::1"), 129, isIpv4: false));
    }

    [Fact]
    public void WrongFamily_Conversion_Throws_NotTruncates()
    {
        // #13 model-level fix: a 128-bit value that does not fit a 32-bit field must throw,
        // never truncate.
        var wide = V6("2001:db8::1");
        var ex = Assert.Throws<InvalidOperationException>(() => BgpConstants.ToUint32OrThrow(wide, "NEXT_HOP"));
        Assert.Contains("wrong address family", ex.Message);

        // A value that fits is convertible.
        Assert.Equal(0xC0A80000u, BgpConstants.ToUint32OrThrow(0xC0A80000u, "v4"));
    }

    [Fact]
    public void ToUInt128_Roundtrips_BothFamilies()
    {
        var v4 = IPAddress.Parse("192.168.1.7");
        var v4Value = BgpConstants.ToUInt128(v4);
        Assert.True(v4Value <= uint.MaxValue);          // low 32 bits only
        Assert.Equal(v4, BgpConstants.FromUInt128(v4Value, isIpv4: true));

        var v6 = IPAddress.Parse("2001:db8::1234:5678");
        var v6Value = BgpConstants.ToUInt128(v6);
        Assert.Equal(v6, BgpConstants.FromUInt128(v6Value, isIpv4: false));
    }

    [Fact]
    public void DualStack_Keys_CannotCollide()
    {
        // The IPv4 low-bits form and a full IPv6 form of the same numeric value must be
        // distinguishable prefixes — the RouteTable key carries IsIpv4 for exactly this reason.
        var v4 = new IpPrefix(0x0A000000u, 8);
        var v6 = new IpPrefix(0x0A000000u, 8, isIpv4: false);

        Assert.NotEqual(v4, v6);
        Assert.False(v4.Equals(v6));
    }

    [Fact]
    public void HostBits_Masked_InConstructor_BothFamilies()
    {
        var v4 = new IpPrefix(0x0A1B2C30u, 24);
        Assert.Equal((UInt128)0x0A1B2C00u, v4.Address);

        var v6 = new IpPrefix(V6("2001:db8::1"), 32, isIpv4: false);
        Assert.Equal(V6("2001:db8::"), v6.Address);
    }
}
