namespace BGPLite.Contracts;

/// <summary>
/// The persistence surface the BGP session layer consumes (issues #262, #230). Every member is
/// asynchronous — the store is invoked from session threads and the route-send path, and a sync
/// EF call there blocks a thread-pool thread for the duration of any SQLite <c>busy_timeout</c>
/// wait. The management API works with the concrete <c>PeerStore</c> (its surface is an API
/// concern, not a session-layer contract) and stays out of this interface on purpose.
/// </summary>
public interface IPeerStore
{
    Task<string> CreatePeerAsync(string ip, uint asn, string? description, CancellationToken ct = default);
    Task UpsertPeerAsync(string ip, uint asn, CancellationToken ct = default);
    Task UpdateSessionStatusAsync(string ip, uint asn, bool active, CancellationToken ct = default);
    Task<PeerRoutingView?> LoadPeerRoutingViewAsync(string ip, uint asn, CancellationToken ct = default);
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
/// A per-peer user-supplied URL prefix-list source (epic #143 / issue #147), projected from
/// <c>PeerCustomSource</c>. Only <c>Active</c> sources appear here — paused sources never leave the DB.
/// <c>Community</c> is the raw user-supplied <c>"ASN:VALUE"</c> override, or null for auto-generation.
/// </summary>
public sealed record CustomSourceView(string Name, string Url, string? Community);

/// <summary>
/// The slice of peer data the BGP send path consumes, loaded in one query for issue #84.
/// Field shapes are identical to the standalone getters so the caller behavior is unchanged:
/// <c>Subscriptions</c> = <c>GetSubscriptions</c>, <c>CustomPrefixes</c> = <c>"prefix/length"</c>
/// strings like <c>GetCustomPrefixes</c>, <c>CustomAsns</c> = <c>GetCustomAsns</c>. <c>UserSources</c>
/// (issue #147) holds only the peer's active URL sources, fetched and advertised per-peer.
/// </summary>
public sealed record PeerRoutingView(
    string PeerId,
    List<string> Subscriptions,
    List<string> CustomPrefixes,
    List<uint> CustomAsns,
    List<CustomSourceView> UserSources);
