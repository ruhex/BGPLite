using System.Net;
using System.Net.Sockets;
using BGPLite.Api;
using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Providers;
using BGPLite.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// #326: StopAsync must not hang behind in-flight handlers. Handlers observe a shutdown token on
/// provider calls and the drain is bounded — the host stops services in reverse registration
/// order, so an unbounded ManagementApi drain held back BgpServer.StopAsync's Cease teardown
/// (in Docker the process was SIGKILLed after the 10 s grace: peers saw TCP RST, not Cease).
/// </summary>
public sealed class ManagementApiShutdownTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private ManagementApi? _api;
    private HttpClient? _client;
    private int _port;

    public ManagementApiShutdownTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using (var boot = new BgpDbContext(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options))
            BgpDbContext.Initialize(boot);
    }

    public void Dispose()
    {
        // StopAsync cancels the shutdown token, so a handler parked in the provider (e.g. when the
        // test failed before reaching its own StopAsync) unwinds here instead of leaking the
        // connection and its in-flight slot for the rest of the test run.
        try { _api?.StopAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(5)); }
        catch { /* best-effort cleanup */ }
        _api?.Dispose();
        _client?.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task StopAsync_CancelsInFlightProviderFetch_AndDrainsWithinBound()
    {
        var provider = new BlockingRuPrefixService();
        // FreeTcpPort has an inherent TOCTOU window — retry on a fresh port if something races us
        // to the bind (bounded; each failed attempt disposes its listener).
        for (var attempt = 0; ; attempt++)
        {
            var port = FreeTcpPort();
            var config = new AppConfig
            {
                ApiListen = "127.0.0.1",
                ApiPort = port,
                Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" },
                RipeStat = new RipeStatConfig { AsnLists = [new AsnList { Name = "ru", Country = "RU" }] }
            };
            _api = new ManagementApi(
                new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options)),
                new RouteTable(),
                config,
                new BgpMetrics(),
                NullLogger<ManagementApi>.Instance,
                provider,
                new InertPrefixSourceService(),
                new InertSessionManager());
            try
            {
                await _api.StartAsync(CancellationToken.None);
                _port = port;
                break;
            }
            catch (HttpListenerException) when (attempt < 2)
            {
                _api.Dispose();
                _api = null;
            }
        }
        _client = new HttpClient();

        // /api/asn-lists hits the provider for the country list — the handler parks inside
        // GetRuPrefixesAsync until the shutdown token fires.
        var request = _client.GetAsync($"http://127.0.0.1:{_port}/api/asn-lists");
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = _api.StopAsync(CancellationToken.None);

        // Old behavior: the drain awaited Task.WhenAll(pending) with no cancellation and the
        // provider never completed on its own — StopAsync hung (RIPEstat worst case: minutes),
        // starving BgpServer.StopAsync's Cease teardown behind it.
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stop.IsCompleted);

        // The abandoned request unwinds as OCE → the handler completes; the response is closed by
        // the listener teardown, so the client sees an error — bounded, never the 100 s default.
        try { await request.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { /* expected — the server is shutting down */ }
    }

    [Fact]
    public async Task StartAsync_Retries_WhenThePortIsTaken()
    {
        // FreeTcpPort has an inherent TOCTOU window — a concurrent bind between the probe and
        // HttpListener.Start must surface as a clear HttpListenerException, not a hang.
        var port = FreeTcpPort();
        var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();
        var config = new AppConfig
        {
            ApiListen = "127.0.0.1",
            ApiPort = port,
            Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" }
        };
        _api = new ManagementApi(
            new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options)),
            new RouteTable(),
            config,
            new BgpMetrics(),
            NullLogger<ManagementApi>.Instance,
            new BlockingRuPrefixService(),
            new InertPrefixSourceService(),
            new InertSessionManager());

        await Assert.ThrowsAnyAsync<HttpListenerException>(() => _api.StartAsync(CancellationToken.None));

        blocker.Stop();
    }

    /// <summary>
    /// #258: completed handlers must leave the in-flight set — the #248 bookkeeping appended
    /// every request's Task and never removed it, so the tracking grew monotonically with request
    /// count (a slow memory leak plus an ever-growing drain snapshot). After a burst of requests
    /// the in-flight count must return to exactly zero.
    /// </summary>
    [Fact]
    public async Task CompletedRequests_LeaveTheInFlightSet()
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = FreeTcpPort();
            var config = new AppConfig
            {
                ApiListen = "127.0.0.1",
                ApiPort = port,
                Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" }
            };
            _api = new ManagementApi(
                new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options)),
                new RouteTable(),
                config,
                new BgpMetrics(),
                NullLogger<ManagementApi>.Instance,
                new BlockingRuPrefixService(),
                new InertPrefixSourceService(),
                new InertSessionManager());
            try
            {
                await _api.StartAsync(CancellationToken.None);
                _port = port;
                break;
            }
            catch (HttpListenerException) when (attempt < 2)
            {
                _api.Dispose();
                _api = null;
            }
        }
        _client = new HttpClient();

        for (var i = 0; i < 25; i++)
        {
            using var response = await _client.GetAsync($"http://127.0.0.1:{_port}/api/sessions");
            Assert.True(response.IsSuccessStatusCode);
        }

        // The handler's finally (response close + slot release) runs just after the response is
        // observed — settle within a bounded window, then demand exactly zero.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (_api.InflightRequestCount > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.Equal(0, _api.InflightRequestCount);
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Parks inside GetRuPrefixesAsync until the caller's token fires; every other member is inert.</summary>
    private sealed class BlockingRuPrefixService : IPrefixService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetPrefixesAsync(uint asn, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task<List<(uint Prefix, byte Length, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default)
            => Task.FromResult(new List<(uint Prefix, byte Length, uint Asn)>());
        public Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default) => Task.FromResult(0);
        public async Task<List<(uint Prefix, byte Length, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default)
        {
            // Yield BEFORE signalling: the whole chain from the accept loop into this provider is
            // synchronous up to the first await, so without this the Entered signal could fire
            // before ListenAsync has even added the handler task to _inflightHandlers — and
            // StopAsync would observe an empty drain without testing anything.
            await Task.Yield();
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);   // never completes on its own; honors the token
            return [];
        }
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetUserSourcePrefixesAsync(string name, string url, string? community, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Reports no configured prefix sources; the shutdown path never calls it.</summary>
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

    /// <summary>Holds no sessions; the shutdown path never calls it.</summary>
    private sealed class InertSessionManager : ISessionManager
    {
        public Task RefreshPeerAsync(string peerIp, uint asn) => Task.CompletedTask;
        public List<string> GetActivePeerIps() => [];
        public int GetAdvertisedPrefixCount(string peerIp, uint asn) => 0;
        public Task RefreshAllEstablishedAsync() => Task.CompletedTask;
        public Task TerminatePeerAsync(string peerIp, uint asn, CancellationToken ct = default) => Task.CompletedTask;
        public void SetPeerMd5Key(string peerIp, string? password) { }
    }

    private sealed class StaticOptionsFactory(DbContextOptions<BgpDbContext> options) : IDbContextFactory<BgpDbContext>
    {
        public BgpDbContext CreateDbContext() => new(options);
    }
}
