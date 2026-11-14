using System.Text.Json;

namespace BGPLite.Tests.Integration;

/// <summary>
/// Management-API surface E2E over the real HTTP listener + SQLite store: peer CRUD and its
/// validation matrix, per-peer source CRUD with the SSRF gate, the read endpoints
/// (asn-lists, community-scheme, server, sessions, routes, as/{asn}/prefixes), and routing
/// to unknown paths.
/// </summary>
public sealed class PeerManagementApiE2eTests
{
    private static async Task<(RouteServerFixture Server, string PeerId)> CreatePeerAsync(RouteServerFixture server, object body)
    {
        var (status, text) = await server.SendJsonAsync("/api/peers", "POST", body);
        Assert.True(status == 200, $"create failed: {status} {text}");
        return (server, JsonDocument.Parse(text).RootElement.GetProperty("id").GetString()!);
    }

    private static string[] SortedLists(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetString()!).OrderBy(p => p, StringComparer.Ordinal).ToArray();

    private static async Task<RouteServerFixture> StartServerAsync(Action<ScriptedPrefixService>? seed = null) =>
        await RouteServerFixture.StartAsync(seed: seed);

    [Fact]
    public async Task Create_Detail_Me_Roundtrip()
    {
        await using var server = await StartServerAsync();
        // API peer at a TEST-NET address (the API boundary rejects loopback peer IPs by design).
        var (status, body) = await server.SendJsonAsync("/api/peers", "POST", new
        {
            ip = "198.51.100.10",
            asn = 65010,
            description = "e2e",
            lists = new[] { "apple", "ru" },
            customPrefixes = new[] { "103.10.0.0/16" },
            customAsns = new[] { 65020u },
        });
        Assert.Equal(200, status);
        var created = JsonDocument.Parse(body).RootElement;
        var peerId = created.GetProperty("id").GetString()!;
        Assert.Equal("198.51.100.10", created.GetProperty("ip").GetString());
        Assert.Equal(65010, created.GetProperty("asn").GetInt32());
        Assert.Equal("inactive", created.GetProperty("status").GetString());
        Assert.False(created.GetProperty("tcpMd5").GetBoolean());
        Assert.Equal(["apple", "ru"], created.GetProperty("lists").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["103.10.0.0/16"], created.GetProperty("customPrefixes").EnumerateArray().Select(e => e.GetString()));

        var detail = await server.GetJsonAsync($"/api/peers/{peerId}");
        Assert.Equal(peerId, detail.GetProperty("id").GetString());
        Assert.True(detail.GetProperty("allRoutes").GetBoolean()); // no per-peer community filter set

        // /api/me resolves peers at the CALLER's IP — seed one at 127.0.0.1 through the store.
        var localPeer = await server.Store.SavePeerConfigurationAsync("127.0.0.1", 65030, null, [], [], []);
        var me = await server.GetJsonAsync("/api/me?asn=65030");
        Assert.Equal("127.0.0.1", me.GetProperty("ip").GetString());
        Assert.Equal(localPeer.Id, me.GetProperty("peers")[0].GetProperty("id").GetString());
        // /api/me's detail carries the live wire count (GET /api/peers/{id} does not).
        Assert.Equal(0, me.GetProperty("peers")[0].GetProperty("advertisedPrefixCount").GetInt32());
    }

    [Fact]
    public async Task Update_PartialSemantics_KeepOmittedFields()
    {
        await using var server = await StartServerAsync();
        var (_, peerId) = await CreatePeerAsync(server, new
        {
            ip = "198.51.100.10",
            asn = 65010,
            description = "before",
            lists = new[] { "apple" },
            customPrefixes = new[] { "103.10.0.0/16" },
        });

        // Omitted fields must not be wiped. List ORDER is not part of the contract — compare sorted.
        var (status1, _) = await server.SendJsonAsync($"/api/peers/{peerId}", "PUT", new { description = "after" });
        Assert.Equal(200, status1);
        var detail1 = await server.GetJsonAsync($"/api/peers/{peerId}");
        Assert.Equal("after", detail1.GetProperty("description").GetString());
        Assert.Equal(["apple"], SortedLists(detail1.GetProperty("lists")));
        Assert.Equal(["103.10.0.0/16"], SortedLists(detail1.GetProperty("customPrefixes")));

        // A non-null list REPLACES the set; prefixes (omitted) stay.
        var (status2, _) = await server.SendJsonAsync($"/api/peers/{peerId}", "PUT", new { lists = new[] { "ru", "extra" } });
        Assert.Equal(200, status2);
        var detail2 = await server.GetJsonAsync($"/api/peers/{peerId}");
        Assert.Equal(["extra", "ru"], SortedLists(detail2.GetProperty("lists")));
        Assert.Equal(["103.10.0.0/16"], SortedLists(detail2.GetProperty("customPrefixes")));
    }

