using BGPLite.Api;
using BGPLite.Api.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BGPLite.Tests;

/// <summary>
/// Regression coverage for #226 (SetCommunities/SetSubscriptions/SetCustomPrefixes/SetCustomAsns
/// were non-atomic — delete-then-insert without a transaction, leaving an empty collection on a
/// mid-mutation failure) and #227 (CreatePeer/UpsertPeer/UpdateSessionStatus used read-then-write,
/// racing on the composite unique index and throwing DbUpdateException on a concurrent duplicate).
/// Uses a real in-memory SQLite DB so transactions and the unique constraint are actually exercised.
/// </summary>
public class PeerStoreAtomicityTests
{
    private const string Ip = "203.0.113.10";
    private const uint Asn = 64512;

    private static (PeerStore store, SqliteConnection connection) NewStore()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(connection).Options;
        using (var boot = new BgpDbContext(options))
            BgpDbContext.Initialize(boot);
        return (new PeerStore(new StaticOptionsFactory(options)), connection);
    }

    private sealed class StaticOptionsFactory : IDbContextFactory<BgpDbContext>
    {
        private readonly DbContextOptions<BgpDbContext> _options;
        public StaticOptionsFactory(DbContextOptions<BgpDbContext> options) => _options = options;
        public BgpDbContext CreateDbContext() => new(_options);
    }

    private static string SeedPeer(PeerStore store) => store.CreatePeer(Ip, Asn, "test");

    // ---- #226: Set* atomicity ----

    /// <summary>
    /// #226: after SetCommunities replaces the set, exactly the new communities are present — the
    /// delete+insert pair committed atomically, so there is never an empty-collection window.
    /// </summary>
    [Fact]
    public void SetCommunities_Replaces_Atomiclly()
    {
        var (store, connection) = NewStore();
        using var conn = connection;
        var peerId = SeedPeer(store);

        store.SetCommunities(peerId, [0x65001000, 0x65002000]);
        store.SetCommunities(peerId, [0x65003000]);

        var communities = store.GetCommunities(peerId).OrderBy(c => c).ToArray();
        Assert.Equal([0x65003000u], communities);
    }

    [Fact]
    public void SetSubscriptions_Replaces_Atomiclly()
    {
        var (store, connection) = NewStore();
        using var conn = connection;
        var peerId = SeedPeer(store);

        store.SetSubscriptions(peerId, ["list-a", "list-b"]);
        store.SetSubscriptions(peerId, ["list-c"]);

        Assert.Equal(["list-c"], store.GetSubscriptions(peerId));
    }

    [Fact]
    public void SetCustomPrefixes_Replaces_Atomiclly()
    {
        var (store, connection) = NewStore();
        using var conn = connection;
        var peerId = SeedPeer(store);

        store.SetCustomPrefixes(peerId, [("10.0.0.0", (byte)24), ("10.1.0.0", (byte)24)]);
        store.SetCustomPrefixes(peerId, [("192.168.0.0", (byte)16)]);

        Assert.Equal(["192.168.0.0/16"], store.GetCustomPrefixes(peerId));
    }

    [Fact]
    public void SetCustomAsns_Replaces_Atomiclly()
    {
        var (store, connection) = NewStore();
        using var conn = connection;
        var peerId = SeedPeer(store);

        store.SetCustomAsns(peerId, [64512, 64513]);
        store.SetCustomAsns(peerId, [64514]);

        Assert.Equal([64514u], store.GetCustomAsns(peerId));
    }

    /// <summary>
    /// #226: a second replace after the collection was emptied (replace-with-empty) must leave it
    /// empty, then a non-empty replace must populate it again — guards against a regression where
    /// the transaction is dropped and an empty-collection window corrupts the state.
    /// </summary>
    [Fact]
    public void SetCustomPrefixes_Empty_Then_Refill_Roundtrips()
    {
        var (store, connection) = NewStore();
        using var conn = connection;
        var peerId = SeedPeer(store);

        store.SetCustomPrefixes(peerId, [("10.0.0.0", (byte)8)]);
        store.SetCustomPrefixes(peerId, []);
        Assert.Empty(store.GetCustomPrefixes(peerId));

        store.SetCustomPrefixes(peerId, [("172.16.0.0", (byte)12)]);
        Assert.Equal(["172.16.0.0/12"], store.GetCustomPrefixes(peerId));
    }

    /// <summary>
    /// #226 fault-injection: if the INSERT half of the delete+insert fails (here: a duplicate
    /// (PeerId, Prefix, PrefixLength) row that violates the composite PK at the SQLite level), the
    /// transaction MUST roll back — the previously-stored prefixes survive. Without the transaction
    /// wrapper the ExecuteDelete would have committed and the peer would be left with an EMPTY
    /// collection. Drives the regression directly: a fault mid-mutation must not corrupt prior state.
    /// </summary>
    [Fact]
    public void SetCustomPrefixes_RollsBack_On_Insert_Failure()
    {
        var (store, connection) = NewStore();
        using var conn = connection;
        var peerId = SeedPeer(store);
        store.SetCustomPrefixes(peerId, [("10.0.0.0", (byte)24)]);

        // Reproduce the exact delete+insert shape of SetCustomPrefixes, then fault the insert side
        // with a SQLite UNIQUE violation. ExecuteSqlRaw bypasses the EF change tracker (which would
        // otherwise throw InvalidOperationException on a duplicate tracked entity before reaching
        // the DB), so the second INSERT reaches SQLite and fails → the transaction must roll back,
        // leaving the original prefix intact.
        var options = new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(connection).Options;
        using (var db = new BgpDbContext(options))
        {
            using var tx = db.Database.BeginTransaction();
            db.Set<PeerCustomPrefix>().Where(c => c.PeerId == peerId).ExecuteDelete();
            db.Database.ExecuteSqlRaw(
                "INSERT INTO PeerCustomPrefix (PeerId, Prefix, PrefixLength) VALUES ({0}, {1}, {2});",
                peerId, "172.16.0.0", 12);
            // Second raw INSERT with the same composite key → SQLite UNIQUE/PK constraint violation.
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
                db.Database.ExecuteSqlRaw(
                    "INSERT INTO PeerCustomPrefix (PeerId, Prefix, PrefixLength) VALUES ({0}, {1}, {2});",
                    peerId, "172.16.0.0", 12));
            // tx.Dispose without Commit → rollback (the unhandled exception unwinds the using).
        }

        // The original prefix survives — the delete was rolled back together with the failed insert.
        Assert.Equal(["10.0.0.0/24"], store.GetCustomPrefixes(peerId));
    }

    // ---- #227: atomic upsert ----

    /// <summary>
    /// #227: two distinct (Ip, Asn) peers coexist (composite unique index not violated by the
    /// upsert). Guards against the upsert accidentally keying on Ip only.
    /// </summary>
    [Fact]
    public void CreatePeer_DistinctAsns_Coexist()
    {
        var (store, connection) = NewStore();
        using var conn = connection;

        var id1 = store.CreatePeer(Ip, 64512, "a");
        var id2 = store.CreatePeer(Ip, 64513, "b");

        Assert.NotEqual(id1, id2);
        Assert.Equal(2, store.GetPeersByIp(Ip).Count);
    }

    /// <summary>
    /// #227: UpsertPeer sets Status=active and stamps LastSessionAt, both on a fresh insert and on
    /// a second call (update path). Called from the BGP connect path — must never throw.
    /// </summary>
    [Fact]
    public void UpsertPeer_SetsActive_And_StampsLastSessionAt()
    {
        var (store, connection) = NewStore();
        using var conn = connection;

        store.UpsertPeer(Ip, Asn);
        var peer = store.GetPeer(Ip, Asn);
        Assert.NotNull(peer);
        Assert.Equal("active", peer!.Status);
        Assert.NotNull(peer.LastSessionAt);

        var firstSessionAt = peer.LastSessionAt!;
        // Sleep so a regression that stamps LastSessionAt with the OLD value (or a fixed value)
        // would be caught by the comparison below. SQLite stores the timestamp as text in the
        // round-trip "O" format, which sorts lexicographically = chronologically, so a string
        // comparison is a valid ordering check.
        Thread.Sleep(15);

        // Second call (update path) — must refresh LastSessionAt, not throw.
        store.UpsertPeer(Ip, Asn);
        var peer2 = store.GetPeer(Ip, Asn);
        Assert.NotNull(peer2);
        Assert.Equal("active", peer2!.Status);
        Assert.NotNull(peer2.LastSessionAt);
        Assert.True(string.CompareOrdinal(peer2.LastSessionAt, firstSessionAt) >= 0,
            $"LastSessionAt must not go backwards: first={firstSessionAt}, second={peer2.LastSessionAt}");
    }

    /// <summary>
    /// #227: UpdateSessionStatus(true/false) flips Status and stamps LastSessionAt only on
    /// activation. A no-op when the peer does not exist (previously read-then-write would NRE on a
    /// concurrently-deleted peer; now the UPDATE matches 0 rows silently).
    /// </summary>
    [Fact]
    public void UpdateSessionStatus_FlipsStatus_And_Is_NoOp_For_Missing_Peer()
    {
        var (store, connection) = NewStore();
        using var conn = connection;
        store.UpsertPeer(Ip, Asn);

        store.UpdateSessionStatus(Ip, Asn, active: false);
        Assert.Equal("inactive", store.GetPeer(Ip, Asn)!.Status);

        store.UpdateSessionStatus(Ip, Asn, active: true);
        var peer = store.GetPeer(Ip, Asn);
        Assert.Equal("active", peer!.Status);
        Assert.NotNull(peer.LastSessionAt);

        // No-op for a peer that does not exist — must not throw.
        store.UpdateSessionStatus("203.0.113.99", 99999, active: true);
    }
}
