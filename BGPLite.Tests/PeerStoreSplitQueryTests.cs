using BGPLite.Api;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BGPLite.Tests;

/// <summary>
/// #260: both peer reads must emit one SELECT per collection, not a single statement LEFT JOINing
/// all of them. Without that, the driver materializes the Cartesian product of the child
/// collections — measured on a real SQLite file, a peer with 3 subscriptions / 200 custom prefixes /
/// 5 ASNs / 2 sources produced 6,000 rows and took 31 ms per <c>LoadPeerRoutingView</c> call
/// (0.33 ms split), and 120 ms per <c>GetPeerDetail</c> call (0.17 ms split).
/// <para>
/// Asserted by counting the SQL actually emitted rather than by timing, so the guard is
/// deterministic and does not depend on the runner. It also covers what EF's
/// <c>MultipleCollectionIncludeWarning</c> cannot: that warning fires only for the <c>Include</c>
/// shape, and <c>GetPeerDetail</c> uses a projection — whose collection subqueries were documented
/// as auto-splitting and demonstrably do not.
/// </para>
/// </summary>
public class PeerStoreSplitQueryTests
{
    private const string Ip = "203.0.113.20";
    private const uint Asn = 64520;

    [Fact]
    public async Task LoadPeerRoutingView_EmitsOneSelectPerCollection()
    {
        var (store, connection, selects) = NewStoreCountingSelects();
        using var _ = connection;
        var id = await SeedPeerWithChildren(store);

        selects.Clear();
        var view = await store.LoadPeerRoutingViewAsync(Ip, Asn);

        Assert.NotNull(view);
        // peer row + 4 collections; a single-query load would emit exactly 1 SELECT.
        Assert.True(selects.Count > 1,
            $"expected a split query (one SELECT per collection), got {selects.Count} SELECT statement(s) — " +
            "the Cartesian-product shape is back (#260)");
    }

    [Fact]
    public async Task GetPeerDetail_EmitsOneSelectPerCollection()
    {
        var (store, connection, selects) = NewStoreCountingSelects();
        using var _ = connection;
        var id = await SeedPeerWithChildren(store);

        selects.Clear();
        var detail = await store.GetPeerDetailAsync(id);

        Assert.NotNull(detail);
        Assert.True(selects.Count > 1,
            $"expected a split query (one SELECT per collection), got {selects.Count} SELECT statement(s) — " +
            "a projection does NOT auto-split, AsSplitQuery is what does it (#260)");
    }

    /// <summary>The split must not change what the reads return — the point is the SQL shape, not the data.</summary>
    [Fact]
    public async Task SplitReadsReturnTheSameData()
    {
        var (store, connection, _) = NewStoreCountingSelects();
        using var __ = connection;
        var id = await SeedPeerWithChildren(store);

        var view = await store.LoadPeerRoutingViewAsync(Ip, Asn);
        var detail = await store.GetPeerDetailAsync(id);

        Assert.NotNull(view);
        Assert.NotNull(detail);
        Assert.Equal(["list-a", "list-b"], view!.Subscriptions);
        Assert.Equal(["10.0.0.0/8", "192.0.2.0/24"], view.CustomPrefixes);
        Assert.Equal([64512u, 64513u], view.CustomAsns);
        // Only Active sources are advertised (#147); the peer below has one of each.
        var source = Assert.Single(view.UserSources);
        Assert.Equal("active-src", source.Name);

        Assert.Equal(["list-a", "list-b"], detail!.Subscriptions);
        Assert.Equal(["10.0.0.0/8", "192.0.2.0/24"], detail.CustomPrefixes);
        Assert.Equal([64512u, 64513u], detail.CustomAsns);
        Assert.Equal(2, detail.CustomSources.Count); // the detail view shows inactive ones too
    }

    // ---- harness ----

    private static (PeerStore Store, SqliteConnection Connection, List<string> Selects) NewStoreCountingSelects()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var selects = new List<string>();
        var options = new DbContextOptionsBuilder<BgpDbContext>()
            .UseSqlite(connection)
            .LogTo(line => { if (line.Contains("SELECT", StringComparison.Ordinal)) lock (selects) selects.Add(line); },
                   [RelationalEventId.CommandExecuted])
            .Options;
        using (var boot = new BgpDbContext(options))
            BgpDbContext.Initialize(boot);
        return (new PeerStore(new StaticOptionsFactory(options)), connection, selects);
    }

    private static async Task<string> SeedPeerWithChildren(PeerStore store)
    {
        var id = await store.CreatePeerAsync(Ip, Asn, "split-query test");
        await store.SetSubscriptionsAsync(id, ["list-a", "list-b"]);
        await store.SetCustomPrefixesAsync(id, [("10.0.0.0", (byte)8), ("192.0.2.0", (byte)24)]);
        await store.SetCustomAsnsAsync(id, [64512u, 64513u]);
        var active = await store.AddCustomSourceAsync(id, "active-src", "https://example.invalid/a", null);
        await store.AddCustomSourceAsync(id, "paused-src", "https://example.invalid/b", null);
        await store.SetSourceActiveAsync(id, active.Id, true);
        return id;
    }

    private sealed class StaticOptionsFactory(DbContextOptions<BgpDbContext> options) : IDbContextFactory<BgpDbContext>
    {
        public BgpDbContext CreateDbContext() => new(options);
    }
}
