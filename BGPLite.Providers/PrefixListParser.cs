using System.Net;
using System.Net.Sockets;
using BGPLite.Protocol;

namespace BGPLite.Providers;

/// <summary>
/// Parses a CIDR-per-line text blob into packed IPv4 prefixes.
/// Blank lines and lines starting with <c>#</c> are ignored; malformed and non-IPv4
/// lines are skipped silently (the route table only stores IPv4 <c>(uint, byte)</c> keys).
/// </summary>
/// <remarks>
/// <b>Length and host-bit handling (#162).</b> Prefix length is constrained to the valid IPv4 range
/// 1..32 — a stray <c>0.0.0.0/0</c> line would otherwise advertise the entire IPv4 space (a
/// catastrophic route leak from a peer-supplied URL list, #147). Length 0 is rejected: a route
/// server should not originate a default. Host bits are masked to the network address so that
/// <c>10.0.0.5/24</c> normalizes to <c>10.0.0.0/24</c> — without this, the same network submitted
/// with different host bits is stored as distinct rows and breaks dedup / longest-prefix-match.
/// </remarks>
public static class PrefixListParser
{
    public static IReadOnlyList<(uint Prefix, byte Length)> Parse(string text)
    {
        var result = new List<(uint Prefix, byte Length)>();

        // Strip a leading UTF-8 BOM (\uFEFF): string.Trim() does not remove it (it is not in the
        // Char.IsWhiteSpace set), so without this the first line of a BOM-prefixed list is silently
        // dropped — a common shape for files saved on Windows / some CDNs (#162).
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var slash = line.IndexOf('/');
            if (slash < 0) continue;
            if (!IPAddress.TryParse(line[..slash], out var ip)) continue;
            if (!byte.TryParse(line[(slash + 1)..], out var length)) continue;
            if (ip.AddressFamily != AddressFamily.InterNetwork) continue; // IPv4 only
            // Reject 0 (default route — a route server should not originate one) and > 32 (nonsensical
            // IPv4 length that byte.TryParse accepted). Length 1..32 only.
            if (length is 0 or > 32) continue;

            // Mask host bits to the network address so equivalent prefixes normalize to one key.
            var packed = BgpConstants.IPAddressToUint(ip);
            var masked = packed & (0xFFFFFFFFu << (32 - length));
            result.Add((masked, length));
        }

        return result;
    }
}
