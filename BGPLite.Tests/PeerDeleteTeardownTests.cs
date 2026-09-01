using BGPLite.Api;
using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Protocol;
using BGPLite.Providers;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// #323: deleting a peer through the management API must terminate its live BGP session(s) before
/// the row is deleted — otherwise the session keeps advertising, and the next refresh lands in
/// RouteAssembler's unknown-peer branch and re-creates the just-deleted row (auto-register).
/// </summary>
public sealed class PeerDeleteTeardownTests
{
    // ---- management API: terminate-before-delete ordering ----

    [Fact]
    public async Task DeletePeer_TerminatesSession_BeforeDeletingRow()
    {
        using var connection = NewOpenConnection();
        var store = NewStore(connection);
        var peerId = await store.CreatePeerAsync("203.0.113.7", 65002, null);
        // At terminate time the row must still exist — that ordering is what keeps a dying
        // session's in-flight refresh away from the auto-register branch.
        var manager = new RecordingSessionManager(async (_, _) => Assert.NotNull(await store.GetDbPeerByIdAsync(peerId)));
        using var api = NewApi(store, manager);

        var response = await api.HandleDeletePeer(peerId);

        Assert.Equal(200, response.StatusCode);
        var terminated = Assert.Single(manager.Terminated);
        Assert.Equal("203.0.113.7", terminated.Ip);
        Assert.Equal(65002u, terminated.Asn);
        Assert.Null(await store.GetDbPeerByIdAsync(peerId));
    }

    [Fact]
    public async Task DeletePeer_UnknownPeer_Returns404_AndDoesNotTerminate()
    {
        using var connection = NewOpenConnection();
        var manager = new RecordingSessionManager();
        using var api = NewApi(NewStore(connection), manager);

        var response = await api.HandleDeletePeer("00000000-0000-0000-0000-000000000000");

        Assert.Equal(404, response.StatusCode);
        Assert.Empty(manager.Terminated);
    }

    // ---- server: the teardown core over real scripted sessions ----

    [Fact]
    public async Task TerminateSessions_Established_SendsExactlyOneCease_AndUnwinds()
    {
        var (session, run, conn) = await EstablishScriptedSessionAsync();
        try
        {
            await BgpServer.TerminateSessionsAsync([session], CancellationToken.None);

            // Exactly one NOTIFICATION beyond the handshake frames: Cease / Administrative Reset.
            var notification = conn.Sent
                .Select(f => BgpMessageReader.ReadMessage(f))
                .OfType<BgpNotificationMessage>()
                .Single();
            Assert.Equal(BgpConstants.Error.Cease, notification.ErrorCode);
            Assert.Equal(BgpConstants.SubError.CeaseAdministrativeReset, notification.SubErrorCode);

            await WaitUntilRunCompletesAsync(run);
        }
        finally
        {
            session.Dispose();
            conn.Dispose();
        }
    }

    [Fact]
    public async Task TerminateSessions_NonEstablished_UnwindsWithoutNotification()
    {
        var conn = new ScriptedConnection();
        var session = NewSession(conn);
        var run = session.RunAsync();
        try
        {
            // Peer OPEN only — no KEEPALIVE, so the session parks in OpenConfirm, never Established.
            conn.EnqueueMessage(PeerOpen(65003));
            for (var i = 0; i < 200 && session.State != BgpFsmState.OpenConfirm; i++)
                await Task.Delay(TimeSpan.FromMilliseconds(10));
            Assert.Equal(BgpFsmState.OpenConfirm, session.State);

            await BgpServer.TerminateSessionsAsync([session], CancellationToken.None);

            // wasEstablished is false, so teardown must not emit any NOTIFICATION.
            Assert.DoesNotContain(conn.Sent, f => BgpMessageReader.ReadMessage(f) is BgpNotificationMessage);
            await WaitUntilRunCompletesAsync(run);
        }
        finally
        {
            session.Dispose();
            conn.Dispose();
        }
    }

    // ---- RouteAssembler: no auto-register for a dying session (#323 resurrection guard) ----

