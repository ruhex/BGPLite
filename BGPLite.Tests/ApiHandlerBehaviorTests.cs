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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using BGPLite.Protocol;

namespace BGPLite.Tests;

/// <summary>
/// Handler-level behavior that needs a real listener: the txt prefix export must not be
/// double-serialized, the CORS preflight must advertise PATCH, OPTIONS
/// preflights consume the client's rate bucket instead of bypassing it, and a reloaded
/// MaxRequestBodyBytes applies to subsequent requests without a restart.
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

    private async Task<int> StartAsync(AppConfig template, ISessionManager? sessions = null, ILogger<ManagementApi>? logger = null, IPrefixSourceService? prefixSources = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = FreeTcpPort();
            var config = new AppConfig
            {
                Bgp = template.Bgp,
                CorsAllowedOrigins = template.CorsAllowedOrigins,
                ApiRateLimit = template.ApiRateLimit,
                RipeStat = template.RipeStat,
                ApiListen = "127.0.0.1",
                ApiPort = port,
            };
            _api = new ManagementApi(
                new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options)),
                new RouteTable(),
                config,
                new BgpMetrics(),
                logger ?? NullLogger<ManagementApi>.Instance,
                new InertPrefixService(),
                prefixSources ?? new InertPrefixSources(),
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
        Assert.Equal("10.0.0.0/8", body.TrimEnd());   // pre-fix: "\"10.0.0.0/8\\n\"" with application/json
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
        Assert.Contains("PATCH", first.Headers.GetValues("Access-Control-Allow-Methods").First()); // pre-fix: no PATCH

        var second = new HttpRequestMessage(HttpMethod.Options, $"http://127.0.0.1:{_port}/api/peers");
        second.Headers.Add("Origin", "http://example.com");
        using var ok = await _client.SendAsync(second);
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);   // 2nd — bucket still has tokens

        var third = new HttpRequestMessage(HttpMethod.Options, $"http://127.0.0.1:{_port}/api/peers");
        third.Headers.Add("Origin", "http://example.com");
        using var limited = await _client.SendAsync(third);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);   // pre-fix: 204 forever
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
        // Pre-fix, ReadBodyAsync kept reading the startup _config, so the same POST returned 200.
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
        // TCP-MD5 arming exists on Linux only — the macOS/unsupported-platform contract
        // (400 on write, tcpMd5:false on read) is pinned by the platform-specific tests below.
        if (!OperatingSystem.IsLinux()) return;
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
        // Pre-fix the create path armed the NEW row's key directly (last-writer-wins across
        // the shared source IP, no disagreement warning) while delete/PATCH/bootstrap resolved
        // through RearmPeerIpMd5KeyAsync/ResolveSharedIpKey. Create goes through the same resolver:
        // with "key-a" already keyed on the IP, creating a sibling with "key-b" must arm the
        // deterministic ordinal pick ("key-a"), not the new row's key.
        // Linux-only: on platforms without TCP-MD5 the API rejects md5Password with 400.
        if (!OperatingSystem.IsLinux()) return;
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

    [Fact]
    public async Task Md5Password_RejectedWith400_WhenPlatformCannotArmIt()
    {
        // XNU has no TCP_MD5SIG (opt 0x10 is TCP_KEEPALIVE): accepting the password and
        // answering tcpMd5:true reported a security feature that never existed on this host.
        if (!OperatingSystem.IsMacOS()) return; // the Linux write path is covered above

        _port = await StartAsync(new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } });
        _client = new HttpClient();

        using var create = await _client.PostAsync($"http://127.0.0.1:{_port}/api/peers",
            new StringContent("""{"ip":"198.51.100.9","asn":65012,"md5Password":"tcp-md5-s3cret"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.DoesNotContain("tcp-md5-s3cret", await create.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TcpMd5Flag_FalseOnUnsupportedPlatform_EvenWhenRowCarriesPassword()
    {
        // A legacy row written before the platform truth was enforced must not report
        // tcpMd5=true — the flag means "this server actually enforces RFC 2385 for the peer".
        if (!OperatingSystem.IsMacOS()) return;

        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));
        var saved = await store.SavePeerConfigurationAsync(
            "198.51.100.11", 65014, null, [], [], [], md5Password: "legacy-secret");

        _port = await StartAsync(new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } });
        _client = new HttpClient();

        using var detail = await _client.GetAsync($"http://127.0.0.1:{_port}/api/peers/{saved.Id}");
        var json = await detail.Content.ReadAsStringAsync();

        Assert.True(detail.IsSuccessStatusCode, $"detail: {(int)detail.StatusCode} {json}");
        Assert.Contains(""""tcpMd5":false"""", json);
        Assert.DoesNotContain("legacy-secret", json);
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

    /// <summary>Records SetPeerMd5Key calls so a test can assert WHAT was armed.</summary>
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

    /// <summary>Captures formatted log messages so a test can assert what must NEVER be logged.</summary>
    private sealed class RecordingLogger : ILogger<ManagementApi>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task AddSource_RejectedUrl_IsNeverLogged()
    {
        // Peer-source URLs may carry query-string tokens — the log events around
        // save-time validation must name the source, never the URL. The loopback host is blocked
        // by the SSRF validator without any network access, so the rejected path fires offline.
        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));
        var id = (await store.SavePeerConfigurationAsync("198.51.100.7", 65090, null, [], [], [])).Id;
        var logger = new RecordingLogger();
        _port = await StartAsync(new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } }, logger: logger);
        _client = new HttpClient();

        var body = JsonSerializer.Serialize(new { name = "leaky", url = "http://127.0.0.1:9/list?token=SECRET123" });
        using var response = await _client.PostAsync($"http://127.0.0.1:{_port}/api/peers/{id}/sources",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);   // the loopback host is rejected
        Assert.DoesNotContain(logger.Messages, m => m.Contains("SECRET123") || m.Contains("token="));
    }

    /// <summary>LoadAllAsync throws a foreign-token OCE — the deterministic stand-in for the
    /// external-fetch budget firing mid-load (live shutdown token, cancelled budget token).</summary>
    private sealed class BudgetExhaustedSources : IPrefixSourceService
    {
        public event Action<string>? ContentCommitted;
        public Task<IReadOnlyList<(PrefixSourceConfig Source, IReadOnlyList<IpPrefix> Prefixes)>> LoadAllAsync(CancellationToken ct = default)
            => throw new OperationCanceledException();
        public Task<IReadOnlyList<IpPrefix>> GetAsync(string name, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IpPrefix>>([]);
        public Task<(IReadOnlyList<IpPrefix> Prefixes, bool Changed)> LoadDefaultAsync(CancellationToken ct = default) => Task.FromResult<(IReadOnlyList<IpPrefix>, bool)>(([], false));
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RefreshAsync(string sourceName, CancellationToken ct = default) => Task.FromResult(false);
        public bool SourceSupportsConditional(string sourceName) => false;
    }

    [Fact]
    public async Task AddSource_FieldLimits_And_PerPeerCap_AreEnforced()
    {
        // Repeated POSTs bounded — name/URL length ceilings and a per-peer source-count cap
        // (the 1 MiB body cap bounds ONE request; the DB rows outlive it).
        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));
        var id = (await store.SavePeerConfigurationAsync("198.51.100.8", 65091, null, [], [], [])).Id;
        _port = await StartAsync(new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } });
        _client = new HttpClient();

        var longName = new string('n', ManagementApi.MaxSourceNameLength + 1);
        using var badName = await _client.PostAsync($"http://127.0.0.1:{_port}/api/peers/{id}/sources",
            new StringContent(JsonSerializer.Serialize(new { name = longName, url = "https://example.com/l" }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badName.StatusCode);

        var longUrl = "https://example.com/" + new string('u', ManagementApi.MaxSourceUrlLength);
        using var badUrl = await _client.PostAsync($"http://127.0.0.1:{_port}/api/peers/{id}/sources",
            new StringContent(JsonSerializer.Serialize(new { name = "ok", url = longUrl }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badUrl.StatusCode);

        // Seed the per-peer cap directly through the store, then POST one more → 400.
        for (var i = 0; i < ManagementApi.MaxSourcesPerPeer; i++)
            await store.AddCustomSourceAsync(id, $"s{i}", $"https://example.com/{i}", null);
        using var capped = await _client.PostAsync($"http://127.0.0.1:{_port}/api/peers/{id}/sources",
            new StringContent(JsonSerializer.Serialize(new { name = "one-too-many", url = "https://example.com/x" }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, capped.StatusCode);
    }

    [Fact]
    public async Task AsnLists_BudgetExpiryMidSources_ServesPartialJson()
    {
        // The budget token firing during LoadAllAsync must degrade to the partial response
        // (the ASN-list entries collected above), not escape the handler and abort the connection
        // without a body.
        _port = await StartAsync(
            new AppConfig
            {
                Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" },
                RipeStat = new RipeStatConfig { AsnLists = [new AsnList { Name = "ru", Country = "RU", Community = "65000:100" }] }
            },
            prefixSources: new BudgetExhaustedSources());
        _client = new HttpClient();

        using var response = await _client.GetAsync($"http://127.0.0.1:{_port}/api/asn-lists");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"expected a partial response, got {(int)response.StatusCode}");   // pre-fix: the connection was aborted (HttpRequestException)
        Assert.Contains("\"ru\"", body);   // the ASN-list half of the response survived
    }

    // ---------------------------------------------------------------- CSRF (#530)

    /// <summary>
    /// A CORS-"simple" cross-origin POST must not mutate the control plane. text/plain and
    /// application/x-www-form-urlencoded are CORS-safelisted, so the browser sends NO preflight and
    /// the missing Access-Control-Allow-Origin on the response does not undo the state change.
    /// The status code alone does not prove the fix — the peer must not exist afterwards.
    /// </summary>
    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/plain; charset=utf-8")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data")]
    public async Task CreatePeer_CorsSimpleContentType_IsRejected_NoPeerCreated(string mediaType)
    {
        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));
        _port = await StartAsync(new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } });
        _client = new HttpClient();

        var body = JsonSerializer.Serialize(new { ip = "203.0.113.66", asn = 65099, description = "csrf-pwned" });
        using var content = new StringContent(body);
        // Set explicitly so a media type carrying parameters (charset) can be exercised too.
        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(mediaType);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}/api/peers")
        {
            Content = content
        };
        request.Headers.Add("Origin", "https://evil.example");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);   // pre-fix: 200 Created
        // The decisive assertion: the attacker-controlled peer must not be in the database.
        Assert.Null(await store.GetPeerAsync("203.0.113.66", 65099));
    }

    [Fact]
    public async Task AddSource_CorsSimpleContentType_IsRejected_NoSourceCreated()
    {
        // The SSRF-relevant route: it makes the server persist and then FETCH an operator-visible
        // URL. A drive-by page must not be able to seed one.
        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));
        var id = (await store.SavePeerConfigurationAsync("198.51.100.7", 65090, null, [], [], [])).Id;
        _port = await StartAsync(new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } });
        _client = new HttpClient();

        var body = JsonSerializer.Serialize(new { name = "pwn", url = "https://evil.example/list.txt" });
        using var content = new StringContent(body, Encoding.UTF8, "text/plain");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}/api/peers/{id}/sources")
        {
            Content = content
        };
        request.Headers.Add("Origin", "https://evil.example");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(await store.GetCustomSourcesAsync(id));
    }

    [Fact]
    public async Task CreatePeer_CrossSiteFetchMetadata_IsRejected_EvenWithJson()
    {
        // Defence in depth: Sec-Fetch-Site is set by the browser and needs no allowlist, so a
        // cross-site request is refused even if a client skips preflighting.
        _port = await StartAsync(new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } });
        _client = new HttpClient();

        var body = JsonSerializer.Serialize(new { ip = "203.0.113.77", asn = 65098, description = "pwned" });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}/api/peers")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreatePeer_SameOriginFetchMetadata_IsAccepted()
    {
        // The operator's own UI is same-origin; it must keep working.
        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));
        _port = await StartAsync(new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } });
        _client = new HttpClient();

        var body = JsonSerializer.Serialize(new { ip = "198.51.100.9", asn = 65097, description = "legit" });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}/api/peers")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        using var response = await _client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode, $"got {(int)response.StatusCode}");
        Assert.NotNull(await store.GetPeerAsync("198.51.100.9", 65097));
    }

    [Fact]
    public async Task CreatePeer_JsonContentType_WithoutFetchMetadata_IsAccepted()
    {
        // CLI / curl / script clients send no Sec-Fetch-Site at all. They must not be broken.
        _port = await StartAsync(new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } });
        _client = new HttpClient();

        var body = JsonSerializer.Serialize(new { ip = "198.51.100.10", asn = 65096, description = "cli" });
        using var response = await _client.PostAsync($"http://127.0.0.1:{_port}/api/peers",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode, $"got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Preflight_ForJson_IsAnswered_SoConfiguredCrossOriginUiKeepsWorking()
    {
        // The Content-Type requirement turns a cross-origin JSON POST into a preflighted request.
        // A legitimately configured cross-origin UI must still get its CORS headers back, or the
        // fix would break it. AddCorsHeaders runs before the OPTIONS short-circuit for this reason.
        _port = await StartAsync(new AppConfig
        {
            Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" },
            CorsAllowedOrigins = ["http://example.com"],
        });
        _client = new HttpClient();

        var preflight = new HttpRequestMessage(HttpMethod.Options, $"http://127.0.0.1:{_port}/api/peers");
        preflight.Headers.Add("Origin", "http://example.com");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
        using var response = await _client.SendAsync(preflight);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("http://example.com", response.Headers.GetValues("Access-Control-Allow-Origin").First());
        Assert.Contains("POST", response.Headers.GetValues("Access-Control-Allow-Methods").First());
    }

    [Fact]
    public async Task DeletePeer_CrossSiteFetchMetadata_IsRejected_PeerSurvives()
    {
        // The Sec-Fetch-Site guard lives in HandleAsync, not in ReadBodyAsync: DELETE reads no
        // body, so a guard inside ReadBodyAsync would silently skip this route. A browser
        // preflights DELETE, but CorsAllowedOrigins is operator-configured — an allowlisted
        // origin must not become a licence to delete peers cross-origin.
        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));
        var id = (await store.SavePeerConfigurationAsync("198.51.100.20", 65090, null, [], [], [])).Id;
        _port = await StartAsync(new AppConfig
        {
            Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" },
            CorsAllowedOrigins = ["http://example.com"],
        });
        _client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"http://127.0.0.1:{_port}/api/peers/{id}")
        {
            Content = new StringContent(string.Empty)
        };
        request.Headers.Add("Origin", "http://example.com");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(await store.GetDbPeerByIdAsync(id));   // the peer must still be there
    }

    [Fact]
    public async Task DeletePeer_SameOrigin_NoContentType_IsAccepted()
    {
        // DELETE carries no body, so it must not be forced to send a JSON media type — only the
        // body-reading routes require one.
        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));
        var id = (await store.SavePeerConfigurationAsync("198.51.100.21", 65090, null, [], [], [])).Id;
        _port = await StartAsync(new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } });
        _client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"http://127.0.0.1:{_port}/api/peers/{id}")
        {
            Content = new StringContent(string.Empty)
        };
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        using var response = await _client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode, $"got {(int)response.StatusCode}");
        Assert.Null(await store.GetDbPeerByIdAsync(id));
    }

    [Theory]
    [InlineData("application/json", true)]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("APPLICATION/JSON", true)]
    [InlineData("application/merge-patch+json", true)]
    [InlineData("text/plain", false)]
    [InlineData("application/x-www-form-urlencoded", false)]
    [InlineData("multipart/form-data", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsJsonContentType_AcceptsOnlyJsonMediaTypes(string? contentType, bool expected)
        => Assert.Equal(expected, ManagementApi.IsJsonContentType(contentType));
}