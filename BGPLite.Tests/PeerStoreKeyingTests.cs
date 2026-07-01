using BGPLite.Api;
using BGPLite.Api.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BGPLite.Tests;

/// <summary>
/// Regression coverage for issue #19: the peer table is keyed by (Ip, Asn), so several distinct
/// peers arriving from the same source IP (different AS) are separate rows with independent
/// subscriptions / prefixes / status. Per RFC 4271 §4.2 the OPEN "My Autonomous System" identifies
/// the sender; a configured neighbor is conventionally (address, ASN). Uses a real in-memory
/// SQLite database so the composite UNIQUE constraint is actually exercised (the EF Core InMemory
/// provider does not enforce unique indexes).
/// </summary>
public class PeerStoreKeyingTests
{
    private const string SharedIp = "203.0.113.10";

    /// <summary>Opens a private in-memory SQLite DB (kept alive by the returned connection, which
    /// the caller must keep open/dispose) and returns a PeerStore over it.</summary>
    private static (PeerStore store, SqliteConnection connection) NewStore()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<BgpDbContext>()
            .UseSqlite(connection)
            .Options;
        using (var boot = new BgpDbContext(options))
            BgpDbContext.Initialize(boot); // EnsureCreated + composite-index migration

        return (new PeerStore(new StaticOptionsFactory(options)), connection);
    }

    private sealed class StaticOptionsFactory : IDbContextFactory<BgpDbContext>
    {
        private readonly DbContextOptions<BgpDbContext> _options;
        public StaticOptionsFactory(DbContextOptions<BgpDbContext> options) => _options = options;
        public BgpDbContext CreateDbContext() => new(_options);
    }

    [Fact]
    public void Distinct_Asns_From_Same_Ip_Create_Separate_Peer_Records()
    {
        var (store, connection) = NewStore();
        using var conn = connection;

        var idA = store.CreatePeer(SharedIp, 64512, "A");
        var idB = store.CreatePeer(SharedIp, 64513, "B"); // same source IP, different AS

        Assert.NotEqual(idA, idB);

        var a = store.GetPeer(SharedIp, 64512);
        var b = store.GetPeer(SharedIp, 64513);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal((uint?)64512, a!.Asn);
        Assert.Equal((uint?)64513, b!.Asn);
        Assert.NotEqual(a.Id, b.Id);

        // Resolving by an AS that never connected must NOT return another peer's record.
        Assert.Null(store.GetPeer(SharedIp, 64599));
    }

    [Fact]
    public void Composite_Unique_Index_Rejects_Duplicate_IpAsn()
    {
        var (_, connection) = NewStore();
        using var conn = connection;
        var options = new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(connection).Options;

        using var db = new BgpDbContext(options);
        db.Peers.Add(new Peer { Ip = SharedIp, Asn = 64512 });
        db.SaveChanges();

        // The failed SaveChanges below leaves this entity in the Added state, so detach it before
        // the next insert or it would be retried and trip the unique constraint again.
        var dup = new Peer { Ip = SharedIp, Asn = 64512 }; // exact duplicate (Ip, Asn)
        db.Peers.Add(dup);
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
        db.Entry(dup).State = EntityState.Detached;

        // A different AS on the same IP is still allowed.
        db.Peers.Add(new Peer { Ip = SharedIp, Asn = 64513 });
        db.SaveChanges();
    }

    [Fact]
    public void UpdateSessionStatus_Is_Scoped_To_IpAsn()
    {
        var (store, connection) = NewStore();
        using var conn = connection;

        var idA = store.CreatePeer(SharedIp, 64512, "A");
        var idB = store.CreatePeer(SharedIp, 64513, "B");
        store.UpdateSessionStatus(SharedIp, 64512, active: true);

        Assert.Equal("active", store.GetDbPeerById(idA)!.Status);
        Assert.Equal("inactive", store.GetDbPeerById(idB)!.Status); // untouched — different AS

        store.UpdateSessionStatus(SharedIp, 64513, active: true);
        Assert.Equal("active", store.GetDbPeerById(idB)!.Status);
    }

    [Fact]
    public void CreatePeer_Is_Idempotent_On_IpAsn()
    {
        var (store, connection) = NewStore();
        using var conn = connection;

        var id1 = store.CreatePeer(SharedIp, 64512, "first");
        var id2 = store.CreatePeer(SharedIp, 64512, "second"); // same (Ip, Asn) → update, not a new row

        Assert.Equal(id1, id2);
        Assert.Equal("second", store.GetDbPeerById(id1)!.Description);
    }

    /// <summary>
    /// Hard requirement: existing peer data on the server must survive the index change. Simulates a
    /// database created by a previous (Ip-only-unique) version holding a real peer row, runs the
    /// idempotent migration in <see cref="BgpDbContext.Initialize"/>, and asserts the row is intact
    /// and the legacy Ip-only unique index has been replaced by the composite (so a second peer with
    /// a different AS can now coexist).
    /// </summary>
    [Fact]
    public void Initialize_Migrates_Legacy_Unique_Index_And_Preserves_Data()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(connection).Options;

        // A database created by a previous (Ip-only-unique) version, holding real data.
        using (var setup = new BgpDbContext(options))
        {
            setup.Database.ExecuteSqlRaw(
                "CREATE TABLE Peers (" +
                "Id TEXT NOT NULL PRIMARY KEY, Ip TEXT NOT NULL, Asn INTEGER NULL, " +
                "Description TEXT NULL, Status TEXT NOT NULL DEFAULT 'inactive', " +
                "CreatedAt TEXT NOT NULL, LastSessionAt TEXT NULL);");
            setup.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IX_Peers_Ip ON Peers (Ip);");
            setup.Database.ExecuteSqlRaw(
                "INSERT INTO Peers (Id, Ip, Asn, Description, Status, CreatedAt) " +
                "VALUES ('peer-1', '203.0.113.10', 64512, 'existing peer', 'inactive', '2024-01-01T00:00:00Z');");
        }

        // Upgrade: Initialize drops IX_Peers_Ip and creates the composite UX_Peers_Ip_Asn (data
        // untouched — only index DDL, never row DML).
        using (var boot = new BgpDbContext(options))
            BgpDbContext.Initialize(boot);

        using var check = new BgpDbContext(options);
        var existing = check.Peers.AsNoTracking().Single();
        Assert.Equal("peer-1", existing.Id);                 // data preserved
        Assert.Equal("203.0.113.10", existing.Ip);
        Assert.Equal((uint?)64512, existing.Asn);
        Assert.Equal("existing peer", existing.Description);

        // The legacy Ip-only unique index is gone: a second peer with a DIFFERENT AS now coexists.
        var store = new PeerStore(new StaticOptionsFactory(options));
        var idB = store.CreatePeer("203.0.113.10", 64513, "new peer behind same NAT");
        Assert.NotEqual("peer-1", idB);

        using var check2 = new BgpDbContext(options);
        Assert.Equal(2, check2.Peers.AsNoTracking().Count());
    }
}
