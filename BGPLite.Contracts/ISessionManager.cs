namespace BGPLite.Contracts;

public interface ISessionManager
{
    /// <summary>
    /// Refreshes the route set for the peer identified by (ip, asn). When several peers share one
    /// source IP (NAT/VPN), only the session matching BOTH fields is refreshed.
    /// </summary>
    Task RefreshPeerAsync(string peerIp, uint asn);
    List<string> GetActivePeerIps();
    /// <summary>Actual advertised prefix count (post-aggregation, post-dedup), or 0.</summary>
    int GetAdvertisedPrefixCount(string peerIp, uint asn);
    /// <summary>Refresh ALL established sessions (unsolicited UPDATE to every peer).</summary>
    Task RefreshAllEstablishedAsync();

    /// <summary>
    /// Terminates all live BGP sessions for the peer identified by (ip, asn): established
    /// sessions are sent exactly one Cease (Administrative Reset) NOTIFICATION, then every matching
    /// session's connection is disposed — the peer's advertised routes are withdrawn by the normal
    /// session teardown. Used by the management API when a peer is deleted, so a deleted peer's
    /// feed stops immediately instead of surviving until the peer disconnects. Sibling sessions
    /// with a different ASN on the same source IP are untouched. The token bounds
    /// the whole Cease phase and is shared across matching sessions — with several sessions, later
    /// ones may be disposed without their Cease completing; sessions are disposed regardless.
    /// </summary>
    Task TerminatePeerAsync(string peerIp, uint asn, CancellationToken ct = default);

    /// <summary>
    /// Terminates all live BGP sessions arriving from ONE source IP, regardless of their ASN.
    /// The IP-only counterpart of <see cref="TerminatePeerAsync"/>: a deleted peer row
    /// with a NULL Asn (legacy Ip-only era rows) cannot be matched by (Ip, Asn) — no live session
    /// ever has RemoteAsn 0 — so without this the teardown was a silent no-op and the session kept
    /// advertising a deleted peer. Same Cease + dispose semantics as <see cref="TerminatePeerAsync"/>.
    /// </summary>
    Task TerminatePeerByIpAsync(string peerIp, CancellationToken ct = default);

    /// <summary>
    /// Sets or clears the TCP-MD5 (RFC 2385) shared key for a peer's source IP. A password
    /// enables enforcement on the listening socket (unsigned segments from that peer are dropped
    /// by the kernel); null/empty disables it. Passwords are never logged. Arming is Linux-only:
    /// passing a non-empty password on any other platform throws
    /// <see cref="PlatformNotSupportedException"/> instead of silently storing an unverifiable key.
    /// A kernel that refuses the key (setsockopt failure — e.g. a Linux build without
    /// CONFIG_TCP_MD5SIG) throws for the same reason: the key is NOT retained, so
    /// <see cref="IsPeerMd5Armed"/> never reports protection the kernel is not enforcing.
    /// </summary>
    void SetPeerMd5Key(string peerIp, string? password);

    /// <summary>
    /// Whether a TCP-MD5 key is currently armed for <paramref name="peerIp"/> — i.e. the kernel
    /// accepted it and will enforce RFC 2385 for that source IP. The control plane's
    /// <c>tcpMd5</c> indicator must be derived from THIS, not from "a password is stored":
    /// a stored password whose arming failed means the peer is unprotected, and reporting it as
    /// protected is the worse half of the failure.
    /// </summary>
    bool IsPeerMd5Armed(string peerIp);
}
