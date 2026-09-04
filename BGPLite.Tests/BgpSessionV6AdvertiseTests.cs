using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// #14 phase 4: outbound IPv6 advertisement — the family split at the send path. IPv4 rides
/// classic NLRI with a NEXT_HOP (RFC 4271 §4.3); IPv6 rides MP_REACH_NLRI with the next hop
/// inside the attribute (RFC 4760 §5, RFC 2545 §3) and ONLY when the peer negotiated
/// MP IPv6/Unicast AND Bgp.NextHopIpv6 is configured. Withdrawals mirror the split (RFC 4760 §7).
/// </summary>
public class BgpSessionV6AdvertiseTests
{
    private static readonly UInt128 V6NextHop = ((UInt128)0x2001 << 112) | ((UInt128)0x0DB8 << 96) | 1;
    private static readonly UInt128 V6PrefixNet = ((UInt128)0x2001 << 112) | ((UInt128)0x0DB8 << 96);

    private static BgpConfig Config(bool withV6NextHop = true) => new()
    {
        Asn = 65001,
        RouterId = "127.0.0.1",
        HoldTime = 0,
        KeepAlive = 0,
        NextHopIpv6 = withV6NextHop ? "2001:db8::1" : null
    };

    private static Route V6Route() => new()
    {
        Prefix = V6PrefixNet,
        IsIpv4 = false,
        PrefixLength = 48,
        NextHop = V6NextHop
    };

    private static async Task<(BgpSession Session, Task Run, ScriptedConnection Conn)> EstablishAsync(
        RouteTable routeTable, BgpConfig? config = null, bool negotiateMpV6 = true, bool routeRefresh = false)
    {
        var conn = new ScriptedConnection();
        var session = new BgpSession(
            conn,
            new PeerConfig { Address = "127.0.0.1" },
            config ?? Config(),
            routeTable,
            AllowAllFilter.Instance,
            new BgpMetrics(),
            NullLogger<BgpSession>.Instance);

        var run = session.RunAsync();
        var capabilities = new List<BgpCapabilityInfo> { BgpCapabilityInfo.FourOctetAsn(65002) };
        if (negotiateMpV6)
            capabilities.Add(BgpCapabilityInfo.MultiprotocolIpv6Unicast());
        if (routeRefresh)
            capabilities.Add(BgpCapabilityInfo.RouteRefresh());
        conn.EnqueueMessage(new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = 0,
            RouterId = 0x0A000002,
            Capabilities = capabilities
        });
        conn.EnqueueMessage(BgpKeepaliveMessage.Instance);

        for (var i = 0; i < 200 && !session.IsEstablished; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        Assert.True(session.IsEstablished, "session must reach Established");
        return (session, run, conn);
    }

