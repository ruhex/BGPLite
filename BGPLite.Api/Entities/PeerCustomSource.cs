namespace BGPLite.Api.Entities;

/// <summary>
/// A user-supplied URL-based prefix-list source for a peer (#143). The URL points to a CIDR-per-line
/// file; BGPLite fetches it at send time (SendAllRoutesAsync) via HttpPrefixProvider and advertises
/// the prefixes to this peer only. Stored as-is (not parsed at API time).
/// </summary>
public class PeerCustomSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PeerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Community { get; set; }
    /// <summary>If false (default), the source is stored but NOT fetched at send time (paused).
    /// User must explicitly activate via PATCH. DELETE removes permanently.</summary>
    public bool Active { get; set; } = false;

    public Peer Peer { get; set; } = null!;
}
