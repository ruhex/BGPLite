using System.Net;
using System.Text;
using System.Text.Json;
using BGPLite.Api;
using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Providers;
using BGPLite.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BGPLite.Tests;

/// <summary>
/// #266 handler-level behavior that needs a real listener: the txt prefix export must not be
/// double-serialized (item 1), the CORS preflight must advertise PATCH (item 2), OPTIONS
/// preflights consume the client's rate bucket instead of bypassing it (item 7), and a reloaded
/// MaxRequestBodyBytes applies to subsequent requests without a restart (item 6).
/// </summary>
public sealed class ApiHandlerBehaviorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private ManagementApi? _api;
    private HttpClient? _client;
    private int _port;

    public ApiHandlerBehaviorTests()
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
        _client?.Dispose();
        _connection.Dispose();
    }

    private async Task<int> StartAsync(AppConfig template)
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = FreeTcpPort();
            var config = new AppConfig
            {
                Bgp = template.Bgp,
                CorsAllowedOrigins = template.CorsAllowedOrigins,
                ApiRateLimit = template.ApiRateLimit,
                ApiListen = "127.0.0.1",
                ApiPort = port,
            };
            _api = new ManagementApi(
                new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options)),
                new RouteTable(),
                config,
                new BgpMetrics(),
                NullLogger<ManagementApi>.Instance,
                new InertPrefixService(),
                new InertPrefixSources(),
                new InertSessions());
            try
            {
                await _api.StartAsync(CancellationToken.None);
                return port;
            }
            catch (HttpListenerException) when (attempt < 2)
            {
                _api.Dispose();
                _api = null;
            }
        }
    }

    [Fact]
    public async Task ExportTxt_PlaintextBody_NotJsonQuoted()
    {
        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));
        var id = store.SavePeerConfiguration("198.51.100.7", 65090, null, [], [("10.0.0.0", (byte)8)], []);
        _port = await StartAsync(new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } });
        _client = new HttpClient();

        using var response = await _client.GetAsync($"http://127.0.0.1:{_port}/api/peers/{id}/prefixes?format=txt");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("10.0.0.0/8", body.TrimEnd());   // RED pre-fix: "\"10.0.0.0/8\\n\"" with application/json
    }

    [Fact]
    public async Task OptionsPreflight_AdvertisesPatch_AndConsumesRateBucket()
    {
        var config = new AppConfig
        {
            Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" },
            CorsAllowedOrigins = ["http://example.com"],
            ApiRateLimit = new ApiRateLimitConfig { Enabled = true, TokenLimit = 2, TokensPerPeriod = 1000, PeriodSeconds = 3600 },
        };
        _port = await StartAsync(config);
        _client = new HttpClient();

        var preflight = new HttpRequestMessage(HttpMethod.Options, $"http://127.0.0.1:{_port}/api/peers");
        preflight.Headers.Add("Origin", "http://example.com");
        using var first = await _client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Contains("PATCH", first.Headers.GetValues("Access-Control-Allow-Methods").First()); // RED pre-fix: no PATCH

        var second = new HttpRequestMessage(HttpMethod.Options, $"http://127.0.0.1:{_port}/api/peers");
        second.Headers.Add("Origin", "http://example.com");
        using var ok = await _client.SendAsync(second);
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);   // 2nd — bucket still has tokens

        var third = new HttpRequestMessage(HttpMethod.Options, $"http://127.0.0.1:{_port}/api/peers");
        third.Headers.Add("Origin", "http://example.com");
        using var limited = await _client.SendAsync(third);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);   // RED pre-fix: 204 forever
    }

    [Fact]
    public async Task MaxRequestBodyBytes_HotReloaded_AppliesToSubsequentRequests()
    {
        var config = new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } };
        _port = await StartAsync(config);
        _client = new HttpClient();

        // ~4 KB body — comfortably under the 1 MiB startup cap. The padding rides in an unknown
        // JSON field (ignored by deserialization) so the request stays semantically valid while
        // the reloaded cap rejects it by size alone.
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ip"] = "198.51.100.5",
            ["asn"] = 65010,
            ["description"] = "hot-reload probe",
            ["padding"] = new string('x', 4096)
        });
        using (var ok = await _client.PostAsync($"http://127.0.0.1:{_port}/api/peers",
            new StringContent(body, Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // Shrink the cap below the body size; the NEXT request is rejected with 413 — no restart.
        _api!.ApplyConfig(new AppConfig { Bgp = config.Bgp, ApiPort = _port, MaxRequestBodyBytes = 1024 });

        using var tooLarge = await _client.PostAsync($"http://127.0.0.1:{_port}/api/peers",
            new StringContent(body, Encoding.UTF8, "application/json"));
        // RED pre-fix: ReadBodyAsync kept reading the startup _config, so the same POST returned 200.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
    }

    private static int FreeTcpPort()
    {
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private sealed class StaticOptionsFactory(DbContextOptions<BgpDbContext> options) : IDbContextFactory<BgpDbContext>
    {
        public BgpDbContext CreateDbContext() => new(options);
    }

    private sealed class InertPrefixService : IPrefixService
    {
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetPrefixesAsync(uint asn, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task<List<(uint Prefix, byte Length, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default) => Task.FromResult(new List<(uint, byte, uint)>());
        public Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default) => Task.FromResult(0);
        public Task<List<(uint Prefix, byte Length, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default) => Task.FromResult(new List<(uint, byte, uint)>());
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetUserSourcePrefixesAsync(string name, string url, string? community, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class InertPrefixSources : IPrefixSourceService
    {
        public Task<IReadOnlyList<(PrefixSourceConfig Source, IReadOnlyList<(uint Prefix, byte Length)> Prefixes)>> LoadAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(PrefixSourceConfig, IReadOnlyList<(uint Prefix, byte Length)>)>>([]);
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetAsync(string name, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetDefaultAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RefreshAsync(string sourceName, CancellationToken ct = default) => Task.FromResult(false);
        public bool SourceSupportsConditional(string sourceName) => false;
    }

    private sealed class InertSessions : ISessionManager
    {
        public Task RefreshPeerAsync(string peerIp, uint asn) => Task.CompletedTask;
        public List<string> GetActivePeerIps() => [];
        public Task TerminatePeerAsync(string peerIp, uint asn, CancellationToken ct = default) => Task.CompletedTask;
        public int GetAdvertisedPrefixCount(string peerIp, uint asn) => 0;
        public Task RefreshAllEstablishedAsync() => Task.CompletedTask;
    }
}
