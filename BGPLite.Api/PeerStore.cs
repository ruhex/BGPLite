using BGPLite.Api.Entities;
using BGPLite.Server;
using Microsoft.EntityFrameworkCore;

namespace BGPLite.Api;

public sealed class PeerStore : IPeerStore
{
    private readonly IDbContextFactory<BgpDbContext> _dbFactory;

    public PeerStore(IDbContextFactory<BgpDbContext> dbFactory) => _dbFactory = dbFactory;

    public string CreatePeer(string ip, uint asn, string? description)
    {
        using var db = _dbFactory.CreateDbContext();
        var existing = db.Peers.FirstOrDefault(p => p.Ip == ip && p.Asn == asn);
        if (existing is not null)
        {
            existing.Description = description;
            db.SaveChanges();
            return existing.Id;
        }

        var peer = new Peer { Ip = ip, Asn = asn, Description = description };
        db.Peers.Add(peer);
        db.SaveChanges();
        return peer.Id;
    }

    public void UpsertPeer(string ip, uint asn)
    {
        using var db = _dbFactory.CreateDbContext();
        var peer = db.Peers.FirstOrDefault(p => p.Ip == ip && p.Asn == asn);
        if (peer is null)
        {
            db.Peers.Add(new Peer { Ip = ip, Asn = asn, Status = "active", LastSessionAt = DateTime.UtcNow });
        }
        else
        {
            peer.Status = "active";
            peer.LastSessionAt = DateTime.UtcNow;
        }
        db.SaveChanges();
    }

    public void UpdateSessionStatus(string ip, uint asn, bool active)
    {
        using var db = _dbFactory.CreateDbContext();
        var peer = db.Peers.FirstOrDefault(p => p.Ip == ip && p.Asn == asn);
        if (peer is null) return;

        peer.Status = active ? "active" : "inactive";
        if (active) peer.LastSessionAt = DateTime.UtcNow;
        db.SaveChanges();
    }

    public void DeletePeer(string id)
    {
        using var db = _dbFactory.CreateDbContext();
        db.Peers.Where(p => p.Id == id).ExecuteDelete();
    }

    public List<Peer> GetAllPeers()
    {
        using var db = _dbFactory.CreateDbContext();
        return db.Peers.AsNoTracking().Include(p => p.Communities).ToList();
    }

    public Peer? GetDbPeerById(string id)
    {
        using var db = _dbFactory.CreateDbContext();
        return db.Peers.AsNoTracking().Include(p => p.Communities).FirstOrDefault(p => p.Id == id);
    }

    PeerInfo? IPeerStore.GetPeerById(string id)
    {
        using var db = _dbFactory.CreateDbContext();
        var peer = db.Peers.AsNoTracking().Include(p => p.Communities).FirstOrDefault(p => p.Id == id);
        return peer is null ? null : MapToInfo(peer);
    }

    public PeerInfo? GetPeerByIp(string ip)
    {
        using var db = _dbFactory.CreateDbContext();
        var peer = db.Peers.AsNoTracking().Include(p => p.Communities).FirstOrDefault(p => p.Ip == ip);
        return peer is null ? null : MapToInfo(peer);
    }

    /// <summary>
    /// Resolves a peer by its durable identity <c>(Ip, Asn)</c> — the form a BGP session knows once
    /// it has parsed the peer's OPEN (issue #19). Several peers may share a source IP with distinct
    /// AS; this returns the specific one, unlike the Ip-only <see cref="GetPeerByIp"/>.
    /// </summary>
    public PeerInfo? GetPeer(string ip, uint asn)
    {
        using var db = _dbFactory.CreateDbContext();
        var peer = db.Peers.AsNoTracking().Include(p => p.Communities).FirstOrDefault(p => p.Ip == ip && p.Asn == asn);
        return peer is null ? null : MapToInfo(peer);
    }

    /// <summary>
    /// Single-roundtrip replacement for the <c>GetPeer</c> + <c>UpdateSessionStatus</c> +
    /// <c>GetSubscriptions</c> + <c>GetCustomPrefixes</c> + <c>GetCustomAsns</c> sequence the BGP
    /// send path used to issue as FIVE separate <c>DbContext</c>s (issue #84). Loads the peer by
    /// <c>(Ip, Asn)</c> with the three routing-relevant child collections <c>Include</c>'d in ONE
    /// <c>AsNoTracking</c> query (read-only intent, consistent with the other getters), then folds
    /// the "session active" status write into the SAME <c>DbContext</c> via <c>ExecuteUpdate</c> —
    /// so the whole read+update is one connection (six statements on five connections → two
    /// statements on one). The returned collection shapes match the standalone getters exactly.
    /// </summary>
    public PeerRoutingView? LoadPeerRoutingView(string ip, uint asn)
    {
        using var db = _dbFactory.CreateDbContext();
        var peer = db.Peers.AsNoTracking()
            .Include(p => p.Subscriptions)
            .Include(p => p.CustomPrefixes)
            .Include(p => p.CustomAsns)
            .FirstOrDefault(p => p.Ip == ip && p.Asn == asn);
        if (peer is null) return null;

        // Fold the status update (was UpdateSessionStatus(active:true) on its own DbContext) into
        // this one. ExecuteUpdate is scoped by (Ip, Asn) — identical effect, no extra connection.
        db.Peers.Where(p => p.Ip == ip && p.Asn == asn)
            .ExecuteUpdate(s => s
                .SetProperty(p => p.Status, "active")
                .SetProperty(p => p.LastSessionAt, DateTime.UtcNow));

        return new PeerRoutingView(
            peer.Id,
            peer.Subscriptions.Select(s => s.AsnListName).ToList(),
            peer.CustomPrefixes.Select(c => c.Prefix + "/" + c.PrefixLength).ToList(),
            peer.CustomAsns.Select(c => c.Asn).ToList());
    }

