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

        try
        {
            // The initial dump advertised the source although the push never completes.
            Assert.True(await SettledAsync(() => session.AdvertisedPrefixCount == 1),
                "the dump must advertise although the push never completes");   // pre-fix: the dump waits for the push

            // The lap the push asks of this session is not lost: deferred behind the dump, or
            // started after it, it withdraws and re-announces the same prefix.
            Assert.True(await SettledAsync(() => Updates().Any(u => u.WithdrawnRoutes.Count > 0)),
                "the lap the push asks of the session must still run");
            Assert.Equal(1, session.AdvertisedPrefixCount);
        }
        finally
        {
            push.TrySetResult();
            session.Dispose();
            try { await run.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
        }

        List<BgpUpdateMessage> Updates() =>
            conn.Sent.Select(f => BgpMessageReader.ReadMessage(f)).OfType<BgpUpdateMessage>().ToList();

        // Waits until the state holds and no dump or refresh is running on the session: the count
        // reads 0 between a withdraw and its re-announcement.
        async Task<bool> SettledAsync(Func<bool> state)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (state() && !session.RefreshInProgress) return true;
                await Task.Delay(10);
            }
            return false;
        }
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