    [Fact]
    public async Task Delete_RemovesPeer_AndSubsequentReads404()
    {
        await using var server = await StartServerAsync();
        var (_, peerId) = await CreatePeerAsync(server, new { ip = "198.51.100.12", asn = 65012 });

        var (deleteStatus, _) = await server.SendJsonAsync($"/api/peers/{peerId}", "DELETE", new { });
        Assert.Equal(200, deleteStatus);

        var (getStatus, _) = await server.SendJsonAsync($"/api/peers/{peerId}", "GET", new { });
        Assert.Equal(404, getStatus);
        var (redeleteStatus, _) = await server.SendJsonAsync($"/api/peers/{peerId}", "DELETE", new { });
        Assert.Equal(404, redeleteStatus);
    }

    [Fact]
    public async Task Create_ValidationMatrix_RejectedWithoutSideEffects()
    {
        await using var server = await StartServerAsync();

        var cases = new (object Body, string ExpectedFragment)[]
        {
            (new { ip = "not-an-ip", asn = 65010 }, "Invalid peer IP"),
            (new { ip = "127.0.0.1", asn = 65010 }, "Invalid peer IP"),       // loopback is a non-peer address
            (new { ip = "198.51.100.10", asn = 0u }, "Invalid peer AS"),
            (new { ip = "198.51.100.10", asn = 65535u }, "Invalid peer AS"),  // RFC 7300 Last-32 reservation
            (new { ip = "198.51.100.10", asn = 65010, lists = new[] { "nope" } }, "Unknown list name"),
            (new { ip = "198.51.100.10", asn = 65010, customPrefixes = new[] { "10.0.0.999/33" } }, "Invalid CIDR"),
            (new { ip = "198.51.100.10", asn = 65010, lists = new[] { "apple", "apple" } }, "duplicate"),
            (new { ip = "198.51.100.10", asn = 65010, maxPrefix = -5 }, "Invalid MaxPrefix"),
        };
        foreach (var (body, expected) in cases)
        {
            var (status, text) = await server.SendJsonAsync("/api/peers", "POST", body);
            Assert.True(status == 400, $"expected 400 for {JsonSerializer.Serialize(body)}, got {status}: {text}");
            Assert.Contains(expected, text, StringComparison.OrdinalIgnoreCase);
        }

        // None of the rejected requests left a row behind.
        var list = await server.GetJsonAsync("/api/me");
        Assert.Empty(list.GetProperty("peers").EnumerateArray());
    }

    [Fact]
    public async Task Sources_SsrfGate_RejectsLoopbackAndNonStandardPorts()
    {
        await using var server = await StartServerAsync();
        var (_, peerId) = await CreatePeerAsync(server, new { ip = "198.51.100.10", asn = 65010 });

        var (loopbackStatus, loopbackBody) = await server.SendJsonAsync($"/api/peers/{peerId}/sources", "POST", new
        {
            name = "loopback",
            url = "http://127.0.0.1:80/prefixes.txt",
        });
        Assert.Equal(400, loopbackStatus);
        Assert.Contains("Invalid URL", loopbackBody);

        var (portStatus, portBody) = await server.SendJsonAsync($"/api/peers/{peerId}/sources", "POST", new
        {
            name = "odd-port",
            url = "http://93.184.216.34:8080/prefixes.txt",   // numeric host: no DNS, port gate fires first
        });
        Assert.Equal(400, portStatus);
        Assert.Contains("Invalid URL", portBody);

        var sources = await server.GetJsonAsync($"/api/peers/{peerId}/sources");
        Assert.Empty(sources.EnumerateArray());
    }

