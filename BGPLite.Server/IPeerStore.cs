namespace BGPLite.Server;

public interface IPeerStore
{
    string CreatePeer(string ip, uint asn, string? description);
    void UpsertPeer(string ip, uint asn);
    void UpdateSessionStatus(string ip, uint asn, bool active);
    void DeletePeer(string id);
    PeerInfo? GetPeerByIp(string ip);
    PeerInfo? GetPeer(string ip, uint asn);
    PeerInfo? GetPeerById(string id);
    List<string> GetSubscriptions(string peerId);
    List<string> GetCustomPrefixes(string peerId);
    List<uint> GetCustomAsns(string peerId);
    HashSet<uint> GetCommunities(string peerId);
    void SetCommunities(string peerId, HashSet<uint> communities);
    void ClearCommunities(string peerId);
    void SetDescription(string id, string description);
}

public class PeerInfo
{
    public string Id { get; init; } = "";
    public string Ip { get; init; } = "";
    public uint? Asn { get; init; }
    public string? Description { get; init; }
    public string Status { get; init; } = "inactive";
    public string CreatedAt { get; init; } = "";
    public string? LastSessionAt { get; init; }
}
