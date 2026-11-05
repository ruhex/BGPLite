using BGPLite.Protocol;

namespace BGPLite.Tests;

/// <summary>
/// Unit tests for the canonical <see cref="PrefixCidr"/> parser — the single policy point
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
    /// Host bits are masked to the network address. <c>10.0.0.5/24</c> normalizes to
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
    /// <c>/0</c> (the default route) is REJECTED by default — a route server must not originate
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
    // ---- family-aware parse (IPv6) ----

    [Fact]
    public void TryParse_Ipv6_AcceptsAndMasks()
    {
        Assert.True(PrefixCidr.TryParse("2001:db8:ffff:ffff:1::/48", out var prefix));
        Assert.False(prefix.IsIpv4);
        Assert.Equal(48, prefix.Length);
        var expected = ((UInt128)0x2001 << 112) | ((UInt128)0x0DB8 << 96) | ((UInt128)0xFFFF << 80);
        Assert.Equal(expected, prefix.Address);
    }

    [Fact]
    public void TryParse_Ipv6_HostRoute128()
    {
        Assert.True(PrefixCidr.TryParse("2001:db8::1/128", out var prefix));
        Assert.False(prefix.IsIpv4);
        Assert.Equal(128, prefix.Length);
    }

    [Theory]
    [InlineData("2001:db8::/129")]   // out of range
    [InlineData("::/0")]             // default route rejected like 0.0.0.0/0
    [InlineData("2001:db8::")]       // no length
    [InlineData("::ffff:10.0.0.1/96")] // v4-mapped — not a real IPv6 prefix
    public void TryParse_Ipv6_Rejects(string bad)
    {
        Assert.False(PrefixCidr.TryParse(bad, out var prefix));
        Assert.Equal(default, prefix);
    }

    // ---- non-canonical IPv4 literals (#545 item 1) ----

    /// <summary>
    /// <see cref="System.Net.IPAddress.TryParse"/> honours the inet_aton grammar and does not
    /// reject those forms — it rewrites them into a DIFFERENT network. Left lenient here,
    /// <c>010.0.0.0/8</c> was accepted as 8.0.0.0/8 (a leading zero turns the octet octal), so a
    /// zero-padded line in <c>nets.txt</c>, in a peer's URL source, or in an API custom prefix made
    /// the route server originate somebody else's address space to every subscribed peer — with no
    /// rejection and no log line on the file/URL and RIPEstat paths, because the line is not
    /// malformed to the parser, merely transformed. Each input below is the operator's intent
    /// mis-parsed, not garbage, which is exactly why it has to be rejected rather than normalized.
    /// </summary>
    [Theory]
    [InlineData("010.0.0.0/8")]        // octal octet → 8.0.0.0/8 (a Level 3 block)
    [InlineData("192.168.000.001/24")] // zero-padded throughout
    [InlineData("0x7f.0.0.1/32")]      // hex octet → 127.0.0.1
    [InlineData("0xC0A80000/24")]      // single hex word → 192.168.0.0
    [InlineData("172.16/12")]          // two-part short form → 172.0.0.16
    [InlineData("192.168.1/24")]       // three-part short form → 192.168.0.1
    [InlineData("1.2/32")]             // two-part short form → 1.0.0.2
    [InlineData("2130706433/32")]      // bare 32-bit integer → 127.0.0.1
    [InlineData("1.2.3.4.5/24")]       // five octets
    [InlineData("1.2.3./24")]          // trailing dot
    [InlineData(".1.2.3/24")]          // leading dot
    [InlineData("1..2.3/24")]          // empty octet
    [InlineData("1.2.3.+4/24")]        // signed octet
    [InlineData("1.2.3.0400/24")]      // over-long octet
    public void TryParse_Rejects_NonCanonical_Ipv4_Literals(string cidr)
    {
        // Family-aware overload (file sources, RIPEstat, URL sources).
        Assert.False(PrefixCidr.TryParse(cidr, out var ipPrefix));
        Assert.Equal(default, ipPrefix);

        // IPv4-only overload (management API custom prefixes, RouteAssembler's DB re-parse).
        Assert.False(PrefixCidr.TryParse(cidr, out var packed, out var length));
        Assert.Equal(0u, packed);
        Assert.Equal((byte)0, length);
    }

    /// <summary>
    /// The canonical forms the gate must keep accepting — including the boundary octets a strict
    /// dotted-quad check is easiest to get wrong on (a bare <c>0</c> octet is canonical; <c>00</c>
    /// is not, and 255 must not be read as out of range).
    /// </summary>
    [Theory]
    [InlineData("0.0.0.0/8", 0x00000000u)]
    [InlineData("10.0.0.0/8", 0x0A000000u)]
    [InlineData("255.255.255.255/32", 0xFFFFFFFFu)]
    [InlineData("100.64.0.0/10", 0x64400000u)]
    [InlineData("203.0.113.0/24", 0xCB007100u)]
    public void TryParse_Still_Accepts_Canonical_Ipv4(string cidr, uint expected)
    {
        Assert.True(PrefixCidr.TryParse(cidr, out var packed, out _));
        Assert.Equal(expected, packed);

        Assert.True(PrefixCidr.TryParse(cidr, out var ipPrefix));
        Assert.True(ipPrefix.IsIpv4);
        Assert.Equal(expected, (uint)ipPrefix.Address);
    }

    /// <summary>IPv6 literal parsing is already strict, so the IPv4 gate must not touch it —
    /// including the compressed and mixed-case forms a dotted-quad check could trip over.</summary>
    [Theory]
    [InlineData("2001:DB8::/32")]
    [InlineData("2001:db8:0:0:0:0:0:1/128")]
    [InlineData("fc00::/7")]
    public void TryParse_Ipv6_Unaffected_By_The_Ipv4_Gate(string cidr)
    {
        Assert.True(PrefixCidr.TryParse(cidr, out var prefix));
        Assert.False(prefix.IsIpv4);
    }

    [Fact]
    public void TryParse_Ipv4OnlyOverload_StillRejectsIpv6()
    {
        // The API's custom-prefix path stays IPv4-scoped for now; the v4 overload must not
        // silently widen with the family-aware parser arriving alongside it.
        Assert.False(PrefixCidr.TryParse("2001:db8::/48", out var prefix, out var length));
        Assert.Equal(0u, prefix);
        Assert.Equal((byte)0, length);
    }
}
