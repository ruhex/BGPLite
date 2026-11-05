using BGPLite.Api;
using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

public class PeerMd5BootstrapTests
{
    [Fact]
    public async Task StartAsync_LoadsPersistedKeys_WithoutStartingManagementApi()
    {
        var (store, connection) = NewStore();
        using var _ = connection;
        await store.SavePeerConfigurationAsync(
            "192.0.2.10", 65010, null, [], [], [], md5Password: "secret-b");
        await store.SavePeerConfigurationAsync(
            "192.0.2.10", 65011, null, [], [], [], md5Password: "secret-a");
        await store.SavePeerConfigurationAsync(
            "192.0.2.20", 65020, null, [], [], [], md5Password: "other-secret");
        var sessions = new RecordingSessions();
        var bootstrap = new PeerMd5BootstrapService(store, sessions, NullLogger<PeerMd5BootstrapService>.Instance);

        await bootstrap.StartAsync(CancellationToken.None);

        Assert.Equal(2, sessions.Keys.Count);
        Assert.Equal(("192.0.2.10", "secret-a"), sessions.Keys[0]);
        Assert.Equal(("192.0.2.20", "other-secret"), sessions.Keys[1]);
    }

    [Fact]
    public async Task StartAsync_KeyFailure_PropagatesAndFailsStartup()
    {
        var (store, connection) = NewStore();
        using var _ = connection;
        await store.SavePeerConfigurationAsync(
            "192.0.2.10", 65010, null, [], [], [], md5Password: "secret");
        var bootstrap = new PeerMd5BootstrapService(
            store, new ThrowingSessions(), NullLogger<PeerMd5BootstrapService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => bootstrap.StartAsync(CancellationToken.None));
    }

    [Fact]
    /// <summary>Verifies that persisted credentials are armed by bootstrap before the BGP listener accepts peers.</summary>
    public async Task PersistedKey_BootstrapThenServerStart_IsArmedBeforeAccepting()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (store, connection) = NewStore();
        using var _ = connection;
        await store.SavePeerConfigurationAsync(
            "192.0.2.10", 65010, null, [], [], [], md5Password: "secret");
        using var server = new BgpServer(
            new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } },
            new RouteTable(), AllowAllFilter.Instance, new BgpMetrics(),
            new NoopSessionFactory(), NullLogger<BgpServer>.Instance);
        server.ListenPort = 0;
        var bootstrap = new PeerMd5BootstrapService(store, server, NullLogger<PeerMd5BootstrapService>.Instance);

        await bootstrap.StartAsync(CancellationToken.None);
        Assert.False(server.IsPeerMd5Armed("192.0.2.10", "secret"));

        await server.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(server.IsPeerMd5Armed("192.0.2.10", "secret"));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static (PeerStore Store, SqliteConnection Connection) NewStore()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(connection).Options;
        using (var db = new BgpDbContext(options))
            BgpDbContext.Initialize(db);
        return (new PeerStore(new StaticOptionsFactory(options)), connection);
    }

    private sealed class StaticOptionsFactory(DbContextOptions<BgpDbContext> options) : IDbContextFactory<BgpDbContext>
    {
        public BgpDbContext CreateDbContext() => new(options);
    }

    private class RecordingSessions : ISessionManager
    {
        public List<(string Ip, string? Password)> Keys { get; } = [];
        public virtual void SetPeerMd5Key(string peerIp, string? password) => Keys.Add((peerIp, password));
        public Task RefreshPeerAsync(string peerIp, uint asn) => Task.CompletedTask;
        public List<string> GetActivePeerIps() => [];
        public Task TerminatePeerAsync(string peerIp, uint asn, CancellationToken ct = default) => Task.CompletedTask;
        public Task TerminatePeerByIpAsync(string peerIp, CancellationToken ct = default) => Task.CompletedTask;
        public bool IsPeerEstablished(string peerIp, uint asn) => false;
        public bool IsPeerMd5Armed(string peerIp, string? password) => password is not null;
        public int GetAdvertisedPrefixCount(string peerIp, uint asn) => 0;
        public Task RefreshAllEstablishedAsync() => Task.CompletedTask;
    }

    private sealed class ThrowingSessions : RecordingSessions
    {
        public override void SetPeerMd5Key(string peerIp, string? password) =>
            throw new InvalidOperationException("simulated kernel refusal");
    }

    private sealed class NoopSessionFactory : IBgpSessionFactory
    {
        public BgpSession Create(IBgpConnection connection, PeerConfig peerConfig) => null!;
    }
}
