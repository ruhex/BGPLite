using System.Text.Json;
using BGPLite.Configuration;
using BGPLite.Protocol;

namespace BGPLite.Tests.Integration;

/// <summary>
/// Wire-level protocol coverage beyond the base dump: IPv6/MP_REACH (RFC 4760/2545), hold-timer
/// expiry (RFC 4271 §6.5), Graceful-Restart-aware shutdown (RFC 4724 §4), session replacement,
/// the per-IP accept throttle, the connect-to-OPEN timeout, withdrawals on config change, AS4_PATH
/// for 2-byte peers (RFC 6793 §6), and the per-peer community allow-list (D20 default-deny).
/// </summary>
public sealed class WireProtocolE2eTests
{
    private sealed class Stand(RouteServerFixture server, string peerId) : IAsyncDisposable
    {
        public RouteServerFixture Server => server;
        public string PeerId => peerId;
        public async ValueTask DisposeAsync() => await server.DisposeAsync();
    }

    private static async Task<Stand> StartPeerAsync(
        Action<ScriptedPrefixService> seed,
        uint asn,
        BgpConfig? bgpOverride = null,
        string[]? lists = null,
        string[]? customPrefixes = null)
    {
        var server = await RouteServerFixture.StartAsync(seed: seed, bgpOverride: bgpOverride);
        var saved = await server.Store.SavePeerConfigurationAsync(
            "127.0.0.1", asn, "e2e-wire",
            lists ?? [],
            (customPrefixes ?? []).Select(FixtureScenario.SplitCidr).ToList(),
            []);
        return new Stand(server, saved.Id);
    }

    private static List<WireRoute> Announced(List<WireUpdate> dump) =>
        dump.SelectMany(u => u.Announced).ToList();

    [Fact]
    public async Task Ipv6_Negotiated_WithConfiguredNextHop_AdvertisesViaMpReach()
    {
        // RFC 2545 §3: the MP_REACH next hop must be the configured GLOBAL IPv6 address; the peer
        // must have negotiated MP IPv6/Unicast in OPEN. 2001:db8::/3e sits inside 2000::/3.
        await using var stand = await StartPeerAsync(
            p => p.SourcePrefixes["extra"] = ["2001:db8:1::/48"],
            65010,
            bgpOverride: new BgpConfig
            {
                Asn = RouteServerFixture.LocalAsn,
                RouterId = "198.51.100.1",
                HoldTime = 0,
                MaxAcceptsPerIpPerMinute = 0,
                NextHopIpv6 = "2001:db8::1",
            },
            lists: ["extra"]);

        using var client = new BgpTestClient(65010, routerId: 0x0A000001, options: new BgpClientOptions(MpV6Unicast: true));
        await client.ConnectAndEstablishAsync(stand.Server.BgpPort);
        var dump = await client.CollectDumpAsync();

        var v6 = Assert.Single(Announced(dump), r => r.Prefix == "2001:db8:1::/48");
        Assert.Equal([FixtureScenario.ExtraCommunity], v6.Communities);
    }

    [Fact]
    public async Task Ipv6_WithoutConfiguredNextHop_SuppressedFailVisibly()
    {
        // No NextHopIpv6 configured → IPv6 routes are suppressed for the send (fail-visible), the
        // peer receives only the End-of-RIB marker.
        await using var stand = await StartPeerAsync(
            p => p.SourcePrefixes["extra"] = ["2001:db8:1::/48"],
            65010,
            lists: ["extra"]);

        using var client = new BgpTestClient(65010, routerId: 0x0A000001, options: new BgpClientOptions(MpV6Unicast: true));
        await client.ConnectAndEstablishAsync(stand.Server.BgpPort);
        var dump = await client.CollectDumpAsync();

        Assert.Empty(Announced(dump));
        Assert.Contains(dump, u => u.EndOfRib);
    }

