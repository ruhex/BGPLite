namespace BGPLite.Protocol;

/// <summary>
/// Encodes/decodes a BGP well-known community between its <c>"ASN:VALUE"</c> string form
/// and the packed 32-bit representation (<c>(asn &lt;&lt; 16) | value</c>) used across the codebase.
/// </summary>
public static class CommunityCodec
{
    public static uint Parse(string community)
    {
        var colon = community.IndexOf(':');
        if (colon < 0)
            throw new FormatException($"Invalid community '{community}' (expected 'ASN:VALUE').");

        // uint.Parse would surface huge/negative parts as OverflowException, which no caller's
        // FormatException filter catches — TryParse keeps every malformed input a FormatException.
        if (!uint.TryParse(community[..colon], out var asn) || !uint.TryParse(community[(colon + 1)..], out var value))
            throw new FormatException($"Invalid community '{community}': ASN and VALUE must be non-negative integers.");

        if (asn > 0xFFFF)
            throw new FormatException($"Invalid community '{community}': ASN part must be 0-65535 (got {asn}).");
        if (value > 0xFFFF)
            throw new FormatException($"Invalid community '{community}': VALUE part must be 0-65535 (got {value}).");

        return (asn << 16) | value;
    }

    public static string Format(uint community) => $"{community >> 16}:{community & 0xFFFF}";
}
