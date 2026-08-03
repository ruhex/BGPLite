using BGPLite.Protocol;

namespace BGPLite.Tests;

/// <summary>
/// Unit tests for the canonical <see cref="PrefixCidr"/> parser (#236) — the single policy point
/// shared by the file source (<c>PrefixListParser</c>), the management API (<c>ParseCustomPrefix</c>),
/// and the BGP send path (<c>RouteAssembler</c>). Covers host-bit masking, the <c>/0</c> default-route
/// rejection (route-leak defense), the length range, IPv4-only, and the <c>allowDefault</c> escape hatch.
/// </summary>
public class PrefixCidrTests
{
    // ---- valid parsing + host-bit masking ----

    [Theory]
    [InlineData("10.0.0.0/8", 0x0A000000u, 8)]
    [InlineData("192.168.1.0/24", 0xC0A80100u, 24)]
    [InlineData("255.255.255.255/32", 0xFFFFFFFFu, 32)] // boundary: single host, no host bits to mask
    [InlineData("1.2.3.4/31", 0x01020304u, 31)]          // /31: host bit is the last bit, kept
    public void TryParse_NetworkAligned_CIDR_Succeeds(string cidr, uint expectedPrefix, byte expectedLength)
    {
        Assert.True(PrefixCidr.TryParse(cidr, out var prefix, out var length));
        Assert.Equal(expectedPrefix, prefix);
        Assert.Equal(expectedLength, length);
    }

    /// <summary>
    /// #236: host bits are masked to the network address. <c>10.0.0.5/24</c> normalizes to
    /// <c>10.0.0.0/24</c> — the canonical form the route table, aggregator, and duplicate-NLRI merger
    /// key on. Without masking the same network submitted two ways is stored as two distinct keys.
    /// </summary>
    [Theory]
    [InlineData("10.0.0.5/24", 0x0A000000u, 24)]   // host bits in last octet
    [InlineData("192.168.1.255/24", 0xC0A80100u, 24)] // all-ones host bits
    [InlineData("172.16.5.1/16", 0xAC100000u, 16)] // host bits in last two octets
    public void TryParse_Masks_Host_Bits(string cidr, uint expectedPrefix, byte expectedLength)
    {
        Assert.True(PrefixCidr.TryParse(cidr, out var prefix, out var length));
        Assert.Equal(expectedPrefix, prefix);
        Assert.Equal(expectedLength, length);
    }

    // ---- /0 default-route handling ----

    /// <summary>
    /// #236: <c>/0</c> (the default route) is REJECTED by default — a route server must not originate
    /// a default from a user-supplied source (file / API / peer URL). This is the route-leak defense
    /// that the old API parser (<c>ParseCustomPrefix</c>) lacked.
    /// </summary>
    [Fact]
    public void TryParse_Rejects_DefaultRoute_ByDefault()
    {
        Assert.False(PrefixCidr.TryParse("0.0.0.0/0", out var prefix, out var length));
        Assert.Equal(0u, prefix);
        Assert.Equal((byte)0, length);
    }

    /// <summary>
    /// <c>allowDefault: true</c> accepts <c>/0</c> and returns the masked 0.0.0.0/0 network (prefix=0).
    /// Kept for the rare operator-config default-route case; no caller uses it today, but the parser
    /// must handle the <<32 shift edge case correctly (it would otherwise be a no-op shift in C#).
    /// </summary>
    [Fact]
    public void TryParse_AllowDefault_Accepts_ZeroPrefix()
    {
        Assert.True(PrefixCidr.TryParse("0.0.0.0/0", out var prefix, out var length, allowDefault: true));
        Assert.Equal(0u, prefix);
        Assert.Equal((byte)0, length);
    }

    /// <summary>
    /// <c>allowDefault</c> with a non-/0 input still masks host bits correctly (the escape hatch does
    /// not disable masking, only the <c>/0</c> rejection).
    /// </summary>
    [Fact]
    public void TryParse_AllowDefault_StillMasks_HostBits_ForNonZero()
    {
        Assert.True(PrefixCidr.TryParse("10.0.0.5/24", out var prefix, out var length, allowDefault: true));
        Assert.Equal(0x0A000000u, prefix);
        Assert.Equal((byte)24, length);
    }

    // ---- length range ----

    [Theory]
    [InlineData("1.2.3.4/33")]   // just over
    [InlineData("1.2.3.4/250")]  // far over — byte.TryParse accepts, range check rejects
    [InlineData("1.2.3.4/255")]  // max byte value
    public void TryParse_Rejects_Length_Over_32(string cidr)
    {
        Assert.False(PrefixCidr.TryParse(cidr, out _, out _));
    }

    [Theory]
    [InlineData("1.2.3.4/1")]    // minimum accepted length (default-policy)
    [InlineData("1.2.3.4/32")]   // maximum accepted length
    public void TryParse_Accepts_Length_Boundary(string cidr)
    {
        Assert.True(PrefixCidr.TryParse(cidr, out _, out var length));
        Assert.True(length >= 1 && length <= 32);
    }

    /// <summary>
    /// <c>byte.TryParse</c> with the default NumberStyles accepts a leading <c>+</c> sign, so
    /// <c>1.2.3.4/+24</c> parses to length 24. This is benign (24 is valid, masking is identical to
    /// <c>/24</c>, dedup is correct), but the behaviour is pinned here so a future tightening to
    /// <c>NumberStyles.None</c> is a visible, tested change rather than a silent contract shift.
    /// </summary>
    [Fact]
    public void TryParse_Accepts_Leading_Plus_Sign_In_Length()
    {
        Assert.True(PrefixCidr.TryParse("1.2.3.4/+24", out var prefix, out var length));
        Assert.Equal((byte)24, length);
        Assert.Equal(0x01020300u, prefix); // masked to 1.2.3.0/24
    }

    // ---- IPv4-only ----

    [Theory]
    [InlineData("::1/128")]       // IPv6 loopback
    [InlineData("2001:db8::/32")] // IPv6 ULA
    [InlineData("fe80::/10")]     // IPv6 link-local
    public void TryParse_Rejects_IPv6(string cidr)
    {
        Assert.False(PrefixCidr.TryParse(cidr, out _, out _));
    }

    // ---- malformed input (never throws) ----

    [Theory]
    [InlineData("")]              // empty
    [InlineData("   ")]           // whitespace
    [InlineData("1.2.3.4")]       // no slash
    [InlineData("1.2.3.4/")]      // slash at end
    [InlineData("/24")]           // empty prefix
    [InlineData("1.2.3.4/24/extra")] // double slash
    [InlineData("not-an-ip/24")]  // non-IP prefix
    [InlineData("1.2.3.4/abc")]   // non-numeric length
    [InlineData("1.2.3.4/-1")]    // negative length (byte.TryParse rejects the '-')
    [InlineData("256.1.1.1/24")]  // out-of-range octet
    public void TryParse_Rejects_Malformed_Without_Throwing(string cidr)
    {
        Assert.False(PrefixCidr.TryParse(cidr, out var prefix, out var length));
        Assert.Equal(0u, prefix);
        Assert.Equal((byte)0, length);
    }

    [Fact]
    public void TryParse_Rejects_Null()
    {
        Assert.False(PrefixCidr.TryParse(null, out var prefix, out var length));
        Assert.Equal(0u, prefix);
        Assert.Equal((byte)0, length);
    }
}
