namespace BGPLite.Api.Entities;

/// <summary>
/// A user-supplied URL-based prefix-list source for a peer (#143). The URL points to a CIDR-per-line
/// file; BGPLite fetches it at send time (SendAllRoutesAsync) via HttpPrefixProvider and advertises
/// the prefixes to this peer only. Stored as-is (not parsed at API time).
/// </summary>
public class PeerCustomSource
{
    public string PeerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Community { get; set; }

    public Peer Peer { get; set; } = null!;
}
