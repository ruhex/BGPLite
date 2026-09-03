using System.Net;
using System.Net.Sockets;

namespace BGPLite.Protocol;

/// <summary>
/// The single canonical CIDR parser for the project (#236). Every input path — operator file
/// sources (<c>PrefixListParser</c>), the management API (<c>ParseCustomPrefix</c>), and the
/// BGP send path's DB-load (<c>RouteAssembler</c>) — routes through one of the <see cref="TryParse"/>
/// overloads so the validation and masking policy is defined in exactly one place. Three divergent
/// parsers previously caused dedup/aggregation breakage (the same network submitted two ways was
/// stored as two distinct keys) and a route-leak vector (the API accepted <c>/0</c>, the default route).
/// <para>
/// <b>Policy</b> (locked down here, recorded in #236):
/// <list type="bullet">
/// <item><b>Host-bit masking is ALWAYS applied</b> — <c>10.0.0.5/24</c> normalizes to
/// <c>10.0.0.0/24</c>. Without this the route table, the aggregator, and the duplicate-NLRI
/// merger key on the raw form and treat the two forms as distinct networks.</item>
/// <item><b>Length range is 1..32 (IPv4) / 1..128 (IPv6)</b>. <c>/0</c> (the default route) is
/// rejected for user-supplied sources — a route server must not originate a default.
/// <c>allowDefault: true</c> permits <c>/0</c> for the rare operator-config default-route case
/// (kept for completeness; no caller currently uses it).</item>
/// <item><b>Family</b>: the <see cref="TryParse(string?, out IpPrefix, bool)"/> overload accepts
/// both families (#14 phase 4); the <see cref="TryParse(string?, out uint, out byte, bool)"/>
/// overload stays IPv4-only — the management API's custom-prefix path is still IPv4-scoped.</item>
/// <item><b>Garbage is rejected, never thrown.</b> Returns <c>false</c> on any malformed input so
/// callers can surface a clean 400 / skip the line, rather than propagating a <c>FormatException</c>.</item>
/// </list>
/// </para>
/// </summary>
public static class PrefixCidr
{
    /// <summary>
    /// Parses a CIDR string into a family-aware <see cref="IpPrefix"/> (IPv4 1..32, IPv6 1..128),
    /// applying the canonical policy (host-bit masking, length range). Returns <c>false</c> (with
    /// <paramref name="prefix"/> zeroed) on any malformed or out-of-policy input — callers should
    /// report/skip, not throw.
    /// </summary>
    /// <param name="cidr">The CIDR string to parse ("a.b.c.d/len" or "2001:db8::/48").</param>
    /// <param name="prefix">On success, the masked network prefix (family carried by <see cref="IpPrefix.IsIpv4"/>).</param>
    /// <param name="allowDefault">When <c>true</c>, accept <c>/0</c> (the default route). Default
    /// <c>false</c> — user-supplied sources (API/file/URL) must not originate a default.</param>
    /// <returns><c>true</c> if the string is a valid CIDR within policy; <c>false</c> otherwise.</returns>
    public static bool TryParse(string? cidr, out IpPrefix prefix, bool allowDefault = false)
    {
        prefix = default;

        if (string.IsNullOrWhiteSpace(cidr))
            return false;

        var slash = cidr.IndexOf('/');
        if (slash <= 0 || slash == cidr.Length - 1)
            return false;

        if (!IPAddress.TryParse(cidr[..slash], out var addr))
            return false;

        // IPv4-mapped forms (::ffff:a.b.c.d) are NOT accepted as IPv6 prefixes: they name the
        // IPv4 address space through the v6 syntax and never appear as a legitimate origin
        // prefix — rejecting them here matches the NextHopIpv6 config validation.
        var isIpv4 = addr.AddressFamily == AddressFamily.InterNetwork;
        var isIpv6 = addr.AddressFamily == AddressFamily.InterNetworkV6 && !addr.IsIPv4MappedToIPv6;
        if (!isIpv4 && !isIpv6)
            return false;

        var maxLength = isIpv4 ? 32 : 128;
        if (!byte.TryParse(cidr[(slash + 1)..], out var len) || len > maxLength)
            return false;
        if (len == 0 && !allowDefault)
            return false;

        // The IpPrefix constructor canonicalizes: host bits masked to the network address,
        // family stored explicitly (IPv4 in the low 32 bits, IPv6 as the full 128).
        prefix = new IpPrefix(BgpConstants.ToUInt128(addr), len, isIpv4);
        return true;
    }

    /// <summary>
    /// Parses a CIDR string ("<c>a.b.c.d/len</c>") into a packed IPv4 prefix + mask length, applying
    /// the canonical policy (host-bit masking, length range, IPv4-only). Returns <c>false</c> (with
    /// <paramref name="prefix"/>/<paramref name="length"/> zeroed) on any malformed or out-of-policy
    /// input — callers should report/skip, not throw.
    /// </summary>
    /// <param name="cidr">The CIDR string to parse.</param>
    /// <param name="prefix">On success, the masked network address as a packed big-endian uint.</param>
    /// <param name="length">On success, the prefix length (0 only when <paramref name="allowDefault"/>
    /// is true; otherwise 1..32).</param>
    /// <param name="allowDefault">When <c>true</c>, accept <c>/0</c> (the default route). Default
    /// <c>false</c> — user-supplied sources (API/file/URL) must not originate a default.</param>
    /// <returns><c>true</c> if the string is a valid IPv4 CIDR within policy; <c>false</c> otherwise.</returns>
    public static bool TryParse(string? cidr, out uint prefix, out byte length, bool allowDefault = false)
    {
        prefix = 0;
        length = 0;

        if (string.IsNullOrWhiteSpace(cidr))
            return false;

        var slash = cidr.IndexOf('/');
        if (slash <= 0 || slash == cidr.Length - 1)
            return false;

        if (!IPAddress.TryParse(cidr[..slash], out var addr) || addr.AddressFamily != AddressFamily.InterNetwork)
            return false; // IPv4 only

        if (!byte.TryParse(cidr[(slash + 1)..], out var len))
            return false;

        var minLen = allowDefault ? 0 : 1;
        if (len < minLen || len > 32)
            return false;

        var packed = BgpConstants.IPAddressToUint(addr);
        // Mask host bits to the network address. For /0 (allowDefault) the shift would be <<32,
        // which is UB-adjacent in C# (shifts are masked to 5 bits → <<0); handle it explicitly so
        // the masked value is 0 (the true 0.0.0.0/0 network), not the packed address.
        var masked = len == 0 ? 0u : packed & (0xFFFFFFFFu << (32 - len));
        prefix = masked;
        length = len;
        return true;
    }
}