    [Fact]
    public async Task Ipv6_Negotiated_WithoutCapability_AdvertisedAsClassicWouldCorrupt_SoSuppressed()
    {
        // The peer did NOT negotiate MP IPv6/Unicast → v6 routes suppressed even with a next hop set.
        await using var stand = await StartPeerAsync(
            p => p.SourcePrefixes["extra"] = ["2001:db8:1::/48"],
            65010,
            bgpOverride: new BgpConfig
            {
                Asn = RouteServerFixture.LocalAsn,
                RouterId = "198.51.100.1",
                HoldTime = 0,
                MaxAcceptsPerIpPerMinute = 0,
                NextHopIpv6 = "2001:db8::1",
            },
            lists: ["extra"]);

        using var client = new BgpTestClient(65010, routerId: 0x0A000001); // no MP v6 capability
        await client.ConnectAndEstablishAsync(stand.Server.BgpPort);
        var dump = await client.CollectDumpAsync();

        Assert.Empty(Announced(dump));
    }

    /// <summary>Hold time 3s negotiated, the client then goes silent — RFC 4271 §6.5: the server MUST send Hold Timer Expired and drop to Idle.</summary>
    [Fact]
    public async Task HoldTimerExpiry_SendsNotification_AndCloses()
    {
        await using var stand = await StartPeerAsync(
            p => p.AsnPrefixes[714] = ["17.253.14.0/24"],
            65010,
            bgpOverride: new BgpConfig
            {
                Asn = RouteServerFixture.LocalAsn,
                RouterId = "198.51.100.1",
                HoldTime = 3,
                KeepAlive = 1,                    // Validate: 1..max(HoldTime/3,1)
                MaxAcceptsPerIpPerMinute = 0,
            },
            lists: ["apple"]);

        using var client = new BgpTestClient(65010, routerId: 0x0A000001, holdTime: 3);
        await client.ConnectAndEstablishAsync(stand.Server.BgpPort);
        // Silence: the client never sends KEEPALIVEs after the handshake.

        var (code, _) = await client.WaitForNotificationAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(BgpConstants.Error.HoldTimerExpired, code);
        Assert.True(await client.WaitForClosedAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task GracefulRestartShutdown_SilentClose_NoNotification()
    {
        // RFC 4724 §4: GR-aware shutdown drops the TCP connection so peers retain routes —
        // deliberately NOT a Cease (the default fixture policy, GracefulRestart=true).
        await using var stand = await StartPeerAsync(
            p => p.AsnPrefixes[714] = ["17.253.14.0/24"],
            65010, lists: ["apple"]);

        using var client = new BgpTestClient(65010, routerId: 0x0A000001);
        await client.ConnectAndEstablishAsync(stand.Server.BgpPort);
        await client.CollectDumpAsync();

        await stand.Server.Server.StopAsync(CancellationToken.None);

        Assert.True(await client.WaitForClosedAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(client.Notifications);
    }

    [Fact]
    public async Task NonGracefulShutdown_SendsSingleCease()
    {
        // GR disabled → shutdown MUST send exactly one NOTIFICATION (Cease, Administrative Reset).
        await using var stand = await StartPeerAsync(
            p => p.AsnPrefixes[714] = ["17.253.14.0/24"],
            65010,
            bgpOverride: new BgpConfig
            {
                Asn = RouteServerFixture.LocalAsn,
                RouterId = "198.51.100.1",
                HoldTime = 0,
                MaxAcceptsPerIpPerMinute = 0,
                GracefulRestart = false,
            },
            lists: ["apple"]);

        using var client = new BgpTestClient(65010, routerId: 0x0A000001);
        await client.ConnectAndEstablishAsync(stand.Server.BgpPort);
        await client.CollectDumpAsync();

        await stand.Server.Server.StopAsync(CancellationToken.None);

        var (code, subCode) = await client.WaitForNotificationAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(BgpConstants.Error.Cease, code);
        Assert.Equal(BgpConstants.SubError.CeaseAdministrativeReset, subCode);
        Assert.True(await client.WaitForClosedAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SameIpAndAsn_SecondConnection_CoexistsWithTheFirst()
    {
        // Session keys are (IP, source port): two sessions from one source IP with the same ASN are
        // distinct peers behind a NAT and COEXIST — neither closes the other. (Replacement happens
        // only on a true (IP, port) key collision.)
        await using var stand = await StartPeerAsync(
            p => p.AsnPrefixes[714] = ["17.253.14.0/24"],
            65010, lists: ["apple"]);

        using var first = new BgpTestClient(65010, routerId: 0x0A000001);
        await first.ConnectAndEstablishAsync(stand.Server.BgpPort);
        await first.CollectDumpAsync();

        using var second = new BgpTestClient(65010, routerId: 0x0A000002);
        await second.ConnectAndEstablishAsync(stand.Server.BgpPort);
        Assert.Contains(Announced(await second.CollectDumpAsync()), r => r.Prefix == "17.253.14.0/24");

        // The first session is untouched and still receives unsolicited updates.
        var (status, body) = await stand.Server.SendJsonAsync($"/api/peers/{stand.PeerId}", "PUT", new
        {
            lists = new[] { "apple" },
            customPrefixes = new[] { "198.51.100.77/32" },
        });
        Assert.True(status == 200, body);
        Assert.Contains(
            (await first.WaitForAnnouncedPrefixAsync("198.51.100.77/32")).SelectMany(u => u.Announced),
            r => r.Prefix == "198.51.100.77/32");
    }

    [Fact]
    public async Task AcceptThrottle_ExcessConnectFromOneIp_ClosedWithoutSession()
    {
        // Bgp.MaxAcceptsPerIpPerMinute=1: the second connect from the same IP is accepted by the
        // kernel then closed immediately — no OPEN, no session.
        await using var server = await RouteServerFixture.StartAsync(
            bgpOverride: new BgpConfig
            {
                Asn = RouteServerFixture.LocalAsn,
                RouterId = "198.51.100.1",
                HoldTime = 0,
                MaxAcceptsPerIpPerMinute = 1,
            });

        using var first = new BgpTestClient(65010, routerId: 0x0A000001);
        await first.ConnectAndEstablishAsync(server.BgpPort);

        using var second = new BgpTestClient(65011, routerId: 0x0A000002);
        await second.ConnectAsync(server.BgpPort);

        Assert.True(await second.WaitForClosedAsync(TimeSpan.FromSeconds(10)), "throttled connect must be closed");
        Assert.Null(second.ServerOpen);
    }

    [Fact]
    public async Task OpenTimeout_SilentConnection_ClosedWithoutNotification()
    {
        // Bgp.OpenTimeoutSeconds=1 (Slowloris defense): a TCP connection that never sends OPEN is
        // dropped within the window, without a NOTIFICATION (no OPEN to answer).
        await using var server = await RouteServerFixture.StartAsync(
            bgpOverride: new BgpConfig
            {
                Asn = RouteServerFixture.LocalAsn,
                RouterId = "198.51.100.1",
                HoldTime = 0,
                MaxAcceptsPerIpPerMinute = 0,
                OpenTimeoutSeconds = 1,
            });

        using var client = new BgpTestClient(65010, routerId: 0x0A000001);
        await client.ConnectAsync(server.BgpPort);

        Assert.True(await client.WaitForClosedAsync(TimeSpan.FromSeconds(10)), "silent connection must hit the OPEN timeout");
        Assert.Empty(client.Notifications);
    }

    [Fact]
    public async Task Refresh_WithoutNegotiatedCapability_Ignored()
    {
        // RFC 2918: a ROUTE-REFRESH from a peer that never advertised the capability is ignored.
        await using var stand = await StartPeerAsync(
            p => p.AsnPrefixes[714] = ["17.253.14.0/24"],
            65010, lists: ["apple"]);

        using var client = new BgpTestClient(65010, routerId: 0x0A000001, options: new BgpClientOptions(RouteRefresh: false));
        await client.ConnectAndEstablishAsync(stand.Server.BgpPort);
        await client.CollectDumpAsync();
        var updatesBefore = client.Updates.Count;

        client.SendRouteRefresh();
        await Task.Delay(2500); // > MinRouteRefreshInterval; no second dump may arrive

        Assert.Equal(updatesBefore, client.Updates.Count);
    }

    [Fact]
    public async Task ConfigChange_WithdrawsRemovedPrefixesOnTheWire()
    {
        await using var stand = await StartPeerAsync(
            p => p.AsnPrefixes[714] = ["17.253.14.0/24"],
            65010, lists: ["apple"], customPrefixes: ["103.10.0.0/16"]);

        using var client = new BgpTestClient(65010, routerId: 0x0A000001);
        await client.ConnectAndEstablishAsync(stand.Server.BgpPort);
        await client.CollectDumpAsync();

        var (status, body) = await stand.Server.SendJsonAsync($"/api/peers/{stand.PeerId}", "PUT", new
        {
            customPrefixes = Array.Empty<string>(),
        });
        Assert.True(status == 200, body);

        await client.WaitForWithdrawnAsync("103.10.0.0/16");
        // After the refresh settles, the prefix is gone from the advertised set.
        var refreshed = await client.CollectDumpAsync(maxTotal: TimeSpan.FromSeconds(8));
        Assert.DoesNotContain(Announced(refreshed), r => r.Prefix == "103.10.0.0/16");
    }

    [Fact]
    public async Task TwoBytePeer_FromFourByteServer_ReceivesAs4Path()
    {
        // RFC 6793 §6: local AS 4200000000 → AS_PATH rides 2-byte encoding (AS_TRANS) and the real
        // path travels in AS4_PATH toward a peer without the 4-octet capability.
        await using var stand = await StartPeerAsync(
            p => p.AsnPrefixes[714] = ["17.253.14.0/24"],
            65010,
            bgpOverride: new BgpConfig
            {
                Asn = 4200000000,
                RouterId = "198.51.100.1",
                HoldTime = 0,
                MaxAcceptsPerIpPerMinute = 0,
            },
            lists: ["apple"]);

        using var client = new BgpTestClient(65010, routerId: 0x0A000001, options: new BgpClientOptions(FourOctetAsn: false));
        await client.ConnectAndEstablishAsync(stand.Server.BgpPort);
        // The server's own OPEN carries AS_TRANS in My AS with the real ASN in the capability.
        Assert.Equal(BgpTestClient.AsTrans, client.ServerOpen!.Asn);

        var route = Assert.Single(Announced(await client.CollectDumpAsync()));
        Assert.Equal("17.253.14.0/24", route.Prefix);
        Assert.True(route.HasAsPath);
        Assert.True(route.HasAs4Path);
    }

    [Fact]
    public async Task CommunityAllowList_DefaultDenyUntagged_DropsOtherTags()
    {
        // D20: an active allow-list consents to SPECIFIC tags — untagged and foreign-tagged routes
        // are dropped; only the allow-listed community's prefixes reach the wire.
        await using var stand = await StartPeerAsync(
            p =>
            {
                p.AsnPrefixes[714] = ["17.253.14.0/24"];
                p.SourcePrefixes["extra"] = ["45.12.97.0/24"];
            },
            65010, lists: ["apple", "extra"]);
        await stand.Server.Store.SetCommunitiesAsync(stand.PeerId, [CommunityCodec.Parse(FixtureScenario.AppleCommunity)]);

        var detail = await stand.Server.GetJsonAsync($"/api/peers/{stand.PeerId}");
        Assert.False(detail.GetProperty("allRoutes").GetBoolean());

        using var client = new BgpTestClient(65010, routerId: 0x0A000001);
        await client.ConnectAndEstablishAsync(stand.Server.BgpPort);
        var prefixes = Announced(await client.CollectDumpAsync()).Select(r => r.Prefix).ToList();

        Assert.Equal(["17.253.14.0/24"], prefixes);
    }
}
