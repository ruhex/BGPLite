namespace BGPLite.Configuration;

/// <summary>
/// The single validation point for AS numbers read from configuration. Every config surface that
/// carries an ASN — <c>Bgp.Asn</c>, <c>PrefixSources</c> Kind=asn, <c>RipeStat.AsnLists[].Asns</c>,
/// <c>Peers[].RemoteAsn</c> — routes through <see cref="RequirePositive"/> so the RFC 7607 rule
/// cannot be forgotten when a new surface is added: the check lives here, not at each call site.
/// The management API applies a broader rule of its own on top (AS_TRANS / Last-ASNs are
/// rejected there for peer rows but are not startup blockers for YAML-typed fields).
/// </summary>
public static class AsnValidation
{
    /// <summary>
    /// RFC 7607 §2 reserves AS 0 — an OPEN carrying it must be rejected with Bad Peer AS, and a
    /// configured zero can never participate in a session (nor can a lookup against
    /// <c>ris-prefixes?resource=AS0</c> return usable prefixes). Fails loud with the field path
    /// so the operator knows which YAML key to fix; returns the ASN for call-site chaining.
    /// </summary>
    public static uint RequirePositive(uint asn, string field)
    {
        if (asn == 0)
            throw new InvalidOperationException(
                $"Invalid configuration: {field} must be a positive AS number (RFC 7607 rejects AS 0) (got 0).");
        return asn;
    }
}
