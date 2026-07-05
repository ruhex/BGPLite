using BGPLite.Api;
using BGPLite.Api.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BGPLite.Tests;

/// <summary>
/// Tests for PeerCustomSource (#143-1 / #146): URL-based prefix-list sources per peer.
/// Mirrors the PeerStoreKeyingTests pattern (real in-memory SQLite).
/// </summary>
public class PeerCustomSourceTests
{
    private const string TestIp = "203.0.113.10";

    private static (PeerStore store, SqliteConnection connection) NewStore()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<BgpDbContext>()
            .UseSqlite(connection)
            .Options;
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

    [Fact]
    public void AddCustomSource_Adds_And_GetCustomSources_Returns_It()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var peerId = store.CreatePeer(TestIp, 65001, null);

        var source = store.AddCustomSource(peerId, "my-list", "https://example.com/list.txt", "65444:501");

        Assert.NotEmpty(source.Id);
        var sources = store.GetCustomSources(peerId);
        var fetched = Assert.Single(sources);
        Assert.Equal(source.Id, fetched.Id);
        Assert.Equal("my-list", fetched.Name);
        Assert.Equal("https://example.com/list.txt", fetched.Url);
        Assert.Equal("65444:501", fetched.Community);
    }

    [Fact]
    public void AddCustomSource_SameName_SamePeer_OK()
    {
        // Name is just a label, not a unique key — duplicates are allowed (different Ids).
        var (store, conn) = NewStore();
        using var _ = conn;
        var peerId = store.CreatePeer(TestIp, 65001, null);

        var a = store.AddCustomSource(peerId, "my-list", "https://a.com/list.txt", null);
        var b = store.AddCustomSource(peerId, "my-list", "https://b.com/other.txt", null);

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(2, store.GetCustomSources(peerId).Count);
    }

    [Fact]
    public void DeleteCustomSource_BySourceId_Removes_Only_That_Source()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var peerId = store.CreatePeer(TestIp, 65001, null);

        var sourceA = store.AddCustomSource(peerId, "list-a", "https://a.com/list.txt", "65444:501");
        store.AddCustomSource(peerId, "list-b", "https://b.com/list.txt", null);

        var deleted = store.DeleteCustomSource(peerId, sourceA.Id);
        Assert.True(deleted);

        var remaining = store.GetCustomSources(peerId);
        var source = Assert.Single(remaining);
        Assert.Equal("list-b", source.Name);
    }

    [Fact]
    public void DeleteCustomSource_NotFound_Returns_False()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var peerId = store.CreatePeer(TestIp, 65001, null);

        Assert.False(store.DeleteCustomSource(peerId, "nonexistent-id"));
    }

    [Fact]
    public void DeletePeer_Cascades_Sources()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var peerId = store.CreatePeer(TestIp, 65001, null);

        store.AddCustomSource(peerId, "list-a", "https://a.com/list.txt", null);
        store.AddCustomSource(peerId, "list-b", "https://b.com/list.txt", null);

        store.DeletePeer(peerId);

        Assert.Empty(store.GetCustomSources(peerId));
    }

    [Fact]
    public void GetCustomSources_Empty_For_NewPeer()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var peerId = store.CreatePeer(TestIp, 65001, null);

        Assert.Empty(store.GetCustomSources(peerId));
    }

    [Fact]
    public void Community_Is_Optional_Null()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var peerId = store.CreatePeer(TestIp, 65001, null);

        store.AddCustomSource(peerId, "no-comm", "https://example.com/list.txt", null);

        var source = Assert.Single(store.GetCustomSources(peerId));
        Assert.Null(source.Community);
    }

    [Fact]
    public void NewSource_Is_Inactive_ByDefault()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var peerId = store.CreatePeer(TestIp, 65001, null);

        var source = store.AddCustomSource(peerId, "paused", "https://example.com/list.txt", null);

        Assert.False(source.Active, "new source must default to inactive (user explicitly activates)");
    }

    [Fact]
    public void SetSourceActive_Toggles_State()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var peerId = store.CreatePeer(TestIp, 65001, null);

        var source = store.AddCustomSource(peerId, "toggle", "https://example.com/list.txt", null);
        Assert.False(source.Active);

        Assert.True(store.SetSourceActive(peerId, source.Id, true));
        var fetched = Assert.Single(store.GetCustomSources(peerId));
        Assert.True(fetched.Active);

        Assert.True(store.SetSourceActive(peerId, source.Id, false));
        fetched = Assert.Single(store.GetCustomSources(peerId));
        Assert.False(fetched.Active);
    }

    [Fact]
    public void SetSourceActive_NotFound_Returns_False()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var peerId = store.CreatePeer(TestIp, 65001, null);

        Assert.False(store.SetSourceActive(peerId, "nonexistent", true));
    }

    [Fact]
    public void DeleteCustomSource_CrossPeer_Returns_False()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var idA = store.CreatePeer(TestIp, 65001, null);
        var idB = store.CreatePeer("203.0.113.11", 65002, null);

        var source = store.AddCustomSource(idA, "list-a", "https://a.com/list.txt", null);

        // Peer B tries to delete Peer A's source — must fail (peer-scoped).
        Assert.False(store.DeleteCustomSource(idB, source.Id));
        Assert.Single(store.GetCustomSources(idA)); // source still exists for A
    }

    [Fact]
    public void SetSourceActive_CrossPeer_Returns_False()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var idA = store.CreatePeer(TestIp, 65001, null);
        var idB = store.CreatePeer("203.0.113.11", 65002, null);

        var source = store.AddCustomSource(idA, "list-a", "https://a.com/list.txt", null);

        // Peer B tries to toggle Peer A's source — must fail.
        Assert.False(store.SetSourceActive(idB, source.Id, true));
        var fetched = Assert.Single(store.GetCustomSources(idA));
        Assert.False(fetched.Active); // unchanged
    }
}
