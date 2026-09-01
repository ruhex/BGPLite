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
            BgpDbContext.Initialize(boot); // EF Migrations: Init + LegacyEnsureCreated

        return (new PeerStore(new StaticOptionsFactory(options)), connection);
    }

    private sealed class StaticOptionsFactory : IDbContextFactory<BgpDbContext>
    {
        private readonly DbContextOptions<BgpDbContext> _options;
        public StaticOptionsFactory(DbContextOptions<BgpDbContext> options) => _options = options;
        public BgpDbContext CreateDbContext() => new(_options);
    }

    [Fact]
    public async Task Distinct_Asns_From_Same_Ip_Create_Separate_Peer_Records()
    {
        var (store, connection) = NewStore();
        using var conn = connection;

        var idA = await store.CreatePeerAsync(SharedIp, 64512, "A");
        var idB = await store.CreatePeerAsync(SharedIp, 64513, "B"); // same source IP, different AS

        Assert.NotEqual(idA, idB);

        var a = await store.GetPeerAsync(SharedIp, 64512);
        var b = await store.GetPeerAsync(SharedIp, 64513);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal((uint?)64512, a!.Asn);
        Assert.Equal((uint?)64513, b!.Asn);
        Assert.NotEqual(a.Id, b.Id);

        // Resolving by an AS that never connected must NOT return another peer's record.
        Assert.Null(await store.GetPeerAsync(SharedIp, 64599));
    }

    [Fact]
    public async Task Composite_Unique_Index_Rejects_Duplicate_IpAsn()
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
    public async Task UpdateSessionStatus_Is_Scoped_To_IpAsn()
    {
        var (store, connection) = NewStore();
        using var conn = connection;

        var idA = await store.CreatePeerAsync(SharedIp, 64512, "A");
        var idB = await store.CreatePeerAsync(SharedIp, 64513, "B");
        await store.UpdateSessionStatusAsync(SharedIp, 64512, active: true);

        Assert.Equal("active", (await store.GetDbPeerByIdAsync(idA))!.Status);
        Assert.Equal("inactive", (await store.GetDbPeerByIdAsync(idB))!.Status); // untouched — different AS

        await store.UpdateSessionStatusAsync(SharedIp, 64513, active: true);
        Assert.Equal("active", (await store.GetDbPeerByIdAsync(idB))!.Status);
    }

    /// <summary>
    /// Regression for issue #84: <see cref="PeerStore.LoadPeerRoutingView"/> must return the SAME
    /// data the five standalone calls used to produce (GetPeer + GetSubscriptions + GetCustomPrefixes
    /// + GetCustomAsns) AND fold the session-status update into the same DbContext
    /// (<see cref="PeerStore.UpdateSessionStatus"/>(active:true)). Asserts both equivalence of shape
    /// and the side effect (Status="active", LastSessionAt set) so a future refactor cannot silently
    /// drop either the read or the folded write.
    /// </summary>
    [Fact]
    public async Task LoadPeerRoutingView_Matches_Standalone_Calls_And_Folds_Status_Update()
    {
        var (store, connection) = NewStore();
        using var conn = connection;

        var id = await store.CreatePeerAsync(SharedIp, 64512, "routed peer");
        await store.SetSubscriptionsAsync(id, ["ru", "microsoft"]);
        await store.SetCustomPrefixesAsync(id, [("203.0.113.0", 24), ("198.51.100.0", 25)]);
        await store.SetCustomAsnsAsync(id, [65001, 65002]);

        var before = DateTime.UtcNow;
        var view = await store.LoadPeerRoutingViewAsync(SharedIp, 64512);

        Assert.NotNull(view);
        // Same peer row.
        Assert.Equal(id, view!.PeerId);
        // Same child-collection shapes (types) as the standalone getters.
        Assert.IsType<List<string>>(view.Subscriptions);
        Assert.IsType<List<string>>(view.CustomPrefixes);
        Assert.IsType<List<uint>>(view.CustomAsns);
        // Same ELEMENTS as the standalone getters. Order is unspecified (neither the getters nor
        // LoadPeerRoutingView impose ORDER BY; the BGP send path treats these as sets), so compare
        // sorted to avoid a flaky test while still pinning exact contents.
        Assert.Equal((await store.GetSubscriptionsAsync(id)).Order().ToArray(), view.Subscriptions.Order().ToArray());
        Assert.Equal((await store.GetCustomPrefixesAsync(id)).Order().ToArray(), view.CustomPrefixes.Order().ToArray());
        Assert.Equal((await store.GetCustomAsnsAsync(id)).Order().ToArray(), view.CustomAsns.Order().ToArray());
        // Explicit contents (guards against a future shape regression even if getters change).
        Assert.Equal(new[] { "microsoft", "ru" }, view.Subscriptions.Order().ToArray());
        Assert.Equal(new[] { "198.51.100.0/25", "203.0.113.0/24" }, view.CustomPrefixes.Order().ToArray());
        Assert.Equal(new uint[] { 65001, 65002 }, view.CustomAsns.Order().ToArray());

        // The folded status write took effect: Status="active" and LastSessionAt stamped at/after
        // the call (was a separate UpdateSessionStatus call on its own DbContext before #84).
        var peer = (await store.GetDbPeerByIdAsync(id))!;
        Assert.Equal("active", peer.Status);
        Assert.NotNull(peer.LastSessionAt);
        Assert.True(peer.LastSessionAt >= before);
    }

    [Fact]
    public async Task LoadPeerRoutingView_Returns_Null_For_Unknown_Peer()
    {
        var (store, connection) = NewStore();
        using var conn = connection;

        // No CreatePeer — the send path must see null so it auto-registers the unknown peer.
        Assert.Null(await store.LoadPeerRoutingViewAsync(SharedIp, 64599));
    }

    [Fact]
    public async Task LoadPeerRoutingView_UserSources_Only_Active_Loaded()
    {
        // Issue #147: paused (Active=false) sources never leave the DB — only Active ones are
        // advertised. AddCustomSource defaults Active=false; SetSourceActive toggles.
        var (store, connection) = NewStore();
        using var conn = connection;

        var id = await store.CreatePeerAsync(SharedIp, 64512, "sources peer");
        var active = await store.AddCustomSourceAsync(id, "on", "https://example.com/on.txt", null);
        await store.AddCustomSourceAsync(id, "off", "https://example.com/off.txt", "65000:9");
        Assert.True(await store.SetSourceActiveAsync(id, active.Id, true));

        var view = await store.LoadPeerRoutingViewAsync(SharedIp, 64512);
        Assert.NotNull(view);
        var src = Assert.Single(view!.UserSources);   // the inactive "off" source is excluded
        Assert.Equal("on", src.Name);
        Assert.Equal("https://example.com/on.txt", src.Url);
        Assert.Null(src.Community);
    }

    [Fact]
    public async Task LoadPeerRoutingView_UserSources_All_Inactive_Empty()
    {
        var (store, connection) = NewStore();
        using var conn = connection;

        var id = await store.CreatePeerAsync(SharedIp, 64512, "all-inactive");
        await store.AddCustomSourceAsync(id, "a", "https://example.com/a.txt", null);
        await store.AddCustomSourceAsync(id, "b", "https://example.com/b.txt", "65000:1");

        var view = await store.LoadPeerRoutingViewAsync(SharedIp, 64512);
        Assert.NotNull(view);
        Assert.Empty(view!.UserSources);
    }

    [Fact]
    public async Task CreatePeer_Is_Idempotent_On_IpAsn()
    {
        var (store, connection) = NewStore();
        using var conn = connection;

        var id1 = await store.CreatePeerAsync(SharedIp, 64512, "first");
        var id2 = await store.CreatePeerAsync(SharedIp, 64512, "second"); // same (Ip, Asn) → update, not a new row

        Assert.Equal(id1, id2);
        Assert.Equal("second", (await store.GetDbPeerByIdAsync(id1))!.Description);
    }

    /// <summary>
    /// Hard requirement: existing peer data on the server must survive the index change. Simulates a
    /// database created by a previous (Ip-only-unique) version holding a real peer row, runs the
    /// idempotent migration in <see cref="BgpDbContext.Initialize"/>, and asserts the row is intact
    /// and the legacy Ip-only unique index has been replaced by the composite (so a second peer with
    /// a different AS can now coexist).
    /// </summary>
    [Fact]
    public async Task Initialize_Migrates_Legacy_Unique_Index_And_Preserves_Data()
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
        var idB = await store.CreatePeerAsync("203.0.113.10", 64513, "new peer behind same NAT");
        Assert.NotEqual("peer-1", idB);

        using var check2 = new BgpDbContext(options);
        Assert.Equal(2, check2.Peers.AsNoTracking().Count());
    }

    /// <summary>
    /// #264: a legacy EnsureCreated-era database MISSING expected Peers columns (an early build
    /// without Description/LastSessionAt) must be converged at Initialize — the stamp alone left
    /// the first runtime write failing with "no such column".
    /// </summary>
    [Fact]
    public void Initialize_LegacyDbMissingPeersColumns_ConvergesBeforeStamp()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        // Early-build shape: no Description, no LastSessionAt, no Status default concerns.
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                "CREATE TABLE Peers (" +
                "\"Id\" TEXT NOT NULL PRIMARY KEY, \"Ip\" TEXT NOT NULL, \"Asn\" INTEGER NULL, " +
                "\"Status\" TEXT NOT NULL DEFAULT 'inactive', \"CreatedAt\" TEXT NOT NULL)";
            cmd.ExecuteNonQuery();
        }
        var options = new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(connection).Options;

        using (var boot = new BgpDbContext(options))
            BgpDbContext.Initialize(boot);   // RED pre-fix: stamps without converging

        // The columns now exist and a session-status write (the first raw write to touch
        // LastSessionAt) succeeds instead of throwing "no such column".
        string[] Columns()
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(\"Peers\")";
            using var reader = cmd.ExecuteReader();
            var names = new List<string>();
            while (reader.Read())
                names.Add(reader.GetString(1));
            return [.. names];
        }
        Assert.Contains("Description", Columns());
        Assert.Contains("LastSessionAt", Columns());

        using (var db = new BgpDbContext(options))
        {
            db.Peers.Add(new BGPLite.Api.Entities.Peer { Id = "p1", Ip = "198.51.100.1", Asn = 65001, Description = "converged", LastSessionAt = DateTime.UtcNow });
            db.SaveChanges();
        }
        using (var check = new BgpDbContext(options))
        {
            var peer = check.Peers.AsNoTracking().Single();
            Assert.Equal("converged", peer.Description);
            Assert.NotNull(peer.LastSessionAt);
        }
    }

    /// <summary>
    /// #237: Initialize runs the EF migration pipeline — a fresh database gets Init +
    /// LegacyEnsureCreated applied in order and recorded in __EFMigrationsHistory, and a second
    /// startup is an idempotent no-op (Migrate() finds nothing pending).
    /// </summary>
    [Fact]
    public void Initialize_FreshDatabase_AppliesMigrations_And_IsIdempotent()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(connection).Options;

        using (var boot = new BgpDbContext(options))
            BgpDbContext.Initialize(boot);
        using (var again = new BgpDbContext(options))
            BgpDbContext.Initialize(again); // second startup must be a no-op, not an error

        using var check = new BgpDbContext(options);
        var applied = check.Database.SqlQueryRaw<string>(
            "SELECT \"MigrationId\" AS \"Value\" FROM __EFMigrationsHistory ORDER BY \"MigrationId\"").ToList();
        Assert.Equal(["20260826182256_Init", "20260826182324_LegacyEnsureCreated", "20260901102233_AddPeerMaxPrefix", "20260901110154_AddPeerMd5Password"], applied);
        // The full schema is in place (spot-check a table from each era).
        Assert.NotNull(check.Peers);
    }
}
