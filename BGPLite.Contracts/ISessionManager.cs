namespace BGPLite.Contracts;

public interface ISessionManager
{
    /// <summary>
    /// Refreshes the route set for the peer identified by (ip, asn). When several peers share one
    /// source IP (NAT/VPN), only the session matching BOTH fields is refreshed (#200).
    /// </summary>
    Task RefreshPeerAsync(string peerIp, uint asn);
    List<string> GetActivePeerIps();
    /// <summary>Actual advertised prefix count (post-aggregation, post-dedup), or 0 (#212).</summary>
    int GetAdvertisedPrefixCount(string peerIp, uint asn);
    /// <summary>#214: Refresh ALL established sessions (unsolicited UPDATE to every peer).</summary>
    Task RefreshAllEstablishedAsync();

    /// <summary>
    /// Terminates all live BGP sessions for the peer identified by (ip, asn) (#323): established
    /// sessions are sent exactly one Cease (Administrative Reset) NOTIFICATION, then every matching
    /// session's connection is disposed — the peer's advertised routes are withdrawn by the normal
    /// session teardown. Used by the management API when a peer is deleted, so a deleted peer's
    /// feed stops immediately instead of surviving until the peer disconnects. Sibling sessions
    /// with a different ASN on the same source IP are untouched (#200 semantics). The token bounds
    /// the whole Cease phase and is shared across matching sessions — with several sessions, later
    /// ones may be disposed without their Cease completing; sessions are disposed regardless.
    /// </summary>
    Task TerminatePeerAsync(string peerIp, uint asn, CancellationToken ct = default);
}
