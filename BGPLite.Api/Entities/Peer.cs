namespace BGPLite.Api.Entities;

public class Peer
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Ip { get; set; } = "";
    public uint? Asn { get; set; }
    public string? Description { get; set; }
    public string Status { get; set; } = "inactive";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSessionAt { get; set; }

    /// <summary>Per-peer prefix ceiling (#391). NULL = inherit the global
    /// <c>Bgp.MaxPrefixesPerPeer</c>; explicit 0 = unlimited for this peer.</summary>
    public int? MaxPrefix { get; set; }

    public List<PeerCommunity> Communities { get; set; } = [];
    public List<PeerSubscription> Subscriptions { get; set; } = [];
    public List<PeerCustomPrefix> CustomPrefixes { get; set; } = [];
    public List<PeerCustomAsn> CustomAsns { get; set; } = [];
    public List<PeerCustomSource> CustomSources { get; set; } = [];
}
