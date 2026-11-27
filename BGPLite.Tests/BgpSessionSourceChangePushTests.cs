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
/// A session's own route build can commit a source change, which starts the fleet push — and the
/// fleet includes this session. The build must not wait for the push: with a push that never
/// completes, the session still advertises, and the lap the push asks of it is still served.
/// </summary>
public sealed class BgpSessionSourceChangePushTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public BgpSessionSourceChangePushTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var boot = new BgpDbContext(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options);
        BgpDbContext.Initialize(boot);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task InitialDump_CommittingASourceChange_DoesNotWaitForTheFleetPush()
    {
        var config = new AppConfig
        {
            Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 },
            PrefixSources = [new PrefixSourceConfig { Name = "apple", Kind = "stub" }]
        };
        var push = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BgpSession session = null!;
        // The real source service behind the real assembler: the first load of "apple" is a
        // content change, which starts the push.
        var sources = new PrefixSourceService(
            config, new PrefixSourceProviderFactory([new FixedProvider([new IpPrefix(0x0A0A0000u, 16)])]),
            NullLogger<PrefixSourceService>.Instance,
            // The production push refreshes every established session — this one included — and
            // here it never completes.
            onSourceChanged: _ => { session?.RefreshRoutesAsync(); return push.Task; });
        var prefixes = new PrefixService(
            config,
            new RipeStatPrefixCache(new RipeStatProvider(new ThrowingFactory(), NullLogger<RipeStatProvider>.Instance), NullLogger<RipeStatPrefixCache>.Instance),
            sources,
            null!);   // the HTTP provider is only on the per-peer user-source path
        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));
        await store.SavePeerConfigurationAsync("127.0.0.1", 65002, null, ["apple"], [], []);
        var assembler = new RouteAssembler(
            prefixes, store, new ConfigCommunityResolver(config, config.Bgp),
            AllowAllFilter.Instance, config, config.Bgp, NullLogger<RouteAssembler>.Instance);

        var conn = new ScriptedConnection();
        session = new BgpSession(
            conn, new PeerConfig { Address = "127.0.0.1" }, config.Bgp, new RouteTable(),
            AllowAllFilter.Instance, new BgpMetrics(), NullLogger<BgpSession>.Instance, routeAssembler: assembler);
        var run = session.RunAsync();
        conn.EnqueueMessage(new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = 0,
            RouterId = 0x0A000002,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)]
        });
        conn.EnqueueMessage(BgpKeepaliveMessage.Instance);

        // The initial dump advertised the source although the push it started never completed.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (session.AdvertisedPrefixCount < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(1, session.AdvertisedPrefixCount);   // pre-fix: 0 — the dump waits for the push

        // The lap the push asked of this session is served once the push is let go: a withdraw
        // and a re-announcement of the same prefix.
        push.TrySetResult();
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        List<BgpUpdateMessage> updates;
        do
        {
            await Task.Delay(10);
            updates = conn.Sent.Select(f => BgpMessageReader.ReadMessage(f)).OfType<BgpUpdateMessage>().ToList();
        } while (!updates.Any(u => u.WithdrawnRoutes.Count > 0) && DateTime.UtcNow < deadline);
        Assert.Contains(updates, u => u.WithdrawnRoutes.Count > 0);
        Assert.Equal(1, session.AdvertisedPrefixCount);

        session.Dispose();
        try { await run.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
    }

    private sealed class FixedProvider(IReadOnlyList<IpPrefix> list) : IPrefixSourceProvider
    {
        public string Kind => "stub";
        public bool SupportsConditionalRequests => false;
        public Task<SourceLoadResult> LoadAsync(PrefixSourceConfig source, string? etag = null, DateTimeOffset? lastModified = null, CancellationToken ct = default) =>
            Task.FromResult(SourceLoadResult.Ok(list));
    }

    /// <summary>RIPEstat is never reached in this test; a request that tried would fail loudly.</summary>
    private sealed class ThrowingFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException("RIPEstat is not on this path");
    }

    private sealed class StaticOptionsFactory(DbContextOptions<BgpDbContext> options) : IDbContextFactory<BgpDbContext>
    {
        public BgpDbContext CreateDbContext() => new(options);
    }
}
