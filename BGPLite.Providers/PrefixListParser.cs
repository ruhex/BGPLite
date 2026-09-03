using BGPLite.Protocol;

namespace BGPLite.Providers;

/// <summary>
/// Parses a CIDR-per-line text blob into family-aware prefixes (IPv4 and IPv6, #14 phase 4).
/// Blank lines and lines starting with <c>#</c> are ignored; malformed lines are skipped
/// silently so one bad line cannot take a whole source down.
/// </summary>
/// <remarks>
/// <b>Length and host-bit handling (#162 / #236).</b> Validation, host-bit masking, and the
/// family handling live in the single canonical <see cref="PrefixCidr"/> parser — every CIDR input
/// path (file, API, DB-load) routes through it so the policy is defined exactly once. <c>/0</c> is
/// rejected (a route server must not originate a default route from a peer-supplied URL list,
/// #147); host bits are masked so <c>10.0.0.5/24</c> normalizes to <c>10.0.0.0/24</c> and dedups
/// correctly; IPv6 entries (<c>2001:db8::/48</c>) are accepted alongside IPv4 ones.
/// </remarks>
public static class PrefixListParser
{
    public static IReadOnlyList<IpPrefix> Parse(string text)
    {
        var result = new List<IpPrefix>();

        // Strip a leading UTF-8 BOM (\uFEFF): string.Trim() does not remove it (it is not in the
        // Char.IsWhiteSpace set), so without this the first line of a BOM-prefixed list is silently
        // dropped — a common shape for files saved on Windows / some CDNs (#162).
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            // Delegate to the canonical parser (#236): host-bit masking, /0 rejection, family
            // handling (IPv4 1..32, IPv6 1..128) — one policy, shared with the API and the BGP
            // send path.
            if (!PrefixCidr.TryParse(line, out var prefix))
                continue;
            result.Add(prefix);
        }

        return result;
    }
}
