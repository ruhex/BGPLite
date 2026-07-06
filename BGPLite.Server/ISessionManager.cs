namespace BGPLite.Server;

public interface ISessionManager
{
    /// <summary>
    /// Refreshes the route set for the peer identified by (ip, asn). When several peers share one
    /// source IP (NAT/VPN), only the session matching BOTH fields is refreshed (#200).
    /// </summary>
    Task RefreshPeerAsync(string peerIp, uint asn);
    List<string> GetActivePeerIps();
}
