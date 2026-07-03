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
    HashSet<uint> GetCommunities(string ip, uint asn);
    void SetCommunities(string peerId, HashSet<uint> communities);
    void ClearCommunities(string peerId);
    void SetDescription(string id, string description);

    /// <summary>
    /// Loads a peer by its durable identity <c>(Ip, Asn)</c> together with the routing-relevant
    /// child data (<see cref="Subscriptions"/>, <see cref="CustomPrefixes"/>, <see cref="CustomAsns"/>)
    /// in a SINGLE query, and folds the "session active" status update (Status="active",
    /// LastSessionAt=now) into the SAME DbContext — so the BGP send path does one read+write
    /// roundtrip instead of the five separate <c>GetPeer</c>/<c>UpdateSessionStatus</c>/
    /// <c>GetSubscriptions</c>/<c>GetCustomPrefixes</c>/<c>GetCustomAsns</c> calls it used to make
    /// (issue #84). Returns <c>null</c> when the peer is unknown (the caller then auto-registers).
    /// The collection shapes match the standalone getters exactly (no behavior change).
    /// </summary>
    PeerRoutingView? LoadPeerRoutingView(string ip, uint asn);
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

/// <summary>
/// The slice of peer data the BGP send path consumes, loaded in one query for issue #84.
/// Field shapes are identical to the standalone getters so the caller behavior is unchanged:
/// <c>Subscriptions</c> = <c>GetSubscriptions</c>, <c>CustomPrefixes</c> = <c>"prefix/length"</c>
/// strings like <c>GetCustomPrefixes</c>, <c>CustomAsns</c> = <c>GetCustomAsns</c>.
/// </summary>
public sealed record PeerRoutingView(
    string PeerId,
    List<string> Subscriptions,
    List<string> CustomPrefixes,
    List<uint> CustomAsns);
