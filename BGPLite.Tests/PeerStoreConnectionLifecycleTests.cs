using System.Data;
using System.Data.Common;
using BGPLite.Api;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BGPLite.Tests;

public class PeerStoreConnectionLifecycleTests
{
    [Fact]
    public async Task CreatePeer_StoreOwnedConnection_UsesOnlyAsyncOpenAndClose()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bgplite-async-{Guid.NewGuid():N}.db");
        var interceptor = new LifecycleInterceptor();
        try
        {
            var options = new DbContextOptionsBuilder<BgpDbContext>()
                .UseSqlite($"Data Source={path}")
                .AddInterceptors(interceptor)
                .Options;
            using (var db = new BgpDbContext(options))
                BgpDbContext.Initialize(db);
            interceptor.Reset();
            var store = new PeerStore(new StaticOptionsFactory(options));

            var id = await store.CreatePeerAsync("203.0.113.10", 64512, "test");

            Assert.False(string.IsNullOrEmpty(id));
            Assert.Equal(0, interceptor.SyncOpens);
            Assert.Equal(0, interceptor.SyncCloses);
            Assert.Equal(1, interceptor.AsyncOpens);
            Assert.Equal(ConnectionState.Closed, interceptor.LastConnection!.State);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CreatePeer_ExternallyOpenedConnection_RemainsOpen()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var interceptor = new LifecycleInterceptor();
        var options = new DbContextOptionsBuilder<BgpDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        using (var db = new BgpDbContext(options))
            BgpDbContext.Initialize(db);
        interceptor.Reset();
        var store = new PeerStore(new StaticOptionsFactory(options));

        await store.CreatePeerAsync("203.0.113.10", 64512, "test");

        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(0, interceptor.SyncOpens + interceptor.AsyncOpens);
        Assert.Equal(0, interceptor.SyncCloses);
    }

    private sealed class LifecycleInterceptor : IDbConnectionInterceptor
    {
        public int SyncOpens { get; private set; }
        public int AsyncOpens { get; private set; }
        public int SyncCloses { get; private set; }
        public DbConnection? LastConnection { get; private set; }

        public void Reset() =>
            (SyncOpens, AsyncOpens, SyncCloses, LastConnection) = (0, 0, 0, null);

        public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            SyncOpens++;
            LastConnection = connection;
        }

        public Task ConnectionOpenedAsync(
            DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            AsyncOpens++;
            LastConnection = connection;
            return Task.CompletedTask;
        }

        public InterceptionResult ConnectionClosing(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            SyncCloses++;
            return result;
        }
    }

    private sealed class StaticOptionsFactory(DbContextOptions<BgpDbContext> options) : IDbContextFactory<BgpDbContext>
    {
        public BgpDbContext CreateDbContext() => new(options);
    }
}