    [Fact]
    public async Task Sources_Crud_ToggleAndDelete()
    {
        await using var server = await StartServerAsync();
        var (_, peerId) = await CreatePeerAsync(server, new { ip = "198.51.100.10", asn = 65010 });

        var source = await server.Store.AddCustomSourceAsync(peerId, "feed", "https://example.com/feed.txt", null);
        var (patchStatus, patchBody) = await server.SendJsonAsync(
            $"/api/peers/{peerId}/sources/{source.Id}", "PATCH", new { active = true });
        Assert.True(patchStatus == 200, patchBody);
        Assert.True(JsonDocument.Parse(patchBody).RootElement.GetProperty("active").GetBoolean());

        var sources = await server.GetJsonAsync($"/api/peers/{peerId}/sources");
        var row = Assert.Single(sources.EnumerateArray());
        Assert.Equal("feed", row.GetProperty("name").GetString());
        Assert.True(row.GetProperty("active").GetBoolean());

        var (deleteStatus, _) = await server.SendJsonAsync($"/api/peers/{peerId}/sources/{source.Id}", "DELETE", new { });
        Assert.Equal(200, deleteStatus);
        var (missingStatus, _) = await server.SendJsonAsync($"/api/peers/{peerId}/sources/{source.Id}", "PATCH", new { active = false });
        Assert.Equal(404, missingStatus);
    }

    [Fact]
    public async Task AsnLists_ReportsBothKinds_WithScriptedCounts()
    {
        await using var server = await StartServerAsync(seed: p =>
        {
            p.AsnPrefixes[714] = ["17.253.14.0/24", "17.250.16.0/20"];
            p.RuPrefixes.AddRange(["5.101.220.0/22", "212.192.0.0/16"]);
        });

        var lists = await server.GetJsonAsync("/api/asn-lists");
        var rows = lists.EnumerateArray().ToDictionary(row => row.GetProperty("id").GetString()!);

        var apple = rows["apple"];
        Assert.Equal(2, apple.GetProperty("prefixCount").GetInt32());
        Assert.Equal("asn", apple.GetProperty("type").GetString());
        Assert.Equal("65000:20", apple.GetProperty("Community").GetString());

        var ru = rows["ru"];
        Assert.Equal(2, ru.GetProperty("prefixCount").GetInt32());
        Assert.Equal("country", ru.GetProperty("type").GetString());
    }

    [Fact]
    public async Task CommunityScheme_ExposesDefaultScheme()
    {
        await using var server = await StartServerAsync();
        var scheme = await server.GetJsonAsync("/api/community-scheme");
        Assert.Equal("65000:100", scheme.GetProperty("customPrefixes").GetString());
        Assert.Equal("65000:200", scheme.GetProperty("customAsns").GetString());
    }

    [Fact]
    public async Task Server_InfoAndSetupSnippets()
    {
        await using var server = await StartServerAsync();
        var info = await server.GetJsonAsync("/api/server");
        Assert.Equal(65000, info.GetProperty("asn").GetInt32());
        Assert.Equal("198.51.100.1", info.GetProperty("routerId").GetString());
        Assert.Equal(server.ApiPort, info.GetProperty("apiPort").GetInt32());
        Assert.Equal(179, info.GetProperty("bgpPort").GetInt32());
        Assert.NotEmpty(info.GetProperty("setup").EnumerateArray());
        Assert.NotEmpty(info.GetProperty("bird").EnumerateArray());
    }

    [Fact]
    public async Task Sessions_AndRoutes_Shape()
    {
        await using var server = await StartServerAsync();

        var sessions = await server.GetJsonAsync("/api/sessions");
        Assert.Equal(0, sessions.GetProperty("active").GetInt32());

        var routes = await server.GetJsonAsync("/api/routes");
        Assert.Equal(0, routes.GetProperty("total").GetInt32());
        Assert.True(routes.GetProperty("byCommunity").ValueKind is JsonValueKind.Object);
    }

    [Fact]
    public async Task AsnPrefixes_Count_SucceedsAndFailsExplicitly()
    {
        await using var server = await StartServerAsync(seed: p =>
        {
            p.AsnPrefixes[714] = ["17.253.14.0/24"];
            p.FailedAsns.Add(64512);
        });

        var ok = await server.GetJsonAsync("/api/as/714/prefixes?count=true");
        Assert.Equal(1, ok.GetProperty("prefixCount").GetInt32());

        // Upstream resolution failure maps to the generic non-revealing 500 (503 is budget expiry only).
        var (failedStatus, failedBody) = await server.SendJsonAsync("/api/as/64512/prefixes?count=true", "GET", new { });
        Assert.Equal(500, failedStatus);
        Assert.Contains("Internal server error", failedBody);
    }