    public void SetDescription(string id, string description)
    {
        using var db = _dbFactory.CreateDbContext();
        db.Peers.Where(p => p.Id == id).ExecuteUpdate(
            s => s.SetProperty(p => p.Description, description));
    }

    // AsNoTracking is a read-only-intent marker here — a no-op for this scalar projection (no entities are materialized to track).
    public HashSet<uint> GetCommunities(string peerId)
    {
        using var db = _dbFactory.CreateDbContext();
        return db.Peers.AsNoTracking().Include(p => p.Communities)
            .Where(p => p.Id == peerId)
            .SelectMany(p => p.Communities)
            .Select(c => (uint)c.Community)
            .ToHashSet();
    }

    // AsNoTracking is a read-only-intent marker here — a no-op for this scalar projection (no entities are materialized to track).
    public HashSet<uint> GetCommunities(string ip, uint asn)
    {
        using var db = _dbFactory.CreateDbContext();
        return db.Peers.AsNoTracking().Include(p => p.Communities)
            .Where(p => p.Ip == ip && p.Asn == asn)
            .SelectMany(p => p.Communities)
            .Select(c => (uint)c.Community)
            .ToHashSet();
    }

    // AsNoTracking is a read-only-intent marker here — a no-op for this scalar projection (no entities are materialized to track).
    public HashSet<uint> GetCommunitiesByIp(string ip)
    {
        using var db = _dbFactory.CreateDbContext();
        return db.Peers.AsNoTracking().Include(p => p.Communities)
            .Where(p => p.Ip == ip)
            .SelectMany(p => p.Communities)
            .Select(c => (uint)c.Community)
            .ToHashSet();
    }

    public void SetCommunities(string peerId, HashSet<uint> communities)
    {
        using var db = _dbFactory.CreateDbContext();
        db.Set<PeerCommunity>().Where(c => c.PeerId == peerId).ExecuteDelete();
        db.Set<PeerCommunity>().AddRange(
            communities.Select(c => new PeerCommunity { PeerId = peerId, Community = c }));
        db.SaveChanges();
    }

    public void ClearCommunities(string peerId)
    {
        using var db = _dbFactory.CreateDbContext();
        db.Set<PeerCommunity>().Where(c => c.PeerId == peerId).ExecuteDelete();
    }

    // AsNoTracking is a read-only-intent marker here — a no-op for this scalar projection (no entities are materialized to track).
    public List<string> GetSubscriptions(string peerId)
    {
        using var db = _dbFactory.CreateDbContext();
        return db.Set<PeerSubscription>().AsNoTracking()
            .Where(s => s.PeerId == peerId)
            .Select(s => s.AsnListName)
            .ToList();
    }

    public void SetSubscriptions(string peerId, List<string> asnListNames)
    {
        using var db = _dbFactory.CreateDbContext();
        db.Set<PeerSubscription>().Where(s => s.PeerId == peerId).ExecuteDelete();
        db.Set<PeerSubscription>().AddRange(
            asnListNames.Select(n => new PeerSubscription { PeerId = peerId, AsnListName = n }));
        db.SaveChanges();
    }

    // AsNoTracking is a read-only-intent marker here — a no-op for this scalar projection (no entities are materialized to track).
    public List<string> GetCustomPrefixes(string peerId)
    {
        using var db = _dbFactory.CreateDbContext();
        return db.Set<PeerCustomPrefix>().AsNoTracking()
            .Where(c => c.PeerId == peerId)
            .Select(c => c.Prefix + "/" + c.PrefixLength)
            .ToList();
    }

    public void SetCustomPrefixes(string peerId, List<(string Prefix, byte Length)> prefixes)
    {
        using var db = _dbFactory.CreateDbContext();
        db.Set<PeerCustomPrefix>().Where(c => c.PeerId == peerId).ExecuteDelete();
        db.Set<PeerCustomPrefix>().AddRange(
            prefixes.Select(p => new PeerCustomPrefix { PeerId = peerId, Prefix = p.Prefix, PrefixLength = p.Length }));
        db.SaveChanges();
    }

    // AsNoTracking is a read-only-intent marker here — a no-op for this scalar projection (no entities are materialized to track).
    public List<uint> GetCustomAsns(string peerId)
    {
        using var db = _dbFactory.CreateDbContext();
        return db.Set<PeerCustomAsn>().AsNoTracking()
            .Where(c => c.PeerId == peerId)
            .Select(c => c.Asn)
            .ToList();
    }

    public void SetCustomAsns(string peerId, List<uint> asns)
    {
        using var db = _dbFactory.CreateDbContext();
        db.Set<PeerCustomAsn>().Where(c => c.PeerId == peerId).ExecuteDelete();
        db.Set<PeerCustomAsn>().AddRange(
            asns.Select(a => new PeerCustomAsn { PeerId = peerId, Asn = a }));
        db.SaveChanges();
    }

    private static PeerInfo MapToInfo(Peer peer) => new()
    {
        Id = peer.Id,
        Ip = peer.Ip,
        Asn = peer.Asn,
        Description = peer.Description,
        Status = peer.Status,
        CreatedAt = peer.CreatedAt.ToString("O"),
        LastSessionAt = peer.LastSessionAt?.ToString("O")
    };
}
