using System.Net;
using BGPLite.Api;
using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Providers;
using BGPLite.Protocol;
using BGPLite.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// #454: <c>GET /api/as/{asn}/prefixes?count=true</code> triggers a cold RIPEstat fetch that is
/// minutes-scale, and pre-fix ran against the shutdown token only — one (or a few) such GETs
/// pinned in-flight slots (global cap 64) for the full fetch chain. #424 bounded the other two
/// cold-fetch GET endpoints with <see cref="ManagementApi.ExternalFetchBudget"/>; this endpoint
/// gets the same wall-clock budget, answering a stable 503 on expiry (a count cannot degrade to
/// a partial list) while shutdown still propagates.
/// </summary>
public sealed class AsnPrefixesBudgetTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private ManagementApi? _api;

    public AsnPrefixesBudgetTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using (var boot = new BgpDbContext(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options))
            BgpDbContext.Initialize(boot);
    }

    public void Dispose()
    {
        try { _api?.StopAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(5)); } catch { /* best-effort */ }
        _api?.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task AsnPrefixesCount_ColdFetchExceedsBudget_AnswersWithinBudget()
    {
        var port = FreeTcpPort();
        var config = new AppConfig
        {
            Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" },
            ApiListen = "127.0.0.1",
            ApiPort = port,
        };
        _api = new ManagementApi(
            new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options)),
            new RouteTable(),
            config,
            new BgpMetrics(),
            NullLogger<ManagementApi>.Instance,
            new HangingPrefixService(),
            new InertSources(),
            new InertSessions());
        await _api.StartAsync(CancellationToken.None);
        _api.ExternalFetchBudget = TimeSpan.FromMilliseconds(300);

        using var client = new HttpClient();
        // RED (pre-#454): the hanging fetch pinned the request forever — this WaitAsync fired.
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/api/as/65001/prefixes?count=true")
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("RIPEstat", body); // non-revealing error policy (#157)
    }

    private static int FreeTcpPort()
    {
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    /// <summary>Cold-fetch stand-in: GetPrefixCountAsync never completes unless cancelled —
    /// exactly the shape of a RIPEstat request against a blackholed upstream.</summary>
    private sealed class HangingPrefixService : IPrefixService
    {
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetPrefixesAsync(uint asn, CancellationToken ct = default) =>
            Task.Delay(Timeout.InfiniteTimeSpan, ct).ContinueWith<IReadOnlyList<(UInt128, byte, bool)>>(_ => [], ct, TaskContinuationOptions.OnlyOnCanceled, TaskScheduler.Default);
        public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default) => Task.FromResult(new List<(UInt128, byte, bool, uint)>());
        public async Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
        public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default) => Task.FromResult(new List<(UInt128, byte, bool, uint)>());
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(UInt128, byte, bool)>>([]);
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetUserSourcePrefixesAsync(string name, string url, string? community, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(UInt128, byte, bool)>>([]);
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class InertSources : IPrefixSourceService
    {
        public Task<IReadOnlyList<(PrefixSourceConfig Source, IReadOnlyList<IpPrefix> Prefixes)>> LoadAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(PrefixSourceConfig, IReadOnlyList<IpPrefix>)>>([]);
        public Task<IReadOnlyList<IpPrefix>> GetAsync(string name, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IpPrefix>>([]);
        public Task<(IReadOnlyList<IpPrefix> Prefixes, bool Changed)> LoadDefaultAsync(CancellationToken ct = default) => Task.FromResult<(IReadOnlyList<IpPrefix>, bool)>(([], false));
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RefreshAsync(string sourceName, CancellationToken ct = default) => Task.FromResult(false);
        public bool SourceSupportsConditional(string sourceName) => true;
        public event Action<string>? ContentCommitted;
    }

    private sealed class InertSessions : ISessionManager
    {
        public Task RefreshPeerAsync(string peerIp, uint asn) => Task.CompletedTask;
        public List<string> GetActivePeerIps() => [];
        public Task TerminatePeerAsync(string peerIp, uint asn, CancellationToken ct = default) => Task.CompletedTask;
        public Task TerminatePeerByIpAsync(string peerIp, CancellationToken ct = default) => Task.CompletedTask;
        public void SetPeerMd5Key(string peerIp, string? password) { }
        public int GetAdvertisedPrefixCount(string peerIp, uint asn) => 0;
        public Task RefreshAllEstablishedAsync() => Task.CompletedTask;
    }

    private sealed class StaticOptionsFactory(DbContextOptions<BgpDbContext> options) : IDbContextFactory<BgpDbContext>
    {
        public BgpDbContext CreateDbContext() => new(options);
    }
}
