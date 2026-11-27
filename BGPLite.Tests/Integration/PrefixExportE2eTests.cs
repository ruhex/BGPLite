using System.Text;
using System.Text.Json;
using BGPLite.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace BGPLite.Tests.Integration;

/// <summary>
/// E2E for GET /api/peers/{id}/prefixes against the real stack (#556): the export must carry
/// every route class the peer selected — ASN-list subscriptions, country lists, prefix sources,
/// custom ASNs, custom prefixes, active user URL sources — exactly like the outbound BGP path.
/// This is the behavior the production deployment (pre-#606 image) still lacks: there the export
/// degraded to custom prefixes only and hid partial failures behind HTTP 200.
/// </summary>
public sealed class PrefixExportE2eTests : IAsyncLifetime
{
    private (RouteServerFixture Server, string PeerId) _stand;

    public async Task InitializeAsync() => _stand = await FixtureScenario.StartWithFullPeerAsync();

    public async Task DisposeAsync() => await _stand.Server.DisposeAsync();

    [Fact]
    public async Task ExportJson_ContainsEveryRouteClass()
    {
        var exported = await _stand.Server.GetJsonAsync($"/api/peers/{_stand.PeerId}/prefixes?format=json");
        var prefixes = exported.EnumerateArray().Select(e => e.GetString()!).ToList();

        Assert.Equal(FixtureScenario.ExpectedFullPeerPrefixes, prefixes);
    }

    [Fact]
    public async Task ExportTxt_IsNewlineDelimitedPlainText()
    {
        using var response = await _stand.Server.Http.GetAsync(
            _stand.Server.ApiBase + $"/api/peers/{_stand.PeerId}/prefixes");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(string.Join("\n", FixtureScenario.ExpectedFullPeerPrefixes), body);
    }

    /// <summary>#556 acceptance criterion 1: the export is the same read model the BGP advertisement uses.</summary>
    [Fact]
    public async Task Export_MatchesLiveBgpDump()
    {
        using var client = new BgpTestClient(FixtureScenario.FullPeerAsn, routerId: 0x0A000001);
        await client.ConnectAndEstablishAsync(_stand.Server.BgpPort);
        var dump = await client.CollectDumpAsync();
        var wirePrefixes = dump.SelectMany(u => u.Announced).Select(r => r.Prefix)
            .Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();

        var exported = await _stand.Server.GetJsonAsync($"/api/peers/{_stand.PeerId}/prefixes?format=json");
        var exportPrefixes = exported.EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.NotEmpty(wirePrefixes);
        Assert.Equal(wirePrefixes, exportPrefixes);
    }

    [Fact]
    public async Task Export_UnconfiguredPeer_FallsBackToRuDefaults()
    {
        // Connect as an ASN the API never configured — the session auto-registers the peer row.
        using var client = new BgpTestClient(asn: 65099, routerId: 0x0A000002);
        await client.ConnectAndEstablishAsync(_stand.Server.BgpPort);
        var dump = await client.CollectDumpAsync();
        Assert.Equal(
            FixtureScenario.RuPrefixes.OrderBy(p => p, StringComparer.Ordinal),
            dump.SelectMany(u => u.Announced).Select(r => r.Prefix).Distinct().OrderBy(p => p, StringComparer.Ordinal));

        var me = await _stand.Server.GetJsonAsync("/api/me?asn=65099");
        var peerId = me.GetProperty("peers")[0].GetProperty("id").GetString()!;

        var exported = await _stand.Server.GetJsonAsync($"/api/peers/{peerId}/prefixes?format=json");
        Assert.Equal(
            FixtureScenario.RuPrefixes.OrderBy(p => p, StringComparer.Ordinal),
            exported.EnumerateArray().Select(e => e.GetString()).OrderBy(p => p, StringComparer.Ordinal));
    }
}

/// <summary>Degraded and boundary export paths: partial source failure (503, no partial list), 404, and the legacy no-ASN row (409).</summary>
public sealed class PrefixExportDegradedE2eTests
{
    [Fact]
    public async Task Export_PartialAsnFailure_Returns503WithoutPartialList()
    {
        await using var server = await RouteServerFixture.StartAsync(seed: prefixes =>
        {
            prefixes.AsnPrefixes[714] = ["17.253.14.0/24"];
            prefixes.FailedAsns.Add(65020);   // the peer's custom AS fails; the rest resolve
            prefixes.AsnPrefixes[65020] = ["203.0.114.0/24"];
        });
        var saved = await server.Store.SavePeerConfigurationAsync(
            "127.0.0.1", 65010, null, ["apple"], [], [65020u]);

        var (exportStatus, exportBody) = await server.SendJsonAsync($"/api/peers/{saved.Id}/prefixes?format=json", "GET", new { });

        Assert.Equal(503, exportStatus);
        Assert.Contains("incomplete", exportBody);
        // No partial data: neither the surviving list prefix nor the custom prefixes leak into the body.
        Assert.DoesNotContain("17.253.14.0/24", exportBody);
        Assert.DoesNotContain("203.0.114.0/24", exportBody);
    }

    [Fact]
    public async Task Export_UnknownPeerId_Returns404()
    {
        await using var server = await RouteServerFixture.StartAsync();
        var (status, body) = await server.SendJsonAsync("/api/peers/does-not-exist/prefixes", "GET", new { });
        Assert.Equal(404, status);
        Assert.Contains("not found", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A legacy Ip-only row (Asn NULL) cannot be projected to (ip, asn) — the export says so explicitly.</summary>
    [Fact]
    public async Task Export_LegacyPeerWithoutAsn_Returns409()
    {
        await using var server = await RouteServerFixture.StartAsync();
        string peerId;
        await using (var db = server.CreateDbContext())
        {
            var legacy = new Peer { Ip = "198.51.100.77", Asn = null };
            db.Peers.Add(legacy);
            await db.SaveChangesAsync();
            peerId = legacy.Id;
        }

        var (status, body) = await server.SendJsonAsync($"/api/peers/{peerId}/prefixes", "GET", new { });
        Assert.Equal(409, status);
        Assert.Contains("ASN", body);
    }
}
