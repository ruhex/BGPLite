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
using BGPLite.Protocol;

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

    private async Task<int> StartAsync(AppConfig template, ISessionManager? sessions = null)
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
                sessions ?? new InertSessions());
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
        var id = (await store.SavePeerConfigurationAsync("198.51.100.7", 65090, null, [], [("10.0.0.0", (byte)8)], [])).Id;
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

    [Fact]
    public async Task MaxPrefix_CreateValidate_DetailRoundtrip()
    {
        var config = new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } };
        _port = await StartAsync(config);
        _client = new HttpClient();

        // Negative MaxPrefix is rejected at the boundary.
        using (var bad = await _client.PostAsync($"http://127.0.0.1:{_port}/api/peers",
            new StringContent("""{"ip":"198.51.100.8","asn":65011,"maxPrefix":-1}""", Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Create with an override → the response echoes MaxPrefix and carries the durable id.
        string peerId;
        using (var ok = await _client.PostAsync($"http://127.0.0.1:{_port}/api/peers",
            new StringContent("""{"ip":"198.51.100.8","asn":65011,"maxPrefix":5000}""", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            using var doc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
            peerId = doc.RootElement.GetProperty("id").GetString()!;
            Assert.Equal(5000, doc.RootElement.GetProperty("maxPrefix").GetInt32());
        }

        // Sanity: the created peer must be readable by id immediately.
        using (var sanity = await _client.GetAsync($"http://127.0.0.1:{_port}/api/peers/{peerId}"))
            Assert.True(sanity.IsSuccessStatusCode, $"sanity get: {(int)sanity.StatusCode}");

        // PUT /api/peers/{id} — MaxPrefix PATCH-style: omitted leaves it; explicit 0 sets
        // unlimited-for-peer. (The update route is PUT; field semantics are partial.)
        using (var put = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{_port}/api/peers/{peerId}")
        {
            Content = new StringContent("""{"maxPrefix":0}""", Encoding.UTF8, "application/json")
        })
        using (var response = await _client.SendAsync(put))
            Assert.True(response.IsSuccessStatusCode,
                $"put: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()} peerId='{peerId}'");

        using (var detail = await _client.GetAsync($"http://127.0.0.1:{_port}/api/peers/{peerId}"))
        {
            var json = await detail.Content.ReadAsStringAsync();
            Assert.True(detail.IsSuccessStatusCode, $"detail: {(int)detail.StatusCode} {json} peerId={peerId}");
            Assert.Contains("\"maxPrefix\":0", json);
        }
    }

    [Fact]
    public async Task Md5Password_CreateEnabled_SecretNeverEchoed_UpdateClears()
    {
        var config = new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } };
        _port = await StartAsync(config);
        _client = new HttpClient();
        const string secret = "tcp-md5-s3cret";

        // Create with a password → tcpMd5 flag on; the secret itself is never echoed.
        string peerId;
        using (var ok = await _client.PostAsync($"http://127.0.0.1:{_port}/api/peers",
            new StringContent($$"""{"ip":"198.51.100.9","asn":65012,"md5Password":"{{secret}}"}""", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            var body = await ok.Content.ReadAsStringAsync();
            Assert.Contains(""""tcpMd5":true"""", body);
            Assert.DoesNotContain(secret, body);
            peerId = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
        }

        // Detail: flag on, secret absent.
        using (var detail = await _client.GetAsync($"http://127.0.0.1:{_port}/api/peers/{peerId}"))
        {
            var json = await detail.Content.ReadAsStringAsync();
            Assert.Contains(""""tcpMd5":true"""", json);
            Assert.DoesNotContain(secret, json);
        }

        // Too-long password (> 80 UTF-8 bytes) → 400, and the secret is not echoed back.
        using (var bad = await _client.PostAsync($"http://127.0.0.1:{_port}/api/peers",
            new StringContent($$"""{"ip":"198.51.100.10","asn":65013,"md5Password":"{{new string('x', 81)}}"}""", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.DoesNotContain(new string('x', 81), await bad.Content.ReadAsStringAsync());
        }

        // Update with "" → cleared back to plain TCP.
        using (var put = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{_port}/api/peers/{peerId}")
        {
            Content = new StringContent("""{"md5Password":""}""", Encoding.UTF8, "application/json")
        })
        using (var response = await _client.SendAsync(put))
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        using (var detail = await _client.GetAsync($"http://127.0.0.1:{_port}/api/peers/{peerId}"))
        {
            var json = await detail.Content.ReadAsStringAsync();
            Assert.Contains(""""tcpMd5":false"""", json);
            Assert.DoesNotContain(secret, json);
        }
    }

    [Fact]
    public async Task CreatePeer_SecondKeyOnSharedIp_ArmsTheDeterministicResolverKey()
    {
        // #455: pre-fix the create path armed the NEW row's key directly (last-writer-wins across
        // the shared source IP, no disagreement warning) while delete/PATCH/bootstrap resolved
        // through RearmPeerIpMd5KeyAsync/ResolveSharedIpKey. Create goes through the same resolver:
        // with "key-a" already keyed on the IP, creating a sibling with "key-b" must arm the
        // deterministic ordinal pick ("key-a"), not the new row's key.
        var sessions = new RecordingSessions();
        _port = await StartAsync(
            new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } },
            sessions);
        _client = new HttpClient();

        using (var first = await _client.PostAsync($"http://127.0.0.1:{_port}/api/peers",
            new StringContent("""{"ip":"203.0.113.11","asn":65001,"md5Password":"key-a"}""", Encoding.UTF8, "application/json")))
            Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        using (var second = await _client.PostAsync($"http://127.0.0.1:{_port}/api/peers",
            new StringContent("""{"ip":"203.0.113.11","asn":65002,"md5Password":"key-b"}""", Encoding.UTF8, "application/json")))
            Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());

        var armed = sessions.Md5Keys.Last(k => k.Password is not null);
        Assert.Equal("203.0.113.11", armed.Ip);
        Assert.Equal("key-a", armed.Password);
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
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetPrefixesAsync(uint asn, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>>([]);
        public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default) => Task.FromResult(new List<(UInt128, byte, bool, uint)>());
        public Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default) => Task.FromResult(0);
        public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default) => Task.FromResult(new List<(UInt128, byte, bool, uint)>());
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>>([]);
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetUserSourcePrefixesAsync(string name, string url, string? community, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>>([]);
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class InertPrefixSources : IPrefixSourceService
    {

        public event Action<string>? ContentCommitted;
        public Task<IReadOnlyList<(PrefixSourceConfig Source, IReadOnlyList<IpPrefix> Prefixes)>> LoadAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(PrefixSourceConfig, IReadOnlyList<IpPrefix>)>>([]);
        public Task<IReadOnlyList<IpPrefix>> GetAsync(string name, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IpPrefix>>([]);
        public Task<(IReadOnlyList<IpPrefix> Prefixes, bool Changed)> LoadDefaultAsync(CancellationToken ct = default) => Task.FromResult<(IReadOnlyList<IpPrefix>, bool)>(([], false));
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RefreshAsync(string sourceName, CancellationToken ct = default) => Task.FromResult(false);
        public bool SourceSupportsConditional(string sourceName) => false;
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

    /// <summary>Records SetPeerMd5Key calls so a test can assert WHAT was armed (#455).</summary>
    private sealed class RecordingSessions : ISessionManager
    {
        public List<(string Ip, string? Password)> Md5Keys { get; } = [];
        public Task RefreshPeerAsync(string peerIp, uint asn) => Task.CompletedTask;
        public List<string> GetActivePeerIps() => [];
        public Task TerminatePeerAsync(string peerIp, uint asn, CancellationToken ct = default) => Task.CompletedTask;
        public Task TerminatePeerByIpAsync(string peerIp, CancellationToken ct = default) => Task.CompletedTask;
        public void SetPeerMd5Key(string peerIp, string? password) => Md5Keys.Add((peerIp, password));
        public int GetAdvertisedPrefixCount(string peerIp, uint asn) => 0;
        public Task RefreshAllEstablishedAsync() => Task.CompletedTask;
    }
}
