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

    /// <summary>Starts an isolated real HTTP/SQLite fixture with optional dependency and independent-connection overrides.</summary>
    private async Task<int> StartAsync(AppConfig template, ISessionManager? sessions = null, ILogger<ManagementApi>? logger = null, IPrefixSourceService? prefixSources = null, IPrefixService? prefixService = null, DbContextOptions<BgpDbContext>? dbOptions = null)
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
                PrefixSources = template.PrefixSources,
                DefaultPrefixSource = template.DefaultPrefixSource,
                ApiListen = "127.0.0.1",
                ApiPort = port,
            };
            var store = new PeerStore(new StaticOptionsFactory(dbOptions ?? new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));
            IPrefixService prefixes = prefixService ?? new InertPrefixService();
            _api = new ManagementApi(
                store,
                new RouteTable(),
                config,
                new BgpMetrics(),
                logger ?? NullLogger<ManagementApi>.Instance,
                prefixes,
                prefixSources ?? new InertPrefixSources(),
                sessions ?? new InertSessions(),
                TestRouteAssembly.CreateExporter(store, config, prefixes));
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

    /// <summary>Verifies that text export returns raw newline-delimited CIDRs rather than JSON-encoded strings.</summary>
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

    /// <summary>Compares JSON export with the production assembler across subscriptions, custom values, and active user sources.</summary>
    [Fact]
    public async Task ExportJson_UsesCompleteOutboundPipeline()
    {
        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));
        var saved = await store.SavePeerConfigurationAsync(
            "198.51.100.7", 65090, null, ["asn", "configured"], [("198.51.100.0", (byte)24)], [65020]);
        var userSource = await store.AddCustomSourceAsync(saved.Id, "user", "https://example.com/user.txt", null);
        await store.SetSourceActiveAsync(saved.Id, userSource.Id, true);
        var prefixes = new ExportPrefixService();
        var config = new AppConfig
        {
            Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" },
            RipeStat = new RipeStatConfig { AsnLists = [new AsnList { Name = "asn", Asns = [65010] }] },
            PrefixSources = [new PrefixSourceConfig { Name = "configured", Kind = "file", Path = "nets.txt" }]
        };
        _port = await StartAsync(config, prefixService: prefixes);
        _client = new HttpClient();

        using var response = await _client.GetAsync($"http://127.0.0.1:{_port}/api/peers/{saved.Id}/prefixes?format=json");
        var body = await response.Content.ReadAsStringAsync();
        var exported = JsonSerializer.Deserialize<string[]>(body);

        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Equal([
            "10.10.0.0/16", "10.20.0.0/16", "172.16.0.0/12", "192.0.2.0/24", "198.51.100.0/24"
        ], exported!.OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>Verifies that source failure returns 503 without leaking custom or otherwise surviving prefixes.</summary>
    [Fact]
    public async Task ExportJson_SourceFailure_Returns503WithoutPartialPrefixes()
    {
        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));
        var saved = await store.SavePeerConfigurationAsync(
            "198.51.100.8", 65091, null, ["asn"], [("198.51.100.0", (byte)24)], []);
        var config = new AppConfig
        {
            Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" },
            RipeStat = new RipeStatConfig { AsnLists = [new AsnList { Name = "asn", Asns = [65010] }] }
        };
        _port = await StartAsync(config, prefixService: new ExportPrefixService { FailAsns = true });
        _client = new HttpClient();

        using var response = await _client.GetAsync($"http://127.0.0.1:{_port}/api/peers/{saved.Id}/prefixes?format=json");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("no partial list", body);
        Assert.DoesNotContain("198.51.100.0", body);
    }

    /// <summary>Verifies that a mixed ASN result is rejected without exposing surviving prefixes or failure details.</summary>
    [Fact]
    public async Task ExportJson_PartialAsnFailure_Returns503WithoutSurvivingPrefixes()
    {
        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));
        var saved = await store.SavePeerConfigurationAsync(
            "198.51.100.9", 65092, null, ["asn"], [], []);
        var config = new AppConfig
        {
            Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" },
            RipeStat = new RipeStatConfig { AsnLists = [new AsnList { Name = "asn", Asns = [65010, 65020] }] }
        };
        _port = await StartAsync(config, prefixService: new ExportPrefixService { PartialAsns = true });
        _client = new HttpClient();

        using var response = await _client.GetAsync($"http://127.0.0.1:{_port}/api/peers/{saved.Id}/prefixes?format=json");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("no partial list", body);
        Assert.DoesNotContain("10.10.0.0", body);
        Assert.DoesNotContain("65020", body);
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

    /// <summary>Verifies that sibling credentials use the deterministic shared-IP key rather than last-writer-wins.</summary>
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

        string firstId;
        using (var first = await _client.PostAsync($"http://127.0.0.1:{_port}/api/peers",
            new StringContent("""{"ip":"203.0.113.11","asn":65001,"md5Password":"key-a"}""", Encoding.UTF8, "application/json")))
        {
            var body = await first.Content.ReadAsStringAsync();
            Assert.True(first.IsSuccessStatusCode, body);
            firstId = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
        }
        string secondId;
        using (var second = await _client.PostAsync($"http://127.0.0.1:{_port}/api/peers",
            new StringContent("""{"ip":"203.0.113.11","asn":65002,"md5Password":"key-b"}""", Encoding.UTF8, "application/json")))
        {
            var body = await second.Content.ReadAsStringAsync();
            Assert.True(second.IsSuccessStatusCode, body);
            secondId = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
        }

        var armed = sessions.Md5Keys.Last(k => k.Password is not null);
        Assert.Equal("203.0.113.11", armed.Ip);
        Assert.Equal("key-a", armed.Password);

        using var firstDetail = await _client.GetAsync($"http://127.0.0.1:{_port}/api/peers/{firstId}");
        using var secondDetail = await _client.GetAsync($"http://127.0.0.1:{_port}/api/peers/{secondId}");
        Assert.Contains("\"tcpMd5\":true", await firstDetail.Content.ReadAsStringAsync());
        Assert.Contains("\"tcpMd5\":false", await secondDetail.Content.ReadAsStringAsync());
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
    /// <summary>Verifies that legacy stored credentials cannot report MD5 protection on an unsupported host.</summary>
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

    [Fact]
    /// <summary>Verifies that a supported host with kernel refusal still reports the peer as unprotected.</summary>
    public async Task TcpMd5Flag_FalseWhenTheKernelRefusedTheKey_EvenOnASupportedPlatform()
    {
        // The platform half is covered above; this is the residual (#546 item 3). On a SUPPORTED
        // platform whose kernel still refuses the key — a Linux build without CONFIG_TCP_MD5SIG,
        // or any setsockopt failure — the password is stored but nothing is armed. The indicator
        // used to read `TcpMd5.IsSupported && password != ""`, so it answered true and told the
        // operator the session was MD5-protected while any host reaching port 179 from that
        // address could complete the handshake unsigned. It now follows actual arming state.
        //
        // Only distinguishing where TcpMd5.IsSupported is true (Linux, and CI runs ubuntu-latest);
        // elsewhere the platform term already forces false and the assertion holds trivially.
        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));
        var saved = await store.SavePeerConfigurationAsync(
            "198.51.100.12", 65015, null, [], [], [], md5Password: "unarmed-secret");

        _port = await StartAsync(
            new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } },
            sessions: new UnarmedSessions());
        _client = new HttpClient();

        using var detail = await _client.GetAsync($"http://127.0.0.1:{_port}/api/peers/{saved.Id}");
        var json = await detail.Content.ReadAsStringAsync();

        Assert.True(detail.IsSuccessStatusCode, $"detail: {(int)detail.StatusCode} {json}");
        Assert.Contains(""""tcpMd5":false"""", json);   // RED pre-fix on Linux: answered true
        Assert.DoesNotContain("unarmed-secret", json);
    }

    /// <summary>Exercises duplicate lists, ASNs, and normalized CIDRs on create/update, requiring 400 and no DB mutation.</summary>
    [Theory]
    [InlineData(false, "lists", "[\"ru\",\"ru\"]", "ru")]
    [InlineData(true, "lists", "[\"ru\",\"ru\"]", "ru")]
    [InlineData(false, "customAsns", "[65010,65010]", "65010")]
    [InlineData(true, "customAsns", "[65010,65010]", "65010")]
    [InlineData(false, "customPrefixes", "[\"10.0.0.0/24\",\"10.0.0.0/24\"]", "10.0.0.0/24")]
    [InlineData(true, "customPrefixes", "[\"10.0.0.0/24\",\"10.0.0.0/24\"]", "10.0.0.0/24")]
    [InlineData(false, "customPrefixes", "[\"10.0.0.1/24\",\"10.0.0.0/24\"]", "10.0.0.0/24")]
    [InlineData(true, "customPrefixes", "[\"10.0.0.1/24\",\"10.0.0.0/24\"]", "10.0.0.0/24")]
    public async Task DuplicatePeerValues_Return400WithFieldValueIndexes_AndDoNotMutateStore(
        bool update, string field, string values, string duplicate)
    {
        _port = await StartAsync(new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } });
        _client = new HttpClient();
        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));
        var saved = update
            ? await store.SavePeerConfigurationAsync("198.51.100.30", 65030, "unchanged", [], [("192.0.2.0", 24)], [64999])
            : null;
        var url = update
            ? $"http://127.0.0.1:{_port}/api/peers/{saved!.Id}"
            : $"http://127.0.0.1:{_port}/api/peers";

        var json = update
            ? $"{{\"{field}\":{values}}}"
            : $"{{\"ip\":\"198.51.100.30\",\"asn\":65030,\"{field}\":{values}}}";
        using var response = await _client.SendAsync(new HttpRequestMessage(update ? HttpMethod.Put : HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, body);
        Assert.Contains(duplicate, body);
        Assert.Contains("indexes 0 and 1", body);
        if (update)
        {
            var detail = await store.GetPeerDetailAsync(saved!.Id);
            Assert.Equal("unchanged", detail!.Description);
            Assert.Equal(["192.0.2.0/24"], detail.CustomPrefixes);
            Assert.Equal([64999u], detail.CustomAsns);
        }
        else
        {
            Assert.Empty(await store.GetPeersByIpAsync("198.51.100.30"));
        }
    }

    [Fact]
    public async Task DistinctOmittedAndEmptyPeerValues_PreserveCreateAndPartialUpdateSemantics()
    {
        _port = await StartAsync(new AppConfig
        {
            Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" },
            RipeStat = new RipeStatConfig
            {
                AsnLists =
                [
                    new AsnList { Name = "ru-a", Country = "RU" },
                    new AsnList { Name = "ru-b", Country = "RU" }
                ]
            }
        });
        _client = new HttpClient();
        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));

        using var created = await _client.PostAsync($"http://127.0.0.1:{_port}/api/peers",
            new StringContent("""{"ip":"198.51.100.31","asn":65031,"lists":["ru-a","ru-b"],"customPrefixes":["10.0.0.1/24","192.0.2.0/24"],"customAsns":[65010,65011]}""", Encoding.UTF8, "application/json"));
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var peer = Assert.Single(await store.GetPeersByIpAsync("198.51.100.31"));

        using (var omitted = await _client.PutAsync($"http://127.0.0.1:{_port}/api/peers/{peer.Id}",
                   new StringContent("{}", Encoding.UTF8, "application/json")))
            Assert.True(omitted.IsSuccessStatusCode, await omitted.Content.ReadAsStringAsync());
        var preserved = await store.GetPeerDetailAsync(peer.Id);
        Assert.Equal(["ru-a", "ru-b"], preserved!.Subscriptions);
        Assert.Equal(["10.0.0.0/24", "192.0.2.0/24"], preserved.CustomPrefixes);
        Assert.Equal([65010u, 65011u], preserved.CustomAsns);

        using var cleared = await _client.PutAsync($"http://127.0.0.1:{_port}/api/peers/{peer.Id}",
            new StringContent("""{"lists":[],"customPrefixes":[],"customAsns":[]}""", Encoding.UTF8, "application/json"));
        Assert.True(cleared.IsSuccessStatusCode, await cleared.Content.ReadAsStringAsync());
        var empty = await store.GetPeerDetailAsync(peer.Id);
        Assert.Empty(empty!.Subscriptions);
        Assert.Empty(empty.CustomPrefixes);
        Assert.Empty(empty.CustomAsns);
    }

    [Fact]
    public async Task AsnLists_CountsDistinguishAllFailedPartialAndRealZero()
    {
        var config = new AppConfig
        {
            Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" },
            RipeStat = new RipeStatConfig
            {
                AsnLists =
                [
                    new AsnList { Name = "all-failed", Asns = [65011, 65012] },
                    new AsnList { Name = "partial", Asns = [65010, 65011, 65020] },
                    new AsnList { Name = "zero", Asns = [65040] },
                    new AsnList { Name = "country-failed", Country = "RU" }
                ]
            }
        };
        _port = await StartAsync(config, prefixService: new CountFailurePrefixService());
        _client = new HttpClient();

        using var response = await _client.GetAsync($"http://127.0.0.1:{_port}/api/asn-lists");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var entries = json.RootElement.EnumerateArray().ToArray();
        var allFailed = entries.Single(e => e.GetProperty("id").GetString() == "all-failed");
        var partial = entries.Single(e => e.GetProperty("id").GetString() == "partial");
        var zero = entries.Single(e => e.GetProperty("id").GetString() == "zero");
        var country = entries.Single(e => e.GetProperty("id").GetString() == "country-failed");

        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Equal(JsonValueKind.Null, allFailed.GetProperty("prefixCount").ValueKind);
        Assert.False(allFailed.GetProperty("partial").GetBoolean());
        Assert.Contains("unavailable", allFailed.GetProperty("countError").GetString());
        Assert.Equal(5, partial.GetProperty("prefixCount").GetInt32());
        Assert.True(partial.GetProperty("partial").GetBoolean());
        Assert.Contains("only successful", partial.GetProperty("countError").GetString());
        Assert.Equal(0, zero.GetProperty("prefixCount").GetInt32());
        Assert.False(zero.GetProperty("partial").GetBoolean());
        Assert.Equal(JsonValueKind.Null, zero.GetProperty("countError").ValueKind);
        Assert.Equal(JsonValueKind.Null, country.GetProperty("prefixCount").ValueKind);
        Assert.Contains("unavailable", country.GetProperty("countError").GetString());
    }

    [Fact]
    public async Task NonCanonicalPeerIp_Returns400_WithoutRewritingTargetHost()
    {
        _port = await StartAsync(new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } });
        _client = new HttpClient();
        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));

        using var response = await _client.PostAsync($"http://127.0.0.1:{_port}/api/peers",
            new StringContent("""{"ip":"010.1.1.1","asn":65010}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await store.GetPeersByIpAsync("010.1.1.1"));
        Assert.Empty(await store.GetPeersByIpAsync("8.1.1.1"));
    }

    /// <summary>Verifies explicit partial-commit reporting, password removal, and refresh after failed initial arming.</summary>
    [Fact]
    public async Task Md5Create_KernelFailure_RemovesStoredPassword_AndReportsCommittedPeer()
    {
        if (!OperatingSystem.IsLinux()) return;
        var sessions = new ThrowingMd5Sessions();
        _port = await StartAsync(
            new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } },
            sessions);
        _client = new HttpClient();

        using var response = await _client.PostAsync($"http://127.0.0.1:{_port}/api/peers",
            new StringContent("""{"ip":"198.51.100.20","asn":65020,"md5Password":"refused-secret"}""", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("peer was created", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("password was removed", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refused-secret", body);
        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));
        var peer = Assert.Single(await store.GetPeersByIpAsync("198.51.100.20"));
        var detail = await store.GetPeerDetailAsync(peer.Id);
        Assert.NotNull(detail);
        Assert.Null(detail.Md5Password);
        Assert.Equal(1, sessions.RefreshCalls);
    }

    /// <summary>Verifies conditional restoration of an existing peer's previous password after failed replacement.</summary>
    [Fact]
    public async Task Md5Create_ExistingPeerKernelFailure_RestoresPreviousStoredPassword()
    {
        if (!OperatingSystem.IsLinux()) return;
        _port = await StartAsync(
            new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } },
            sessions: new ThrowingMd5Sessions());
        _client = new HttpClient();
        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));
        var saved = await store.SavePeerConfigurationAsync(
            "198.51.100.22", 65022, null, [], [], [], md5Password: "old-secret");

        using var response = await _client.PostAsync($"http://127.0.0.1:{_port}/api/peers",
            new StringContent("""{"ip":"198.51.100.22","asn":65022,"md5Password":"new-secret"}""", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("previous stored password was restored", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("new-secret", body);
        Assert.DoesNotContain("old-secret", body);
        Assert.Equal("old-secret", (await store.GetPeerDetailAsync(saved.Id))!.Md5Password);
        Assert.Single(await store.GetPeersByIpAsync("198.51.100.22"));
    }

    /// <summary>Verifies rollback, secret non-disclosure, and refresh scheduling after failed update rearming.</summary>
    [Fact]
    public async Task Md5Update_KernelFailure_RestoresPreviousStoredPassword()
    {
        if (!OperatingSystem.IsLinux()) return;
        var sessions = new ThrowingMd5Sessions();
        _port = await StartAsync(
            new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } },
            sessions);
        _client = new HttpClient();
        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));
        var saved = await store.SavePeerConfigurationAsync(
            "198.51.100.21", 65021, null, [], [], [], md5Password: "old-secret");

        using var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put,
            $"http://127.0.0.1:{_port}/api/peers/{saved.Id}")
        {
            Content = new StringContent("""{"md5Password":"new-secret"}""", Encoding.UTF8, "application/json")
        });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("previous stored password was restored", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("new-secret", body);
        Assert.DoesNotContain("old-secret", body);
        Assert.Equal("old-secret", (await store.GetPeerDetailAsync(saved.Id))!.Md5Password);
        Assert.Equal(1, sessions.RefreshCalls);
    }

    /// <summary>Overlaps two replacements and verifies that failed rollback restores the newer committed password.</summary>
    [Fact]
    public async Task Md5Update_ConcurrentSuccessBeforeFailure_RestoresNewestPassword()
    {
        if (!OperatingSystem.IsLinux()) return;
        var sessions = new RecordingSessions { ThrowOnPassword = "x-secret" };
        var connectionString = $"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();
        var dbOptions = new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(connectionString).Options;
        using (var boot = new BgpDbContext(dbOptions))
            BgpDbContext.Initialize(boot);
        _port = await StartAsync(
            new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } },
            sessions, dbOptions: dbOptions);
        _client = new HttpClient();
        var store = new PeerStore(new StaticOptionsFactory(dbOptions));
        var saved = await store.SavePeerConfigurationAsync(
            "198.51.100.24", 65024, null, [], [], [], md5Password: "old-secret");
        var firstAtGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeCalls = 0;
        _api!.Md5MutationGateProbe = async _ =>
        {
            if (Interlocked.Increment(ref probeCalls) == 1)
            {
                firstAtGate.SetResult();
                await releaseFirst.Task;
            }
        };

        var failing = _client.PutAsync($"http://127.0.0.1:{_port}/api/peers/{saved.Id}",
            new StringContent("""{"md5Password":"x-secret"}""", Encoding.UTF8, "application/json"));
        await firstAtGate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var newer = await _client.PutAsync($"http://127.0.0.1:{_port}/api/peers/{saved.Id}",
            new StringContent("""{"md5Password":"y-secret"}""", Encoding.UTF8, "application/json"));
        Assert.True(newer.IsSuccessStatusCode, await newer.Content.ReadAsStringAsync());
        releaseFirst.SetResult();
        using var failed = await failing;
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

        Assert.Equal("y-secret", (await store.GetPeerDetailAsync(saved.Id))!.Md5Password);
    }

    /// <summary>Overlaps replacement and clear through real HTTP/SQLite, requiring stored and armed state to finish cleared.</summary>
    [Fact]
    public async Task Md5Updates_SharedIp_CannotApplyCredentialsReadBeforeNewerClear()
    {
        if (!OperatingSystem.IsLinux()) return;
        var sessions = new RecordingSessions();
        var logger = new RecordingLogger();
        var connectionString = $"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();
        var dbOptions = new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(connectionString).Options;
        using (var boot = new BgpDbContext(dbOptions))
            BgpDbContext.Initialize(boot);
        _port = await StartAsync(
            new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } },
            sessions, logger, dbOptions: dbOptions);
        _client = new HttpClient();
        var store = new PeerStore(new StaticOptionsFactory(dbOptions));
        var saved = await store.SavePeerConfigurationAsync(
            "198.51.100.23", 65023, null, [], [], [], md5Password: "old-secret");
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeCalls = 0;
        var gateCalls = 0;
        var clearAtGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _api!.Md5MutationGateProbe = _ =>
        {
            if (Interlocked.Increment(ref gateCalls) == 2)
                clearAtGate.SetResult();
            return Task.CompletedTask;
        };
        _api.Md5RearmProbe = async _ =>
        {
            if (Interlocked.Increment(ref probeCalls) == 1)
            {
                firstRead.SetResult();
                await releaseFirst.Task;
            }
        };

        var replace = _client.PutAsync($"http://127.0.0.1:{_port}/api/peers/{saved.Id}",
            new StringContent("""{"md5Password":"new-secret"}""", Encoding.UTF8, "application/json"));
        await firstRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var clearTask = _client.PutAsync($"http://127.0.0.1:{_port}/api/peers/{saved.Id}",
            new StringContent("""{"md5Password":""}""", Encoding.UTF8, "application/json"));
        await clearAtGate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseFirst.SetResult();
        using var clear = await clearTask;
        using var replaced = await replace;
        Assert.True(clear.IsSuccessStatusCode,
            $"{await clear.Content.ReadAsStringAsync()}\n{string.Join("\n", logger.Messages)}");
        Assert.True(replaced.IsSuccessStatusCode, await replaced.Content.ReadAsStringAsync());

        Assert.Null((await store.GetPeerDetailAsync(saved.Id))!.Md5Password);
        Assert.False(sessions.IsPeerMd5Armed("198.51.100.23", "new-secret"));
        Assert.Null(sessions.Md5Keys.Last().Password);
    }

    [Fact]
    public async Task PeerDetail_StatusFollowsLiveEstablishedState_NotPersistedWriteRaces()
    {
        var sessions = new RecordingSessions();
        _port = await StartAsync(
            new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } },
            sessions);
        _client = new HttpClient();
        var store = new PeerStore(new StaticOptionsFactory(new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(_connection).Options));
        var saved = await store.SavePeerConfigurationAsync("198.51.100.30", 65030, null, [], [], []);

        using var inactive = await _client.GetAsync($"http://127.0.0.1:{_port}/api/peers/{saved.Id}");
        Assert.Contains("\"status\":\"inactive\"", await inactive.Content.ReadAsStringAsync());

        sessions.Established = true;
        using var active = await _client.GetAsync($"http://127.0.0.1:{_port}/api/peers/{saved.Id}");
        Assert.Contains("\"status\":\"active\"", await active.Content.ReadAsStringAsync());

        sessions.Established = false;
        using var closed = await _client.GetAsync($"http://127.0.0.1:{_port}/api/peers/{saved.Id}");
        Assert.Contains("\"status\":\"inactive\"", await closed.Content.ReadAsStringAsync());
        Assert.Equal("inactive", (await store.GetPeerDetailAsync(saved.Id))!.Status);
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

    private sealed class ExportPrefixService : IPrefixService
    {
        public bool FailAsns { get; init; }
        public bool PartialAsns { get; init; }
        public async Task<AsnPrefixResolution> GetPrefixesForAsnsResultAsync(IEnumerable<uint> asns, CancellationToken ct = default)
        {
            var prefixes = await GetPrefixesForAsns(asns, ct);
            return PartialAsns ? new AsnPrefixResolution(prefixes.Take(1).ToList(), [65020u]) : new AsnPrefixResolution(prefixes, []);
        }
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetPrefixesAsync(uint asn, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(UInt128, byte, bool)>>([]);
        public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default)
        {
            if (FailAsns) throw new InvalidOperationException("simulated source outage");
            return Task.FromResult(asns.Select(asn =>
                ((UInt128)(0x0A000000u + (asn % 200) * 0x10000u), (byte)16, true, asn)).ToList());
        }
        public Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default) => Task.FromResult(0);
        public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<(UInt128, byte, bool, uint)> { (0xCB007100, 24, true, 0) });
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(UInt128, byte, bool)>>([(0xAC100000, 12, true)]);
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetUserSourcePrefixesAsync(string name, string url, string? community, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(UInt128, byte, bool)>>([(0xC0000200, 24, true)]);
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CountFailurePrefixService : IPrefixService
    {
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetPrefixesAsync(uint asn, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(UInt128, byte, bool)>>([]);
        public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default) => Task.FromResult(new List<(UInt128, byte, bool, uint)>());
        public Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default) => asn switch
        {
            65010 => Task.FromResult(2),
            65020 => Task.FromResult(3),
            65040 => Task.FromResult(0),
            _ => Task.FromException<int>(new InvalidOperationException("simulated source outage"))
        };
        public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default) =>
            Task.FromException<List<(UInt128, byte, bool, uint)>>(new InvalidOperationException("simulated country-source outage"));
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(UInt128, byte, bool)>>([]);
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetUserSourcePrefixesAsync(string name, string url, string? community, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(UInt128, byte, bool)>>([]);
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

    /// <summary>
    /// Stands in for a server that arms every key it is given — the default for API tests, which
    /// are about the HTTP surface, not about the kernel. Tracks what was armed so the
    /// <c>tcpMd5</c> indicator (now derived from arming state) reads true after a successful set.
    /// <see cref="UnarmedSessions"/> is the counterpart for the failed-arming case.
    /// </summary>
    private sealed class InertSessions : ISessionManager
    {
        private readonly Dictionary<string, string> _armed = [];
        public Task RefreshPeerAsync(string peerIp, uint asn) => Task.CompletedTask;
        public List<string> GetActivePeerIps() => [];
        public Task TerminatePeerAsync(string peerIp, uint asn, CancellationToken ct = default) => Task.CompletedTask;
        public Task TerminatePeerByIpAsync(string peerIp, CancellationToken ct = default) => Task.CompletedTask;
        public void SetPeerMd5Key(string peerIp, string? password)
        {
            if (string.IsNullOrEmpty(password)) _armed.Remove(peerIp);
            else _armed[peerIp] = password;
        }
        public bool IsPeerEstablished(string peerIp, uint asn) => false;
        public bool IsPeerMd5Armed(string peerIp, string? password) =>
            _armed.TryGetValue(peerIp, out var armed) && armed == password;
        public int GetAdvertisedPrefixCount(string peerIp, uint asn) => 0;
        public Task RefreshAllEstablishedAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// A server whose kernel refuses every key: the password is stored, nothing is armed. Models
    /// a Linux host without CONFIG_TCP_MD5SIG (or any setsockopt failure), where the indicator
    /// used to read true off the stored password alone.
    /// </summary>
    private sealed class UnarmedSessions : ISessionManager
    {
        public Task RefreshPeerAsync(string peerIp, uint asn) => Task.CompletedTask;
        public List<string> GetActivePeerIps() => [];
        public Task TerminatePeerAsync(string peerIp, uint asn, CancellationToken ct = default) => Task.CompletedTask;
        public Task TerminatePeerByIpAsync(string peerIp, CancellationToken ct = default) => Task.CompletedTask;
        public void SetPeerMd5Key(string peerIp, string? password) { }
        public bool IsPeerEstablished(string peerIp, uint asn) => false;
        public bool IsPeerMd5Armed(string peerIp, string? password) => false;
        public int GetAdvertisedPrefixCount(string peerIp, uint asn) => 0;
        public Task RefreshAllEstablishedAsync() => Task.CompletedTask;
    }

    private sealed class ThrowingMd5Sessions : ISessionManager
    {
        public int RefreshCalls { get; private set; }
        public Task RefreshPeerAsync(string peerIp, uint asn)
        {
            RefreshCalls++;
            return Task.CompletedTask;
        }
        public List<string> GetActivePeerIps() => [];
        public Task TerminatePeerAsync(string peerIp, uint asn, CancellationToken ct = default) => Task.CompletedTask;
        public Task TerminatePeerByIpAsync(string peerIp, CancellationToken ct = default) => Task.CompletedTask;
        public void SetPeerMd5Key(string peerIp, string? password) =>
            throw new InvalidOperationException("simulated kernel refusal");
        public bool IsPeerEstablished(string peerIp, uint asn) => false;
        public bool IsPeerMd5Armed(string peerIp, string? password) => false;
        public int GetAdvertisedPrefixCount(string peerIp, uint asn) => 0;
        public Task RefreshAllEstablishedAsync() => Task.CompletedTask;
    }

    /// <summary>Records SetPeerMd5Key calls so a test can assert WHAT was armed.</summary>
    private sealed class RecordingSessions : ISessionManager
    {
        private readonly object _md5Sync = new();
        public List<(string Ip, string? Password)> Md5Keys { get; } = [];
        public string? ThrowOnPassword { get; set; }
        public bool Established { get; set; }
        public Task RefreshPeerAsync(string peerIp, uint asn) => Task.CompletedTask;
        public List<string> GetActivePeerIps() => [];
        public Task TerminatePeerAsync(string peerIp, uint asn, CancellationToken ct = default) => Task.CompletedTask;
        public Task TerminatePeerByIpAsync(string peerIp, CancellationToken ct = default) => Task.CompletedTask;
        public void SetPeerMd5Key(string peerIp, string? password)
        {
            lock (_md5Sync)
            {
                if (ThrowOnPassword is not null && password == ThrowOnPassword)
                    throw new InvalidOperationException("simulated kernel refusal");
                Md5Keys.Add((peerIp, password));
            }
        }
        public bool IsPeerEstablished(string peerIp, uint asn) => Established;
        public bool IsPeerMd5Armed(string peerIp, string? password)
        {
            lock (_md5Sync)
                return !string.IsNullOrEmpty(password) && Md5Keys.LastOrDefault(k => k.Ip == peerIp).Password == password;
        }
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
            => Messages.Add(formatter(state, exception) + (exception is null ? string.Empty : $" | {exception}"));
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