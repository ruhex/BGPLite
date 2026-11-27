using System.Text;
using System.Text.Json;
using BGPLite.Protocol;

namespace BGPLite.Tests.Integration;

/// <summary>
/// Wire-level E2E: a real BGP client against the real listener. Covers every route class reaching
/// the wire with its category community, ExactUnion summarization, duplicate-NLRI merging,
/// custom-prefix suppression, RU defaults for unconfigured peers, RFC 2918 route refresh,
/// API-driven unsolicited updates, the per-peer MaxPrefix Cease, peer-delete teardown, 4-byte ASN
/// (RFC 6793), and the OPEN rejections of RFC 4271 §6.2.
/// </summary>
public sealed class BgpWireE2eTests
{
    /// <summary>Owns the fixture so tests can `await using` it after the create-call returns the id.</summary>
    private sealed class Stand(RouteServerFixture server, string peerId) : IAsyncDisposable
    {
        public RouteServerFixture Server => server;
        public string PeerId => peerId;
        public async ValueTask DisposeAsync() => await server.DisposeAsync();
    }

    private static string FnvUserSourceCommunity(string name)
    {
        // Mirrors ConfigCommunityResolver's deterministic FNV-1a auto-community (<Asn>:5XX).
        unchecked
        {
            uint hash = 2166136261u;
            foreach (var b in Encoding.UTF8.GetBytes(name))
            {
                hash ^= b;
                hash *= 16777619u;
            }
            return $"65000:{500 + (int)(hash % 100)}";
        }
    }

    private static async Task<Stand> StartFullPeerAsync()
    {
        var (server, peerId) = await FixtureScenario.StartWithFullPeerAsync();
        return new Stand(server, peerId);
    }

    /// <summary>Seeds a configured peer through the store: the API boundary rejects loopback peer IPs (a production policy), and locally the wire client can only source from 127.0.0.1.</summary>
    private static async Task<Stand> StartPeerAsync(
        Action<ScriptedPrefixService> seed, uint asn, string[]? lists = null, string[]? customPrefixes = null, int? maxPrefix = null)
    {
        var server = await RouteServerFixture.StartAsync(seed: seed);
        var saved = await server.Store.SavePeerConfigurationAsync(
            "127.0.0.1", asn, "e2e-wire",
            lists ?? [],
            (customPrefixes ?? []).Select(FixtureScenario.SplitCidr).ToList(),
            [], maxPrefix);
        return new Stand(server, saved.Id);
    }

    private static List<WireRoute> Announced(List<WireUpdate> dump) =>
        dump.SelectMany(u => u.Announced).ToList();

    private static WireRoute? Find(List<WireUpdate> dump, string prefix) =>
        Announced(dump).FirstOrDefault(r => r.Prefix == prefix);

    private static string[] Sorted(IEnumerable<string> prefixes) =>
        [.. prefixes.Distinct().OrderBy(p => p, StringComparer.Ordinal)];

    [Fact]
    public async Task Dump_ContainsEveryRouteClass_WithCategoryCommunities()
    {
        await using var stand = await StartFullPeerAsync();
        using var client = new BgpTestClient(FixtureScenario.FullPeerAsn, routerId: 0x0A000001);
        await client.ConnectAndEstablishAsync(stand.Server.BgpPort);
        var dump = await client.CollectDumpAsync();
        var routes = Announced(dump);

        foreach (var prefix in FixtureScenario.ExpectedFullPeerPrefixes)
            Assert.NotNull(Find(dump, prefix));

        // ASN-list subscription — apple community.
        Assert.Equal([FixtureScenario.AppleCommunity], Find(dump, FixtureScenario.ApplePrefixes[0])!.Communities);
        // Country list — ru community.
        Assert.Equal([FixtureScenario.RuCommunity], Find(dump, FixtureScenario.RuPrefixes[0])!.Communities);
        // File prefix-source community.
        Assert.Equal([FixtureScenario.ExtraCommunity], Find(dump, FixtureScenario.ExtraPrefixes[0])!.Communities);
        // Custom-AS community (<Asn>:200 default scheme).
        Assert.Equal([FixtureScenario.CustomAsnCommunity], Find(dump, FixtureScenario.CustomAsnCatalogPrefixes[0])!.Communities);
        // Custom-prefix community (<Asn>:100 default scheme).
        Assert.Equal([FixtureScenario.CustomPrefixCommunity], Find(dump, FixtureScenario.CustomPrefix)!.Communities);
        // User URL source — deterministic FNV-1a auto-community in the reserved 5XX range.
        Assert.Equal([FnvUserSourceCommunity("peer-feed")], Find(dump, FixtureScenario.UserSourcePrefixes[0])!.Communities);
        // Every announcement carries the mandatory well-known attributes.
        Assert.All(routes, r => Assert.True(r.HasAsPath));
        Assert.All(routes, r => Assert.True(r.HasNextHop));
    }

