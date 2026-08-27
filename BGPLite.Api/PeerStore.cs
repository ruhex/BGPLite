using BGPLite.Api.Entities;
using Microsoft.EntityFrameworkCore;
using BGPLite.Contracts;

namespace BGPLite.Api;

public sealed class PeerStore : IPeerStore
{
    private readonly IDbContextFactory<BgpDbContext> _dbFactory;

    public PeerStore(IDbContextFactory<BgpDbContext> dbFactory) => _dbFactory = dbFactory;

    /// <summary>
    /// Creates or upserts the peer row alone. Callers configuring a peer in full should use
    /// <see cref="SavePeerConfiguration"/> instead, so the row and its collections commit together
    /// (#259).
    /// </summary>
    public string CreatePeer(string ip, uint asn, string? description)
    {
        using var db = _dbFactory.CreateDbContext();
        return UpsertPeerRow(db, ip, asn, description);
    }

    /// <summary>
    /// The peer-row upsert, on a caller-supplied <see cref="BgpDbContext"/> so it can participate in
    /// an enclosing transaction (#259) instead of always committing on its own. Behaviour is
    /// unchanged from the #227 implementation this was extracted from.
    /// </summary>
    private static string UpsertPeerRow(BgpDbContext db, string ip, uint asn, string? description)
    {
        // #227: atomic SQLite upsert eliminates the read-then-write race on the composite unique
        // index UX_Peers_Ip_Asn. Two concurrent CreatePeer calls for the same (Ip, Asn) previously
        // both observed `existing is null`, both INSERTed, and the second threw DbUpdateException
        // (UNIQUE constraint failed). Now a single INSERT ... ON CONFLICT DO UPDATE ... RETURNING
        // is fully atomic: the existence check, the insert-or-update, AND the id read-back happen
        // in one statement — no TOCTOU window for a concurrent DeletePeer to race against (the
        // earlier upsert + follow-up SELECT had such a window). RETURNING requires SQLite 3.35+
        // (Microsoft.Data.Sqlite >= 6) — satisfied by the EF Core Sqlite 10 dependency.
        //
        // EF Core's SqlQuery<T> is documented only for SELECT/composable queries, so for a DML
        // statement with RETURNING we go through the underlying ADO.NET connection (ExecuteScalar)
        // — the documented path for a single scalar result from non-composable SQL.
        var id = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.ToString("O");
        var connection = db.Database.GetDbConnection();
        var closeAfter = false;
        try
        {
            if (connection.State != System.Data.ConnectionState.Open)
            {
                db.Database.OpenConnection();
                closeAfter = true;
            }
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO Peers (Id, Ip, Asn, Description, Status, CreatedAt, LastSessionAt) " +
                "VALUES (@id, @ip, @asn, @desc, 'inactive', @now, NULL) " +
                "ON CONFLICT(Ip, Asn) DO UPDATE SET Description = excluded.Description " +
                "RETURNING Id";
            AddParam(cmd, "@id", id);
            AddParam(cmd, "@ip", ip);
            AddParam(cmd, "@asn", (long)asn);
            AddParam(cmd, "@desc", (object?)description ?? DBNull.Value);
            AddParam(cmd, "@now", now);
            var storedId = (string)cmd.ExecuteScalar()!;
            return storedId;
        }
        finally
        {
            if (closeAfter) db.Database.CloseConnection();
        }
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    public void UpsertPeer(string ip, uint asn)
    {
        using var db = _dbFactory.CreateDbContext();
        // #227: atomic upsert — see CreatePeer. UpsertPeer is called from the BGP connect path
        // (Program.cs _onPeerIdentified), where there is no HTTP caller to receive a 409, so the
        // previous read-then-write race could throw DbUpdateException into the session handler.
        var id = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.ToString("O");
        db.Database.ExecuteSqlInterpolated($@"
            INSERT INTO Peers (Id, Ip, Asn, Description, Status, CreatedAt, LastSessionAt)
            VALUES ({id}, {ip}, {asn}, NULL, 'active', {now}, {now})
            ON CONFLICT(Ip, Asn) DO UPDATE SET Status = 'active', LastSessionAt = {now}");
    }

    public void UpdateSessionStatus(string ip, uint asn, bool active)
    {
        using var db = _dbFactory.CreateDbContext();
        // #227: single-statement UPDATE avoids the read-then-write race and is a no-op (0 rows
        // affected) if the peer was concurrently deleted, instead of throwing on a null entity.
        var status = active ? "active" : "inactive";
        var now = DateTime.UtcNow.ToString("O");
        if (active)
        {
            db.Database.ExecuteSqlInterpolated($@"
                UPDATE Peers SET Status = {status}, LastSessionAt = {now}
                WHERE Ip = {ip} AND Asn = {asn}");
        }
        else
        {
            db.Database.ExecuteSqlInterpolated($@"
                UPDATE Peers SET Status = {status}
                WHERE Ip = {ip} AND Asn = {asn}");
        }
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
    /// Returns ALL peers at the given IP (#23). When several peers share one source IP (NAT/VPN),
    /// each is a distinct record with its own Id, subscriptions, and communities. Used by /api/me
    /// to return a multi-peer array when disambiguation is needed.
    /// </summary>
    public List<PeerInfo> GetPeersByIp(string ip)
    {
        using var db = _dbFactory.CreateDbContext();
        return db.Peers.AsNoTracking()
            .Include(p => p.Communities)
            .Where(p => p.Ip == ip)
            .Select(p => MapToInfo(p))
            .ToList();
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
    /// <c>(Ip, Asn)</c> with the four routing-relevant child collections <c>Include</c>'d in ONE
    /// <c>AsNoTracking</c> query (read-only intent, consistent with the other getters), then folds
    /// the "session active" status write into the SAME <c>DbContext</c> via <c>ExecuteUpdate</c> —
    /// so the whole read+update is one connection (six statements on five connections → two
    /// statements on one). The returned collection shapes match the standalone getters exactly.
    /// </summary>
    public PeerRoutingView? LoadPeerRoutingView(string ip, uint asn)
    {
        using var db = _dbFactory.CreateDbContext();

        // AsSplitQuery: without it EF emits ONE statement that LEFT JOINs all four collections, so
        // the driver materializes subs x prefixes x asns x sources rows. Measured on a real SQLite
        // file, 200 iterations: a peer with 3 subscriptions / 200 custom prefixes / 5 ASNs /
        // 2 sources produces 6,000 rows and takes 31 ms per call; 5/1000/10/3 produces 150,000 rows
        // and takes 814 ms. Split, the same reads are 0.33 ms and 1.4 ms — 96x and 596x (#260).
        //
        // This runs on the BGP send path (every session establish, every RefreshRoutesAsync, and
        // RefreshAllEstablishedAsync fires it for all peers at once), so it is the read a user waits
        // on before their selected prefixes reach the wire.
        //
        // The read is wrapped in a transaction because splitting gives up the single-statement
        // consistency #138 relied on: the four SELECTs would otherwise each see their own snapshot,
        // and a UI edit landing between them would advertise a mixed configuration. SQLite runs in
        // WAL here (#95), so a read transaction takes no write lock and does not block writers.
        Peer? peer;
        using (var read = db.Database.BeginTransaction())
        {
            peer = db.Peers.AsNoTracking()
                .Include(p => p.Subscriptions)
                .Include(p => p.CustomPrefixes)
                .Include(p => p.CustomAsns)
                .Include(p => p.CustomSources.Where(c => c.Active))
                .AsSplitQuery()
                .FirstOrDefault(p => p.Ip == ip && p.Asn == asn);
            read.Commit();
        }
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
            peer.CustomAsns.Select(c => c.Asn).ToList(),
            // Only Active user sources are advertised (issue #147); the filtered Include above
            // already excluded paused rows at the SQL level.
            peer.CustomSources
                .Select(c => new CustomSourceView(c.Name, c.Url, c.Community))
                .ToList());
    }

    /// <summary>
    /// Single-DbContext replacement for the <c>GetDbPeerById</c> + <c>GetSubscriptions</c> +
    /// <c>GetCustomPrefixes</c> + <c>GetCustomAsns</c> + <c>GetCustomSources</c> +
    /// <c>GetCommunities</c> sequence the management API's GET endpoints used to issue as 5–6
    /// separate <c>DbContext</c> instances (each opening its own SQLite connection + running the
    /// PRAGMA trio) — issue #228. Loads the peer and ALL its child collections through ONE
    /// read-only <c>DbContext</c> via an EF Core projection.
    /// <para>
    /// This doc previously claimed EF "auto-splits the collection subqueries inside a projection ...
    /// so there is no Cartesian-product row explosion". That is not what EF emits: the projection
    /// produced a single statement LEFT JOINing all five collections, byte-for-byte the same shape
    /// as an Include-based load, and this method carried the full N x M x K x ... explosion since
    /// #228. Verified by dumping the SQL — one SELECT before, six after adding
    /// <c>AsSplitQuery</c> (#260). For a peer with 200 custom prefixes that is 120 ms per call
    /// against 0.17 ms, on the path the management UI hits to render a peer.
    /// </para>
    /// Read-only (<c>AsNoTracking</c>); unlike <see cref="LoadPeerRoutingView"/> it
    /// does NOT fold a status update (the GET path does not mutate). Returns null if the peer does
    /// not exist. Field shapes match the prior standalone getters byte-for-byte.
    /// </summary>
    public PeerDetailDto? GetPeerDetail(string peerId)
    {
        using var db = _dbFactory.CreateDbContext();
        // AsSplitQuery is what makes each collection its own SELECT — a projection alone does not
        // split (#260). Read transaction for the same reason as LoadPeerRoutingView: the six
        // statements must see one snapshot, and in WAL a read transaction blocks nobody.
        using var read = db.Database.BeginTransaction();
        var detail = db.Peers.AsNoTracking()
            .Where(p => p.Id == peerId)
            .Select(p => new PeerDetailDto(
                p.Id,
                p.Ip,
                p.Asn,
                p.Description,
                p.Status,
                p.CreatedAt,
                p.LastSessionAt,
                p.Subscriptions.Select(s => s.AsnListName).ToList(),
                p.CustomPrefixes.Select(c => c.Prefix + "/" + c.PrefixLength).ToList(),
                p.CustomAsns.Select(c => c.Asn).ToList(),
                // CustomSources carries ALL sources (incl. inactive) so the API can show the toggle.
                p.CustomSources
                    .Select(c => new PeerSourceView(c.Id, c.Name, c.Url, c.Community, c.Active))
                    .ToList(),
                // Communities stored as long (PeerCommunity.Community); the API formats to "ASN:VAL".
                p.Communities.Select(c => c.Community).ToList()))
            .AsSplitQuery()
            .FirstOrDefault();
        read.Commit();
        return detail;
    }

    /// <summary>
    /// Creates (or upserts) a peer and applies its whole configuration in ONE transaction (#259).
    /// <para>
    /// The management API previously chained <c>CreatePeer</c> → <c>SetSubscriptions</c> →
    /// <c>SetCustomPrefixes</c> → <c>SetCustomAsns</c>, each opening its own <c>DbContext</c> and
    /// transaction. #226/#227 made each individual step atomic; the composition was not. A failure
    /// part-way — a duplicate CIDR violating the <c>(PeerId, Prefix, PrefixLength)</c> key is the
    /// reported trigger — returned 500 to the client over an already-committed peer row, leaving a
    /// half-configured peer that the client's retry then had to reconcile. It also opened a window
    /// where a concurrent BGP session read a <c>LoadPeerRoutingView</c> with the subscriptions
    /// applied but not the custom prefixes, and advertised the incomplete set.
    /// </para>
    /// <para>
    /// Every child collection is a set, and all four are keyed on <c>(PeerId, …)</c>, so duplicates
    /// are deduplicated rather than rejected: asking to advertise 10.0.0.0/8 twice means the same
    /// thing as asking once, and a user assembling a list in the UI produces repeats by pasting.
    /// </para>
    /// </summary>
    public string SavePeerConfiguration(
        string ip, uint asn, string? description,
        IReadOnlyList<string> asnListNames,
        IReadOnlyList<(string Prefix, byte Length)> customPrefixes,
        IReadOnlyList<uint> customAsns)
    {
        using var db = _dbFactory.CreateDbContext();
        using var tx = db.Database.BeginTransaction();

        var id = UpsertPeerRow(db, ip, asn, description);
        ReplaceSubscriptions(db, id, asnListNames);
        ReplaceCustomPrefixes(db, id, customPrefixes);
        ReplaceCustomAsns(db, id, customAsns);
        db.SaveChanges();

        tx.Commit();
        return id;
    }

    /// <summary>
    /// Applies a partial peer update in ONE transaction (#259). A <c>null</c> argument means "leave
    /// this alone", matching the PATCH semantics the management API exposes — an empty list means
    /// "clear it", which is a different request and is honoured as such.
    /// </summary>
    public void UpdatePeerConfiguration(
        string peerId, string? description,
        IReadOnlyList<string>? asnListNames,
        IReadOnlyList<(string Prefix, byte Length)>? customPrefixes,
        IReadOnlyList<uint>? customAsns)
    {
        using var db = _dbFactory.CreateDbContext();
        using var tx = db.Database.BeginTransaction();

        if (description is not null)
        {
            db.Peers.Where(p => p.Id == peerId)
                .ExecuteUpdate(s => s.SetProperty(p => p.Description, description));
        }
        if (asnListNames is not null) ReplaceSubscriptions(db, peerId, asnListNames);
        if (customPrefixes is not null) ReplaceCustomPrefixes(db, peerId, customPrefixes);
        if (customAsns is not null) ReplaceCustomAsns(db, peerId, customAsns);
        db.SaveChanges();

        tx.Commit();
    }

    // The Replace* helpers stage a delete + insert on the CALLER's DbContext without committing, so
    // the composite methods above can put every part of a save inside one transaction. The public
    // Set* methods keep their own DbContext + transaction for single-field callers.

    /// <summary>Stages the subscription set, deduplicated — keyed <c>(PeerId, AsnListName)</c>.</summary>
    private static void ReplaceSubscriptions(BgpDbContext db, string peerId, IReadOnlyList<string> names)
    {
        db.Set<PeerSubscription>().Where(s => s.PeerId == peerId).ExecuteDelete();
        db.Set<PeerSubscription>().AddRange(
            names.Distinct(StringComparer.Ordinal)
                 .Select(n => new PeerSubscription { PeerId = peerId, AsnListName = n }));
    }

    /// <summary>Stages the custom-prefix set, deduplicated — keyed <c>(PeerId, Prefix, PrefixLength)</c>.</summary>
    private static void ReplaceCustomPrefixes(BgpDbContext db, string peerId, IReadOnlyList<(string Prefix, byte Length)> prefixes)
    {
        db.Set<PeerCustomPrefix>().Where(c => c.PeerId == peerId).ExecuteDelete();
        db.Set<PeerCustomPrefix>().AddRange(
            prefixes.Distinct()
                    .Select(p => new PeerCustomPrefix { PeerId = peerId, Prefix = p.Prefix, PrefixLength = p.Length }));
    }

    /// <summary>Stages the custom-ASN set, deduplicated — keyed <c>(PeerId, Asn)</c>.</summary>
    private static void ReplaceCustomAsns(BgpDbContext db, string peerId, IReadOnlyList<uint> asns)
    {
        db.Set<PeerCustomAsn>().Where(c => c.PeerId == peerId).ExecuteDelete();
        db.Set<PeerCustomAsn>().AddRange(
            asns.Distinct().Select(a => new PeerCustomAsn { PeerId = peerId, Asn = a }));
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
        // #226: ExecuteDelete runs in its own implicit transaction and AddRange/SaveChanges in
        // another; without an explicit transaction a failure between them leaves the peer with an
        // EMPTY collection (delete committed, insert did not). Wrap both in one transaction so the
        // replace is atomic.
        using var tx = db.Database.BeginTransaction();
        db.Set<PeerCommunity>().Where(c => c.PeerId == peerId).ExecuteDelete();
        db.Set<PeerCommunity>().AddRange(
            communities.Select(c => new PeerCommunity { PeerId = peerId, Community = c }));
        db.SaveChanges();
        tx.Commit();
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
        // #226: wrap delete+insert in a transaction — see SetCommunities.
        using var tx = db.Database.BeginTransaction();
        db.Set<PeerSubscription>().Where(s => s.PeerId == peerId).ExecuteDelete();
        db.Set<PeerSubscription>().AddRange(
            asnListNames.Select(n => new PeerSubscription { PeerId = peerId, AsnListName = n }));
        db.SaveChanges();
        tx.Commit();
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
        // #226: wrap delete+insert in a transaction — see SetCommunities.
        using var tx = db.Database.BeginTransaction();
        db.Set<PeerCustomPrefix>().Where(c => c.PeerId == peerId).ExecuteDelete();
        db.Set<PeerCustomPrefix>().AddRange(
            prefixes.Select(p => new PeerCustomPrefix { PeerId = peerId, Prefix = p.Prefix, PrefixLength = p.Length }));
        db.SaveChanges();
        tx.Commit();
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
        // #226: wrap delete+insert in a transaction — see SetCommunities.
        using var tx = db.Database.BeginTransaction();
        db.Set<PeerCustomAsn>().Where(c => c.PeerId == peerId).ExecuteDelete();
        db.Set<PeerCustomAsn>().AddRange(
            asns.Select(a => new PeerCustomAsn { PeerId = peerId, Asn = a }));
        db.SaveChanges();
        tx.Commit();
    }

    /// <summary>
    /// Lists all user-supplied URL-based prefix-list sources for a peer (#143). Sources are stored as
    /// URLs (not parsed); fetched at send time in SendAllRoutesAsync.
    /// </summary>
    public List<PeerCustomSource> GetCustomSources(string peerId)
    {
        using var db = _dbFactory.CreateDbContext();
        return db.Set<PeerCustomSource>().AsNoTracking()
            .Where(c => c.PeerId == peerId)
            .ToList();
    }

    /// <summary>Adds a URL-based prefix-list source to a peer. Returns the created entity (with Id).</summary>
    public PeerCustomSource AddCustomSource(string peerId, string name, string url, string? community)
    {
        using var db = _dbFactory.CreateDbContext();
        var source = new PeerCustomSource
        {
            PeerId = peerId,
            Name = name,
            Url = url,
            Community = community
        };
        db.Set<PeerCustomSource>().Add(source);
        db.SaveChanges();
        return source;
    }

    /// <summary>Removes a URL-based source by its Id, scoped to a peer. Returns true if found and removed.</summary>
    public bool DeleteCustomSource(string peerId, string sourceId)
    {
        using var db = _dbFactory.CreateDbContext();
        var deleted = db.Set<PeerCustomSource>()
            .Where(c => c.Id == sourceId && c.PeerId == peerId)
            .ExecuteDelete();
        return deleted > 0;
    }

    /// <summary>Toggles a source's active state, scoped to a peer. Returns true if found and updated.</summary>
    public bool SetSourceActive(string peerId, string sourceId, bool active)
    {
        using var db = _dbFactory.CreateDbContext();
        var updated = db.Set<PeerCustomSource>()
            .Where(c => c.Id == sourceId && c.PeerId == peerId)
            .ExecuteUpdate(s => s.SetProperty(c => c.Active, active));
        return updated > 0;
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
