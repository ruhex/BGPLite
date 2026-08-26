namespace BGPLite.Api;

/// <summary>
/// Full per-peer read model for the management API's GET endpoints (#228). Replaces the 5–6
/// separate <c>DbContext</c> roundtrips <c>BuildPeerDetail</c>/<c>HandleGetPeer</c> used to issue
/// (one each for the peer row, subscriptions, custom prefixes, custom ASNs, communities, and — for
/// <c>HandleGetPeer</c> — custom sources). Field shapes match the prior standalone getters so the
/// JSON response is byte-identical. <c>CustomSources</c> carries ALL sources (including inactive)
/// so the API can show a source's active toggle state; the send path uses <see cref="PeerRoutingView"/>
/// which filters Active at the SQL level.
/// </summary>

public sealed record PeerSourceView(string Id, string Name, string Url, string? Community, bool Active);

public sealed record PeerDetailDto(
    string Id,
    string Ip,
    uint? Asn,
    string? Description,
    string Status,
    DateTime CreatedAt,
    DateTime? LastSessionAt,
    List<string> Subscriptions,
    List<string> CustomPrefixes,
    List<uint> CustomAsns,
    List<PeerSourceView> CustomSources,
    List<long> Communities);