    [Fact]
    public async Task RouteRefresh_Afi2_RereadvertisesV6Routes()
    {
        // #420 (RFC 2918 §2): a refresh request names the AFI/SAFI to re-send. An MP-IPv6-
        // negotiated peer's AFI=2/SAFI=1 request was silently ignored — the only way to get the
        // routes re-advertised was bouncing the session. It must trigger the same debounced
        // re-announcement dump the AFI=1 request does.
        var routeTable = new RouteTable();
        routeTable.AddOrUpdate(V6Route());
        var (session, run, conn) = await EstablishAsync(routeTable, routeRefresh: true);

        // Wait for the initial dump's single MP_REACH frame before asking for a refresh.
        var initial = 0;
        for (var i = 0; i < 300 && (initial = CountMpReach(conn)) == 0; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        Assert.Equal(1, initial);

        conn.EnqueueMessage(new BgpRouteRefreshMessage { Afi = BgpConstants.Afi.IPv6, Reserved = 0, Safi = BgpConstants.Safi.Unicast });
        for (var i = 0; i < 300 && CountMpReach(conn) <= initial; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10));

        Assert.True(CountMpReach(conn) > initial, "AFI=2 ROUTE_REFRESH must re-advertise the v6 routes");

        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task RouteRefresh_Afi2_WithoutNegotiation_Ignored()
    {
        var routeTable = new RouteTable();
        routeTable.AddOrUpdate(V6Route());
        var (session, run, conn) = await EstablishAsync(routeTable, negotiateMpV6: false, routeRefresh: true);

        conn.EnqueueMessage(new BgpRouteRefreshMessage { Afi = BgpConstants.Afi.IPv6, Reserved = 0, Safi = BgpConstants.Safi.Unicast });
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        // Without MP-IPv6 negotiation no v6 advertisement exists to refresh — the request is a no-op.
        Assert.Equal(0, CountMpReach(conn));

        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task RouteRefresh_Afi1_StillRereadvertises()
    {
        // Control for the #420 gate refactor: the IPv4/Unicast refresh path is unchanged.
        var routeTable = new RouteTable();
        routeTable.AddOrUpdate(new Route { Prefix = 0x0A000000, PrefixLength = 8, NextHop = 1 });
        var (session, run, conn) = await EstablishAsync(routeTable, routeRefresh: true);

        // Wait for the initial dump before snapshotting (Established ≠ dump-complete,
        // CodeRabbit on #450).
        var initial = 0;
        for (var i = 0; i < 300 && (initial = CountUpdates(conn)) == 0; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        Assert.True(initial > 0);

        conn.EnqueueMessage(new BgpRouteRefreshMessage { Afi = BgpConstants.Afi.IPv4, Reserved = 0, Safi = BgpConstants.Safi.Unicast });
        for (var i = 0; i < 300 && CountUpdates(conn) <= initial; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10));

        Assert.True(CountUpdates(conn) > initial, "AFI=1 ROUTE_REFRESH must re-advertise");

        await TeardownAsync(session, run);
    }

    /// <summary>Serves a fixed outbound route list — the seam for driving SendRoutesAsync with
    /// inputs the shared RouteTable cannot hold (e.g. cross-community duplicates, #476).</summary>
    private sealed class StubRouteAssembler(List<Route> routes) : IRouteAssembler
    {
        public Task<List<Route>> BuildOutboundRoutesAsync(
            string peerIp, uint remoteAsn, PeerConfig filterPeerConfig, string peerLabel, CancellationToken ct)
            => Task.FromResult(routes);
    }

    private static int CountUpdates(ScriptedConnection conn) =>
        conn.Sent.Select(f => BgpMessageReader.ReadMessage(f)).OfType<BgpUpdateMessage>().Count();

    private static int CountMpReach(ScriptedConnection conn) =>
        conn.Sent.Select(f => BgpMessageReader.ReadMessage(f)).OfType<BgpUpdateMessage>().Count(u => u.MpReachV6 is not null);

    private static async Task TeardownAsync(BgpSession session, Task run)
    {
        session.Dispose();
        try { await run.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
    }

    /// <summary>All UPDATEs the session put on the wire, polled until the expected frame shows
    /// up (the initial dump runs concurrently with the test thread).</summary>
    private static async Task<List<BgpUpdateMessage>> SentUpdatesAsync(ScriptedConnection conn, Func<BgpUpdateMessage, bool> until)
    {
        var updates = new List<BgpUpdateMessage>();
        for (var i = 0; i < 200; i++)
        {
            updates.Clear();
            foreach (var frame in conn.Sent)
                if (BgpMessageReader.ReadMessage(frame) is BgpUpdateMessage u)
                    updates.Add(u);
            if (updates.Any(until)) break;
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
        return updates;
    }

    [Fact]
    public async Task Negotiated_WithNextHop_V6RoutesRideMpReach()
    {
        var routeTable = new RouteTable();
        routeTable.AddOrUpdate(V6Route());                       // unowned seed → shared-table dump
        routeTable.AddOrUpdate(new Route { Prefix = 0x0A000000, PrefixLength = 8, NextHop = 1 });

        var (session, run, conn) = await EstablishAsync(routeTable);

        var updates = await SentUpdatesAsync(conn, u => u.MpReachV6 is not null);
        var v6Update = Assert.Single(updates, u => u.MpReachV6 is not null);
        Assert.Equal(V6NextHop, v6Update.MpReachV6!.Value.NextHop);
        var pfx = Assert.Single(v6Update.MpReachV6!.Value.Prefixes);
        Assert.Equal((V6PrefixNet, (byte)48), (pfx.Address, pfx.Length));
        // RFC 4760 §5: the MP_REACH UPDATE carries no classic NLRI and no classic NEXT_HOP.
        Assert.Empty(v6Update.Nlri);
        Assert.DoesNotContain(v6Update.PathAttributes, a => a.TypeCode == BgpConstants.Attribute.NextHop);
        // ...while the IPv4 route still rides the classic path.
        Assert.Contains(updates, u => u.Nlri.Any(p => p.Address == 0x0A000000 && p.Length == 8));

        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task V6Announcement_IsRoundTrippable_ByTheRealReader()
    {
        // The wire shape must survive our own reader: AFI/SAFI 2/1, global next hop, prefixes.
        var routeTable = new RouteTable();
        routeTable.AddOrUpdate(V6Route());
        var (session, run, conn) = await EstablishAsync(routeTable);

        var mp = Assert.Single(await SentUpdatesAsync(conn, u => u.MpReachV6 is not null), u => u.MpReachV6 is not null).MpReachV6!.Value;
        var value = MpReachCodec.EncodeMpReachV6(mp.NextHop, mp.Prefixes);
        var decoded = MpReachCodec.DecodeMpReachV6(value);
        Assert.Equal(V6NextHop, decoded.NextHop);
        Assert.Equal(48, Assert.Single(decoded.Prefixes).Length);

        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task NoNegotiation_V6RoutesSuppressed_Ipv4Untouched()
    {
        var routeTable = new RouteTable();
        routeTable.AddOrUpdate(V6Route());
        routeTable.AddOrUpdate(new Route { Prefix = 0x0A000000, PrefixLength = 8, NextHop = 1 });

        var (session, run, conn) = await EstablishAsync(routeTable, negotiateMpV6: false);

        var updates = await SentUpdatesAsync(conn, u => u.Nlri.Any(p => p.Address == 0x0A000000));
        Assert.DoesNotContain(updates, u => u.MpReachV6 is not null);
        Assert.DoesNotContain(updates, u => u.Nlri.Any(p => !p.IsIpv4));
        Assert.Contains(updates, u => u.Nlri.Any(p => p.Address == 0x0A000000)); // v4 unaffected

        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task NoConfiguredNextHop_V6RoutesSuppressed()
    {
        var routeTable = new RouteTable();
        routeTable.AddOrUpdate(V6Route());

        var (session, run, conn) = await EstablishAsync(routeTable, config: Config(withV6NextHop: false));

        // The End-of-RIB empty UPDATE always follows Established, so "any UPDATE seen" is the
        // reachable condition; the assertion is that none of them carries MP_REACH.
        Assert.DoesNotContain(await SentUpdatesAsync(conn, u => u.Nlri.Count == 0 && u.WithdrawnRoutes.Count == 0), u => u.MpReachV6 is not null);

        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task Withdrawal_V6PrefixesRideMpUnreach_OnTheWire()
    {
        // The refresh cycle withdraws EVERYTHING it advertised, then re-sends. After the table
        // is drained the withdrawal pass must split by family: the v4 seed via the classic
        // withdrawn field, the v6 seed via an MP_UNREACH-only UPDATE (RFC 4760 §7).
        var routeTable = new RouteTable();
        routeTable.AddOrUpdate(V6Route());
        routeTable.AddOrUpdate(new Route { Prefix = 0x0A000000, PrefixLength = 8, NextHop = 1 });

        var (session, run, conn) = await EstablishAsync(routeTable);
        // Let the initial dump finish (its MP_REACH UPDATE is the completion signal) — a refresh
        // racing the dump would withdraw a half-filled mirror and prove nothing.
        await SentUpdatesAsync(conn, u => u.MpReachV6 is not null);
        await session.RefreshRoutesAsync(); // withdraw-all + resend against the same table

        var updates = await SentUpdatesAsync(conn, u => u.MpUnreachV6 is { Count: > 0 });
        Assert.True(updates.Any(u => u.MpUnreachV6 is { Count: > 0 }),
            "no MP_UNREACH UPDATE on the wire; sent updates: " +
            string.Join("; ", updates.Select(u =>
                $"Wd={u.WithdrawnRoutes.Count} Nlri={u.Nlri.Count} Reach={(u.MpReachV6 is null ? "-" : u.MpReachV6.Value.Prefixes.Count.ToString())} Unreach={u.MpUnreachV6?.Count.ToString() ?? "-"}")));
        var mpUnreach = Assert.Single(updates, u => u.MpUnreachV6 is { Count: > 0 });
        Assert.Empty(mpUnreach.WithdrawnRoutes);                          // v6 never rides the classic field
        // (the reader moves the type-15 attribute into MpUnreachV6, so PathAttributes is empty here)
        var withdrawn = Assert.Single(mpUnreach.MpUnreachV6!);
        Assert.Equal((V6PrefixNet, (byte)48), (withdrawn.Address, withdrawn.Length));
        // ...and the v4 seed's withdrawal still rides the classic withdrawn field.
        Assert.Contains(updates, u => u.WithdrawnRoutes.Any(w => w.Address == 0x0A000000 && w.Length == 8));

        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task EndOfRib_Ipv6Family_SentAfterInitialDump()
    {
        // RFC 4724 §2: End-of-RIB per AFI. The IPv6 EoR is an UPDATE carrying an EMPTY
        // MP_UNREACH_NLRI (AFI=2/SAFI=1) — sent after the initial dump to MP-IPv6 peers.
        var routeTable = new RouteTable();
        routeTable.AddOrUpdate(V6Route());
        var (session, run, conn) = await EstablishAsync(routeTable);

        var eor = await SentUpdatesAsync(conn, u => u.MpUnreachV6 is { Count: 0 });
        var mpEor = Assert.Single(eor, u => u.MpUnreachV6 is { Count: 0 });
        Assert.Empty(mpEor.Nlri);

        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task DuplicateV6Prefixes_AcrossCommunitySets_MergeKeepsTheFamily()
    {
        // #476: two sources can announce the SAME IPv6 prefix with different community sets (the
        // aggregator keeps them in separate groups); MergeDuplicatePrefixes then unions them. The
        // merged route must stay IPv6 — before the fix the family bit was lost, the route landed in
        // the IPv4 batch and its /48 length crashed the send (AOORE → Cease → teardown on every
        // connect). The union must ride ONE MP_REACH UPDATE carrying both communities.
        var routes = new List<Route>
        {
            new() { Prefix = V6PrefixNet, IsIpv4 = false, PrefixLength = 48, NextHop = V6NextHop, Communities = [200u] },
            new() { Prefix = V6PrefixNet, IsIpv4 = false, PrefixLength = 48, NextHop = V6NextHop, Communities = [100u] }
        };
        var conn = new ScriptedConnection();
        var session = new BgpSession(
            conn,
            new PeerConfig { Address = "127.0.0.1" },
            Config(),
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            NullLogger<BgpSession>.Instance,
            routeAssembler: new StubRouteAssembler(routes));
        var run = session.RunAsync();
        conn.EnqueueMessage(new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = 0,
            RouterId = 0x0A000002,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002), BgpCapabilityInfo.MultiprotocolIpv6Unicast()]
        });
        conn.EnqueueMessage(BgpKeepaliveMessage.Instance);
        for (var i = 0; i < 200 && !session.IsEstablished; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10));

        var updates = await SentUpdatesAsync(conn, u => u.MpReachV6 is not null);
        var v6Update = Assert.Single(updates, u => u.MpReachV6 is not null);
        var pfx = Assert.Single(v6Update.MpReachV6!.Value.Prefixes);
        Assert.False(pfx.IsIpv4, "the merged duplicate must stay IPv6");
        Assert.Equal((V6PrefixNet, (byte)48), (pfx.Address, pfx.Length));
        Assert.Empty(v6Update.Nlri); // never a bogus classic-IPv4 NLRI for a v6 prefix
        var communityAttr = Assert.Single(v6Update.PathAttributes, a => a.TypeCode == BgpConstants.Attribute.Community);
        Assert.Equal(new[] { 100u, 200u }, AttributeHelper.ReadCommunities(communityAttr));
        Assert.True(session.IsEstablished, "the duplicate merge must not tear the session down");

        await TeardownAsync(session, run);
    }

    [Fact]
    public void MpUnreachAttribute_IsOptionalNonTransitive()
    {
        var attr = UpdateCodec.WithMpUnreachV6Attribute([new IpPrefix(V6PrefixNet, 48, isIpv4: false)]).Single();
        Assert.Equal(MpReachCodec.MpUnreachNlriType, attr.TypeCode);
        Assert.True(attr.Optional);
        Assert.False(attr.Transitive);
        Assert.True(MpReachCodec.DecodeMpUnreachV6(attr.Data).Single().IsIpv4 == false);
    }

    [Fact]
    public void NextHopIpv6_Validation()
    {
        // Global unicast (2000::/3) is accepted; link-local, ULA, multicast, mapped and garbage
        // are rejected with a message that names the knob.
        new BgpConfig { Asn = 65001, RouterId = "1.2.3.4", NextHopIpv6 = "2001:db8::1" }.Validate();

        foreach (var bad in new[] { "fe80::1", "fc00::1", "ff02::1", "::ffff:192.0.2.1", "2001:db8::1/48", "1.2.3.4", "not-an-address" })
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => new BgpConfig { Asn = 65001, RouterId = "1.2.3.4", NextHopIpv6 = bad }.Validate());
            Assert.Contains("NextHopIpv6", ex.Message);
        }
    }
}
