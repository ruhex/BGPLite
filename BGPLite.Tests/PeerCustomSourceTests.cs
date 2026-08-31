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
    public async Task AddCustomSource_Adds_And_GetCustomSources_Returns_It()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var peerId = await store.CreatePeerAsync(TestIp, 65001, null);

        var source = await store.AddCustomSourceAsync(peerId, "my-list", "https://example.com/list.txt", "65444:501");

        Assert.NotEmpty(source.Id);
        var sources = await store.GetCustomSourcesAsync(peerId);
        var fetched = Assert.Single(sources);
        Assert.Equal(source.Id, fetched.Id);
        Assert.Equal("my-list", fetched.Name);
        Assert.Equal("https://example.com/list.txt", fetched.Url);
        Assert.Equal("65444:501", fetched.Community);
    }

    [Fact]
    public async Task AddCustomSource_SameName_SamePeer_OK()
    {
        // Name is just a label, not a unique key — duplicates are allowed (different Ids).
        var (store, conn) = NewStore();
        using var _ = conn;
        var peerId = await store.CreatePeerAsync(TestIp, 65001, null);

        var a = await store.AddCustomSourceAsync(peerId, "my-list", "https://a.com/list.txt", null);
        var b = await store.AddCustomSourceAsync(peerId, "my-list", "https://b.com/other.txt", null);

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(2, (await store.GetCustomSourcesAsync(peerId)).Count);
    }

    [Fact]
    public async Task DeleteCustomSource_BySourceId_Removes_Only_That_Source()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var peerId = await store.CreatePeerAsync(TestIp, 65001, null);

        var sourceA = await store.AddCustomSourceAsync(peerId, "list-a", "https://a.com/list.txt", "65444:501");
        await store.AddCustomSourceAsync(peerId, "list-b", "https://b.com/list.txt", null);

        var deleted = await store.DeleteCustomSourceAsync(peerId, sourceA.Id);
        Assert.True(deleted);

        var remaining = await store.GetCustomSourcesAsync(peerId);
        var source = Assert.Single(remaining);
        Assert.Equal("list-b", source.Name);
    }

    [Fact]
    public async Task DeleteCustomSource_NotFound_Returns_False()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var peerId = await store.CreatePeerAsync(TestIp, 65001, null);

        Assert.False(await store.DeleteCustomSourceAsync(peerId, "nonexistent-id"));
    }

    [Fact]
    public async Task DeletePeer_Cascades_Sources()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var peerId = await store.CreatePeerAsync(TestIp, 65001, null);

        await store.AddCustomSourceAsync(peerId, "list-a", "https://a.com/list.txt", null);
        await store.AddCustomSourceAsync(peerId, "list-b", "https://b.com/list.txt", null);

        await store.DeletePeerAsync(peerId);

        Assert.Empty(await store.GetCustomSourcesAsync(peerId));
    }

    [Fact]
    public async Task GetCustomSources_Empty_For_NewPeer()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var peerId = await store.CreatePeerAsync(TestIp, 65001, null);

        Assert.Empty(await store.GetCustomSourcesAsync(peerId));
    }

    [Fact]
    public async Task Community_Is_Optional_Null()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var peerId = await store.CreatePeerAsync(TestIp, 65001, null);

        await store.AddCustomSourceAsync(peerId, "no-comm", "https://example.com/list.txt", null);

        var source = Assert.Single(await store.GetCustomSourcesAsync(peerId));
        Assert.Null(source.Community);
    }

    [Fact]
    public async Task NewSource_Is_Inactive_ByDefault()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var peerId = await store.CreatePeerAsync(TestIp, 65001, null);

        var source = await store.AddCustomSourceAsync(peerId, "paused", "https://example.com/list.txt", null);

        Assert.False(source.Active, "new source must default to inactive (user explicitly activates)");
    }

    [Fact]
    public async Task SetSourceActive_Toggles_State()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var peerId = await store.CreatePeerAsync(TestIp, 65001, null);

        var source = await store.AddCustomSourceAsync(peerId, "toggle", "https://example.com/list.txt", null);
        Assert.False(source.Active);

        Assert.True(await store.SetSourceActiveAsync(peerId, source.Id, true));
        var fetched = Assert.Single(await store.GetCustomSourcesAsync(peerId));
        Assert.True(fetched.Active);

        Assert.True(await store.SetSourceActiveAsync(peerId, source.Id, false));
        fetched = Assert.Single(await store.GetCustomSourcesAsync(peerId));
        Assert.False(fetched.Active);
    }

    [Fact]
    public async Task SetSourceActive_NotFound_Returns_False()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var peerId = await store.CreatePeerAsync(TestIp, 65001, null);

        Assert.False(await store.SetSourceActiveAsync(peerId, "nonexistent", true));
    }

    [Fact]
    public async Task DeleteCustomSource_CrossPeer_Returns_False()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var idA = await store.CreatePeerAsync(TestIp, 65001, null);
        var idB = await store.CreatePeerAsync("203.0.113.11", 65002, null);

        var source = await store.AddCustomSourceAsync(idA, "list-a", "https://a.com/list.txt", null);

        // Peer B tries to delete Peer A's source — must fail (peer-scoped).
        Assert.False(await store.DeleteCustomSourceAsync(idB, source.Id));
        Assert.Single(await store.GetCustomSourcesAsync(idA)); // source still exists for A
    }

    [Fact]
    public async Task SetSourceActive_CrossPeer_Returns_False()
    {
        var (store, conn) = NewStore();
        using var _ = conn;
        var idA = await store.CreatePeerAsync(TestIp, 65001, null);
        var idB = await store.CreatePeerAsync("203.0.113.11", 65002, null);

        var source = await store.AddCustomSourceAsync(idA, "list-a", "https://a.com/list.txt", null);

        // Peer B tries to toggle Peer A's source — must fail.
        Assert.False(await store.SetSourceActiveAsync(idB, source.Id, true));
        var fetched = Assert.Single(await store.GetCustomSourcesAsync(idA));
        Assert.False(fetched.Active); // unchanged
    }
}
