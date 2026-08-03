using BGPLite.Protocol;

namespace BGPLite.Providers;

/// <summary>
/// Parses a CIDR-per-line text blob into packed IPv4 prefixes.
/// Blank lines and lines starting with <c>#</c> are ignored; malformed and non-IPv4
/// lines are skipped silently (the route table only stores IPv4 <c>(uint, byte)</c> keys).
/// </summary>
/// <remarks>
/// <b>Length and host-bit handling (#162 / #236).</b> Validation, host-bit masking, and the IPv4-only
/// constraint now live in the single canonical <see cref="PrefixCidr"/> parser — every CIDR input
/// path (file, API, DB-load) routes through it so the policy is defined exactly once. <c>/0</c> is
/// rejected (a route server must not originate a default route from a peer-supplied URL list, #147);
/// host bits are masked so <c>10.0.0.5/24</c> normalizes to <c>10.0.0.0/24</c> and dedups correctly.
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

            // Delegate to the canonical parser (#236): host-bit masking, /0 rejection, IPv4-only,
            // range 1..32 — one policy, shared with the API and the BGP send path.
            if (!PrefixCidr.TryParse(line, out var prefix, out var length))
                continue;
            result.Add((prefix, length));
        }

        return result;
    }
}