    [Fact]
    public async Task UnknownRoute_Returns404()
    {
        await using var server = await StartServerAsync();
        var (status, body) = await server.SendJsonAsync("/api/definitely-not-here", "GET", new { });
        Assert.Equal(404, status);
        Assert.Contains("Not found", body);
    }

    [Fact]
    public async Task RateLimit_DrainedBucket_Answers429()
    {
        await using var server = await RouteServerFixture.StartAsync(
            rateLimitOverride: new BGPLite.Configuration.ApiRateLimitConfig
            {
                Enabled = true,
                TokenLimit = 2,
                TokensPerPeriod = 2,
                PeriodSeconds = 60,
            });

        // The first two requests fit the bucket; the third from the same client IP is rejected.
        Assert.Equal(200, (await server.SendJsonAsync("/api/server", "GET", new { })).Status);
        Assert.Equal(200, (await server.SendJsonAsync("/api/server", "GET", new { })).Status);
        var (third, body) = await server.SendJsonAsync("/api/server", "GET", new { });
        Assert.Equal(429, third);
        Assert.Contains("Too many requests", body);
    }

    [Fact]
    public async Task BodyCap_OversizedRequest_Answers413()
    {
        // YAML boot also covers MaxRequestBodyBytes (1024 = the configured minimum): a body beyond
        // the cap is rejected BEFORE deserialization.
        await using var server = await RouteServerFixture.StartFromYamlAsync(port => $"""
            Bgp:
              Asn: 65000
              RouterId: 198.51.100.1
              HoldTime: 0
            ApiPort: {port}
            ApiListen: "127.0.0.1"
            MaxRequestBodyBytes: 1024
            """);

        var (status, _) = await server.SendJsonAsync("/api/peers", "POST", new
        {
            ip = "198.51.100.10",
            asn = 65010,
            description = new string('x', 2048), // 2 KiB body > the 1 KiB cap
        });
        Assert.Equal(413, status);
    }

    [Fact]
    public async Task Cors_ReflectsOnlyAllowlistedOrigins()
    {
        await using var server = await RouteServerFixture.StartAsync(
            corsOriginsOverride: ["https://ui.example"]);

        using var allowed = new HttpRequestMessage(HttpMethod.Options, server.ApiBase + "/api/server");
        allowed.Headers.Add("Origin", "https://ui.example");
        allowed.Headers.Add("Access-Control-Request-Method", "GET");
        using var allowedResponse = await server.Http.SendAsync(allowed);
        Assert.True(allowedResponse.IsSuccessStatusCode);
        Assert.Equal("https://ui.example", allowedResponse.Headers.TryGetValues("Access-Control-Allow-Origin", out var acao) ? acao.FirstOrDefault() : null);

        using var rogue = new HttpRequestMessage(HttpMethod.Options, server.ApiBase + "/api/server");
        rogue.Headers.Add("Origin", "https://evil.example");
        rogue.Headers.Add("Access-Control-Request-Method", "GET");
        using var rogueResponse = await server.Http.SendAsync(rogue);
        var rogueAcao = rogueResponse.Headers.TryGetValues("Access-Control-Allow-Origin", out var rogueValues) ? rogueValues.FirstOrDefault() : null;
        Assert.Null(rogueAcao);
    }

    [Fact]
    public async Task Me_WithoutAsn_ListsAllPeersAtCallerIp()
    {
        await using var server = await StartServerAsync();
        var a = await server.Store.SavePeerConfigurationAsync("127.0.0.1", 65030, null, [], [], []);
        var b = await server.Store.SavePeerConfigurationAsync("127.0.0.1", 65031, null, [], [], []);

        var me = await server.GetJsonAsync("/api/me");
        var ids = me.GetProperty("peers").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();

        Assert.Contains(a.Id, ids);
        Assert.Contains(b.Id, ids);
        Assert.Equal(2, ids.Count);
    }
}