    [Fact]
    public async Task BuildOutboundRoutes_CancelledToken_UnknownPeer_DoesNotAutoRegister()
    {
        using var connection = NewOpenConnection();
        var store = NewStore(connection);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // #262: the async store surfaces an already-cancelled token as OCE from the load itself
        // (previously the sync read ran through and the assembler returned an empty set). Either
        // way the auto-register branch must not be reached — that is the #323 resurrection guard.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewAssembler(store).BuildOutboundRoutesAsync(
                "203.0.113.9", 65002, new PeerConfig { Address = "203.0.113.9" }, "203.0.113.9", cts.Token));

        Assert.Empty(await store.GetPeersByIpAsync("203.0.113.9"));   // no resurrection of a deleted peer
    }

    [Fact]
    public async Task BuildOutboundRoutes_LiveToken_UnknownPeer_StillAutoRegisters()
    {
        using var connection = NewOpenConnection();
        var store = NewStore(connection);

        await NewAssembler(store).BuildOutboundRoutesAsync(
            "203.0.113.9", 65002, new PeerConfig { Address = "203.0.113.9" }, "203.0.113.9", CancellationToken.None);

        // Control (D11 behavior): a live unknown peer is still auto-registered.
        Assert.Single(await store.GetPeersByIpAsync("203.0.113.9"));
    }

    // ---- harness ----

    /// <summary>RunAsync may unwind via cancellation or via the scripted closed connection — both are completions; only a hang fails the test.</summary>
    private static async Task WaitUntilRunCompletesAsync(Task run)
    {
        try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { /* unwound via cancellation */ }
        catch (IOException) { /* unwound via the scripted closed connection */ }
    }

    private static async Task<(BgpSession Session, Task Run, ScriptedConnection Conn)> EstablishScriptedSessionAsync()
    {
        var conn = new ScriptedConnection();
        var session = NewSession(conn);
        var run = session.RunAsync();

        conn.EnqueueMessage(PeerOpen(65002));
        conn.EnqueueMessage(BgpKeepaliveMessage.Instance);

        for (var i = 0; i < 200 && !session.IsEstablished; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        Assert.True(session.IsEstablished, "session must reach Established");
        return (session, run, conn);
    }

    private static BgpOpenMessage PeerOpen(uint asn) => new()
    {
        Version = BgpConstants.BgpVersion,
        // RFC 6793 §4.1: the 2-octet My-AS field carries AS_TRANS when the ASN needs 4 octets.
        Asn = (ushort)(asn > ushort.MaxValue ? BgpConstants.AsPath.AsTrans : asn),
        HoldTime = 0,
        RouterId = 0x0A000002,
        Capabilities = [BgpCapabilityInfo.FourOctetAsn(asn)],
    };

    private static BgpSession NewSession(ScriptedConnection conn) => new(
        conn,
        new PeerConfig { Address = "127.0.0.1" },
        new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 },
        new RouteTable(),
        AllowAllFilter.Instance,
        new BgpMetrics(),
        NullLogger<BgpSession>.Instance);

    private static SqliteConnection NewOpenConnection()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var boot = new BgpDbContext(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(connection).Options))
            BgpDbContext.Initialize(boot);
        return connection;
    }

    private static PeerStore NewStore(SqliteConnection connection) =>
        new(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(connection).Options));

    private static ManagementApi NewApi(PeerStore store, ISessionManager sessionManager) => new(
        store,
        new RouteTable(),
        new AppConfig(),
        new BgpMetrics(),
        NullLogger<ManagementApi>.Instance,
        new InertPrefixService(),
        new InertPrefixSourceService(),
        sessionManager);

    private static RouteAssembler NewAssembler(PeerStore store)
    {
        var config = new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } };
        return new RouteAssembler(
            new InertPrefixService(),
            store,
            new ConfigCommunityResolver(config, config.Bgp),
            AllowAllFilter.Instance,
            config,
            config.Bgp,
            NullLogger<RouteAssembler>.Instance);
    }

    /// <summary>Records TerminatePeerAsync calls; the optional hook runs at call time (row-state assertions).</summary>
    private sealed class RecordingSessionManager(Func<string, uint, Task>? onTerminate = null) : ISessionManager
    {
        public List<(string Ip, uint Asn)> Terminated { get; } = [];

        public Task RefreshPeerAsync(string peerIp, uint asn) => Task.CompletedTask;
        public List<string> GetActivePeerIps() => [];
        public int GetAdvertisedPrefixCount(string peerIp, uint asn) => 0;
        public Task RefreshAllEstablishedAsync() => Task.CompletedTask;

        public async Task TerminatePeerAsync(string peerIp, uint asn, CancellationToken ct = default)
        {
            Terminated.Add((peerIp, asn));
            if (onTerminate is not null) await onTerminate(peerIp, asn);
        }
        public void SetPeerMd5Key(string peerIp, string? password) { }
    }

    private sealed class StaticOptionsFactory(DbContextOptions<BgpDbContext> options) : IDbContextFactory<BgpDbContext>
    {
        public BgpDbContext CreateDbContext() => new(options);
    }

    /// <summary>Answers every prefix query with an empty set; the delete path never calls it.</summary>
    private sealed class InertPrefixService : IPrefixService
    {
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetPrefixesAsync(uint asn, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task<List<(uint Prefix, byte Length, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default)
            => Task.FromResult(new List<(uint Prefix, byte Length, uint Asn)>());
        public Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default) => Task.FromResult(0);
        public Task<List<(uint Prefix, byte Length, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default)
            => Task.FromResult(new List<(uint Prefix, byte Length, uint Asn)>());
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetUserSourcePrefixesAsync(string name, string url, string? community, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Reports no configured prefix sources; the delete path never calls it.</summary>
    private sealed class InertPrefixSourceService : IPrefixSourceService
    {
        public Task<IReadOnlyList<(PrefixSourceConfig Source, IReadOnlyList<(uint Prefix, byte Length)> Prefixes)>> LoadAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(PrefixSourceConfig, IReadOnlyList<(uint Prefix, byte Length)>)>>([]);
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetAsync(string name, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetDefaultAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RefreshAsync(string sourceName, CancellationToken ct = default) => Task.FromResult(false);
        public bool SourceSupportsConditional(string sourceName) => false;
    }
}