    [Fact]
    public async Task Aggregation_MergesAdjacentPrefixes_WithinSameCommunity()
    {
        await using var stand = await StartPeerAsync(
            p => p.AsnPrefixes[714] = ["10.8.0.0/24", "10.8.1.0/24"],
            65010, lists: ["apple"]);

        using var client = new BgpTestClient(65010, routerId: 0x0A000001);
        await client.ConnectAndEstablishAsync(stand.Server.BgpPort);
        var dump = await client.CollectDumpAsync();

        Assert.Equal(["10.8.0.0/23"], Announced(dump).Select(r => r.Prefix).ToList());
        Assert.Equal([FixtureScenario.AppleCommunity], Find(dump, "10.8.0.0/23")!.Communities);
    }

    [Fact]
    public async Task Aggregation_DoesNotMergeAcrossCommunities()
    {
        await using var stand = await StartPeerAsync(
            p =>
            {
                p.AsnPrefixes[714] = ["10.8.0.0/24"];        // apple community
                p.SourcePrefixes["extra"] = ["10.8.1.0/24"]; // extra community
            },
            65010, lists: ["apple", "extra"]);

        using var client = new BgpTestClient(65010, routerId: 0x0A000001);
        await client.ConnectAndEstablishAsync(stand.Server.BgpPort);
        var prefixes = Announced(await client.CollectDumpAsync()).Select(r => r.Prefix).ToList();

        // Different community sets summarize separately: both /24s, no /23.
        Assert.Equal(["10.8.0.0/24", "10.8.1.0/24"], prefixes.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public async Task DuplicateNlri_MergedWithUnionedCommunities()
    {
        await using var stand = await StartPeerAsync(
            p =>
            {
                p.AsnPrefixes[714] = ["45.60.0.0/16"];
                p.SourcePrefixes["extra"] = ["45.60.0.0/16"];
            },
            65010, lists: ["apple", "extra"]);

        using var client = new BgpTestClient(65010, routerId: 0x0A000001);
        await client.ConnectAndEstablishAsync(stand.Server.BgpPort);
        var routes = Announced(await client.CollectDumpAsync());

        var route = Assert.Single(routes);
        Assert.Equal("45.60.0.0/16", route.Prefix);
        // One UPDATE carrying BOTH sources' communities — a receiving router keeps one path per NLRI.
        Assert.Equal(
            new[] { FixtureScenario.AppleCommunity, FixtureScenario.ExtraCommunity }.OrderBy(c => c, StringComparer.Ordinal),
            route.Communities.OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public async Task CustomPrefix_SuppressesCoveredSourcePrefixes()
    {
        await using var stand = await StartPeerAsync(
            p =>
            {
                p.AsnPrefixes[714] = ["17.253.14.0/24"];
                p.RuPrefixes.AddRange(["103.10.5.0/24", "103.10.6.0/24"]);
            },
            65010, lists: ["apple", "ru"], customPrefixes: ["103.10.0.0/16"]);

        using var client = new BgpTestClient(65010, routerId: 0x0A000001);
        await client.ConnectAndEstablishAsync(stand.Server.BgpPort);
        var dump = await client.CollectDumpAsync();

        // The operator's broader custom prefix wins; the covered source /24s are gone.
        Assert.Equal(["103.10.0.0/16", "17.253.14.0/24"], Sorted(Announced(dump).Select(r => r.Prefix)));
        Assert.Equal([FixtureScenario.CustomPrefixCommunity], Find(dump, "103.10.0.0/16")!.Communities);
    }

    [Fact]
    public async Task UnconfiguredPeer_GetsRuDefaults_Untagged()
    {
        await using var server = await RouteServerFixture.StartAsync(seed: p =>
            p.RuPrefixes.AddRange(["5.101.220.0/22", "84.201.160.0/19"]));

        using var client = new BgpTestClient(65099, routerId: 0x0A000002);
        await client.ConnectAndEstablishAsync(server.BgpPort);
        var routes = Announced(await client.CollectDumpAsync());

        Assert.Equal(
            new[] { "5.101.220.0/22", "84.201.160.0/19" }.OrderBy(p => p, StringComparer.Ordinal),
            routes.Select(r => r.Prefix).OrderBy(p => p, StringComparer.Ordinal));
        Assert.All(routes, r => Assert.Empty(r.Communities));
    }

    [Fact]
    public async Task RouteRefresh_ReSendsTheFullDump()
    {
        await using var stand = await StartFullPeerAsync();
        using var client = new BgpTestClient(FixtureScenario.FullPeerAsn, routerId: 0x0A000001);
        await client.ConnectAndEstablishAsync(stand.Server.BgpPort);
        await client.CollectDumpAsync();

        client.SendRouteRefresh();
        var refreshed = await client.CollectDumpAsync(maxTotal: TimeSpan.FromSeconds(20));

        Assert.Equal(FixtureScenario.ExpectedFullPeerPrefixes, Sorted(Announced(refreshed).Select(r => r.Prefix)));
    }

    [Fact]
    public async Task ApiChange_PushesUnsolicitedUpdate()
    {
        await using var stand = await StartFullPeerAsync();
        using var client = new BgpTestClient(FixtureScenario.FullPeerAsn, routerId: 0x0A000001);
        await client.ConnectAndEstablishAsync(stand.Server.BgpPort);
        await client.CollectDumpAsync();

        // Partial-update semantics: a non-null customPrefixes list REPLACES the set — pass both.
        var (status, body) = await stand.Server.SendJsonAsync($"/api/peers/{stand.PeerId}", "PUT", new
        {
            customPrefixes = new[] { FixtureScenario.CustomPrefix, "198.51.100.77/32" },
        });
        Assert.True(status == 200, body);

        var updates = await client.WaitForAnnouncedPrefixAsync("198.51.100.77/32");
        Assert.Contains(
            updates.SelectMany(u => u.Announced),
            r => r.Prefix == "198.51.100.77/32" && r.Communities.SequenceEqual([FixtureScenario.CustomPrefixCommunity]));
    }

    /// <summary>The per-peer MaxPrefix ceiling bounds prefixes the peer ANNOUNCES to the server (RFC 4271 §6.7); the third announced NLRI trips the Cease.</summary>
    [Fact]
    public async Task MaxPrefix_TearsDown_CeaseMaxPrefixes()
    {
        await using var stand = await StartPeerAsync(_ => { }, 65010, maxPrefix: 2);

        using var client = new BgpTestClient(65010, routerId: 0x0A000001);
        await client.ConnectAndEstablishAsync(stand.Server.BgpPort);
        client.SendUpdate(["198.51.100.0/24", "198.51.101.0/24", "203.0.113.0/24"]);

        var (code, subCode) = await client.WaitForNotificationAsync();
        Assert.Equal(BgpConstants.Error.Cease, code);
        Assert.Equal(BgpConstants.SubError.CeaseMaxPrefixes, subCode);
        Assert.True(await client.WaitForClosedAsync());
    }

    [Fact]
    public async Task DeletePeer_Established_SendsCeaseAndCloses()
    {
        await using var stand = await StartPeerAsync(
            p => p.AsnPrefixes[714] = ["17.253.14.0/24"],
            65011, lists: ["apple"]);

        using var client = new BgpTestClient(65011, routerId: 0x0A000001);
        await client.ConnectAndEstablishAsync(stand.Server.BgpPort);
        await client.CollectDumpAsync();

        var (status, deleteBody) = await stand.Server.SendJsonAsync($"/api/peers/{stand.PeerId}", "DELETE", new { });
        Assert.True(status == 200, deleteBody);

        var (code, subCode) = await client.WaitForNotificationAsync();
        Assert.Equal(BgpConstants.Error.Cease, code);
        Assert.Equal(BgpConstants.SubError.CeaseAdministrativeReset, subCode);
        Assert.True(await client.WaitForClosedAsync());
    }

    [Fact]
    public async Task FourByteAsnPeer_EstablishesViaAsTrans_ReceivesRoutes()
    {
        await using var stand = await StartPeerAsync(
            p => p.AsnPrefixes[714] = ["17.253.14.0/24"],
            4200000000, lists: ["apple"]);

        // RFC 6793 §4.1: 4-byte ASN rides the capability; the OPEN My AS field carries AS_TRANS.
        using var client = new BgpTestClient(4200000000, routerId: 0x0A000001);
        await client.ConnectAndEstablishAsync(stand.Server.BgpPort);
        Assert.Equal(BgpTestClient.AsTrans, client.OpenAsn);
        var dump = await client.CollectDumpAsync();

        Assert.Contains(Announced(dump), r => r.Prefix == "17.253.14.0/24");
    }

    [Fact]
    public async Task Open_UnacceptableHoldTime_RejectedWithNotification()
    {
        await using var server = await RouteServerFixture.StartAsync();

        // RFC 4271 §4.2/§6.2: hold time 1-2 MUST be rejected with Unacceptable Hold Time.
        using var client = new BgpTestClient(65010, routerId: 0x0A000001, holdTime: 1);
        await client.ConnectAsync(server.BgpPort);
        client.SendMessage(new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65010,
            HoldTime = 1,
            RouterId = 0x0A000001,
        });

        var (code, subCode) = await client.WaitForNotificationAsync();
        Assert.Equal(BgpConstants.Error.OpenMessageError, code);
        Assert.Equal(BgpConstants.SubError.UnacceptableHoldTime, subCode);
    }

    [Fact]
    public async Task Open_UnsupportedVersion_RejectedWithNotification()
    {
        await using var server = await RouteServerFixture.StartAsync();

        // RFC 4271 §6.2: a version-4-only speaker answers a version-3 OPEN with Unsupported Version.
        using var client = new BgpTestClient(65010, routerId: 0x0A000001);
        await client.ConnectAsync(server.BgpPort);
        client.SendMessage(new BgpOpenMessage
        {
            Version = 3,
            Asn = 65010,
            HoldTime = 0,
            RouterId = 0x0A000001,
        });

        var (code, subCode) = await client.WaitForNotificationAsync();
        Assert.Equal(BgpConstants.Error.OpenMessageError, code);
        Assert.Equal(BgpConstants.SubError.UnsupportedVersion, subCode);
    }
}
