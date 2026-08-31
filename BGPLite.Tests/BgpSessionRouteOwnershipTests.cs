using System.Threading.Channels;
using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// #289: a withdrawal must remove the route received FROM THAT PEER (RFC 4271 §3.2/§9), not
/// whatever happens to sit at the prefix. BGPLite keeps one shared <see cref="RouteTable"/> rather
/// than per-peer Adj-RIBs-In, so <c>BgpSession</c> tracks what it installed and gates removals on
/// it. Without that, any peer that completed a handshake could delete prefixes seeded at startup by
/// <c>RouteSeedingService</c>, or routes announced by another peer, just by listing them as
/// withdrawn — verified on <c>main</c> before the fix: a peer that announced nothing emptied a
/// two-route table.
/// <para>
/// Driven through the <c>IBgpConnection</c> seam (#96) rather than loopback sockets: the assertions
/// are about route-table state, so scripting frames deterministically is both faster and immune to
/// the timing flakiness that #302 documents.
/// </para>
/// </summary>
public class BgpSessionRouteOwnershipTests
{
    private const uint TenSlashEight = 0x0A000000;
    private const uint TestNetSlash24 = 0xC0000200;

    [Fact]
    public async Task Withdrawal_ForPrefixNeverAnnounced_IsIgnored()
    {
        var routeTable = Seeded((TenSlashEight, 8), (TestNetSlash24, 24));
        var (session, run, conn) = await EstablishAsync(routeTable);

        // The peer has announced nothing at all, and withdraws both seeded prefixes.
        conn.EnqueueFrame(WithdrawFrame([8, 0x0A], [24, 0xC0, 0x00, 0x02]));
        await SettleAsync(conn);

        Assert.Equal(2, routeTable.Count);
        Assert.NotNull(routeTable.Get(TenSlashEight, 8));
        Assert.NotNull(routeTable.Get(TestNetSlash24, 24));
        Assert.True(session.IsEstablished, "an ignored withdrawal is not a protocol error");

        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task Withdrawal_ForItsOwnAnnouncement_RemovesTheRoute()
    {
        var routeTable = new RouteTable();
        var (session, run, conn) = await EstablishAsync(routeTable);

        conn.EnqueueFrame(AnnounceFrame(8, 0x0A));
        await SettleAsync(conn, () => routeTable.Count == 1);
        Assert.NotNull(routeTable.Get(TenSlashEight, 8));

        conn.EnqueueFrame(WithdrawFrame([8, 0x0A]));
        await SettleAsync(conn, () => routeTable.Count == 0);

        Assert.Null(routeTable.Get(TenSlashEight, 8));

        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task Withdrawal_DoesNotRemoveAnotherPeersRoute()
    {
        var routeTable = new RouteTable();
        var (owner, ownerRun, ownerConn) = await EstablishAsync(routeTable, routerId: 0x0A000002);
        var (other, otherRun, otherConn) = await EstablishAsync(routeTable, routerId: 0x0A000003);

        ownerConn.EnqueueFrame(AnnounceFrame(8, 0x0A));
        await SettleAsync(ownerConn, () => routeTable.Count == 1);

        // The second peer withdraws a prefix the FIRST one announced.
        otherConn.EnqueueFrame(WithdrawFrame([8, 0x0A]));
        await SettleAsync(otherConn);
        Assert.NotNull(routeTable.Get(TenSlashEight, 8));

        // The peer that announced it can still withdraw it.
        ownerConn.EnqueueFrame(WithdrawFrame([8, 0x0A]));
        await SettleAsync(ownerConn, () => routeTable.Count == 0);
        Assert.Null(routeTable.Get(TenSlashEight, 8));

        await TeardownAsync(owner, ownerRun);
        await TeardownAsync(other, otherRun);
    }

    [Fact]
    public async Task FilteredAnnouncement_IsNotOwned_SoItsWithdrawalRemovesNothing()
    {
        // Ownership follows what actually reached the table, not what was announced: a route the
        // incoming filter dropped was never installed, so a later withdrawal for it must not remove
        // a same-prefix route that came from somewhere else.
        var routeTable = Seeded((TenSlashEight, 8));
        var (session, run, conn) = await EstablishAsync(routeTable, filter: new RejectAllIncomingFilter());

        conn.EnqueueFrame(AnnounceFrame(8, 0x0A));
        await SettleAsync(conn);
        Assert.Equal(1, routeTable.Count); // the announce was filtered out, the seed still stands

        conn.EnqueueFrame(WithdrawFrame([8, 0x0A]));
        await SettleAsync(conn);

        Assert.NotNull(routeTable.Get(TenSlashEight, 8));

        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task TreatAsWithdraw_OnlyRemovesRoutesThisPeerInstalled()
    {
        // The interaction with #288: treat-as-withdraw removes the UPDATE's NLRI, but it is still a
        // withdrawal and obeys the same ownership rule. Otherwise a malformed UPDATE becomes a way
        // to delete any prefix — strictly worse than the plain withdrawal this fix closes.
        var routeTable = Seeded((TenSlashEight, 8));
        var (session, run, conn) = await EstablishAsync(routeTable);

        // Announcing UPDATE for the seeded prefix with NEXT_HOP omitted: the pipeline rejects it and
        // treat-as-withdraw applies to 10.0.0.0/8 — which this peer never installed.
        conn.EnqueueFrame(MalformedAnnounceFrame(8, 0x0A));
        await SettleAsync(conn);

        Assert.NotNull(routeTable.Get(TenSlashEight, 8));
        Assert.True(session.IsEstablished, "treat-as-withdraw keeps the session up");

        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task Withdrawal_AfterAnotherPeerReplacedTheRoute_RemovesNothing()
    {
        // The residual hole in the first version of this fix, caught in review: ownership was tracked
        // per session, but RouteTable.AddOrUpdate replaces by prefix. Peer A announces, peer B
        // announces the SAME prefix (replacing A's route), then A withdraws — and A's set still said
        // it owned that prefix, so it deleted B's route. Two peers announcing one prefix is ordinary
        // for a route server, so this was reachable without anything adversarial.
        var routeTable = new RouteTable();
        var (a, aRun, aConn) = await EstablishAsync(routeTable, routerId: 0x0A000002);
        var (b, bRun, bConn) = await EstablishAsync(routeTable, routerId: 0x0A000003);

        aConn.EnqueueFrame(AnnounceFrame(8, 0x0A));
        await SettleAsync(aConn, () => routeTable.Count == 1);

        // B replaces it — same prefix, different next hop, so the installed route is observably B's.
        bConn.EnqueueFrame(BuildAnnounce(AttributesWithNextHop(0x0A, 0x0A, 0x0A, 0x0A), 8, [0x0A]));
        await SettleAsync(bConn, () => routeTable.Get(TenSlashEight, 8)?.NextHop == 0x0A0A0A0A);

        // A withdraws the prefix it no longer owns.
        aConn.EnqueueFrame(WithdrawFrame([8, 0x0A]));
        await SettleAsync(aConn);

        var route = routeTable.Get(TenSlashEight, 8);
        Assert.NotNull(route);
        Assert.Equal(0x0A0A0A0Au, route!.NextHop); // still B's route

        // B, the current owner, can still withdraw it.
        bConn.EnqueueFrame(WithdrawFrame([8, 0x0A]));
        await SettleAsync(bConn, () => routeTable.Count == 0);

        await TeardownAsync(a, aRun);
        await TeardownAsync(b, bRun);
    }

    [Fact]
    public async Task SharedTableFallback_DoesNotAdvertiseRoutesInjectedByAnotherPeer()
    {
        // #307: with no peer store injected, RouteAssembler falls back to the shared table — which
        // also holds every NLRI any peer announced inbound. Advertising those would hand one peer's
        // injected routes to every other peer. The owner tag from #289 is what separates the startup
        // seed (unowned) from peer-injected entries (owned by the installing session).
        var routeTable = Seeded((TestNetSlash24, 24));   // the "startup seed"
        var (injector, injectorRun, injectorConn) = await EstablishAsync(routeTable, routerId: 0x0A000002);

        injectorConn.EnqueueFrame(AnnounceFrame(8, 0x0A));
        await SettleAsync(injectorConn, () => routeTable.Count == 2);
        Assert.NotNull(routeTable.Get(TenSlashEight, 8)); // it IS in the shared table

        // A second peer connects; its initial dump goes through the fallback.
        var (victim, victimRun, victimConn) = await EstablishAsync(routeTable, routerId: 0x0A000003);
        var advertised = await CollectAdvertisedNlriAsync(victimConn);

        Assert.Contains((TestNetSlash24, (byte)24), advertised);      // the seed reaches the peer
        Assert.DoesNotContain((TenSlashEight, (byte)8), advertised);  // the other peer's injection does not

        await TeardownAsync(injector, injectorRun);
        await TeardownAsync(victim, victimRun);
    }

    /// <summary>Waits for the initial dump and returns every NLRI the session put on the wire.</summary>
    private static async Task<List<(uint Prefix, byte Length)>> CollectAdvertisedNlriAsync(ScriptedConnection conn)
    {
        var nlri = new List<(uint, byte)>();
        for (var i = 0; i < 200; i++)
        {
            nlri.Clear();
            foreach (var frame in conn.Sent)
            {
                if (BgpMessageReader.ReadMessage(frame) is not BgpUpdateMessage update) continue;
                foreach (var p in update.Nlri)
                    nlri.Add((p.Address, p.Length));
            }
            if (nlri.Count > 0) break;
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
        return nlri;
    }

    // ---- frame builders ----

    private static byte[] Frame(BgpMessageType type, params byte[] payload)
    {
        var frame = new byte[BgpConstants.MessageHeaderSize + payload.Length];
        BgpConstants.Marker.CopyTo(frame.AsSpan(0, BgpConstants.MarkerSize));
        frame[16] = (byte)(frame.Length >> 8);
        frame[17] = (byte)frame.Length;
        frame[18] = (byte)type;
        payload.CopyTo(frame, BgpConstants.MessageHeaderSize);
        return frame;
    }

    /// <summary>
    /// UPDATE carrying only withdrawn routes. Each argument is one NLRI already in wire form —
    /// the prefix-length byte followed by its significant address bytes, e.g. <c>[8, 0x0A]</c>.
    /// </summary>
    private static byte[] WithdrawFrame(params byte[][] prefixes)
    {
        var withdrawn = new List<byte>();
        foreach (var nlri in prefixes)
            withdrawn.AddRange(nlri);
        var payload = new List<byte> { (byte)(withdrawn.Count >> 8), (byte)withdrawn.Count };
        payload.AddRange(withdrawn);
        payload.AddRange([0x00, 0x00]); // no path attributes
        return Frame(BgpMessageType.Update, [.. payload]);
    }

    // The handshake below negotiates the 4-octet-ASN capability, so AS_PATH is 4-octet encoded.
    private static readonly byte[] WellFormedAttributes =
    [
        0x40, 0x01, 0x01, 0x00,                                 // ORIGIN igp
        0x40, 0x02, 0x06, 0x02, 0x01, 0x00, 0x00, 0x00, 0x64,   // AS_PATH seq [100]
        0x40, 0x03, 0x04, 0xC0, 0x00, 0x02, 0x01,               // NEXT_HOP 192.0.2.1
    ];

    /// <summary>Well-formed ORIGIN + AS_PATH + NEXT_HOP, with the next hop supplied by the caller.</summary>
    private static byte[] AttributesWithNextHop(params byte[] nextHop)
    {
        var attrs = new List<byte>
        {
            0x40, 0x01, 0x01, 0x00,
            0x40, 0x02, 0x06, 0x02, 0x01, 0x00, 0x00, 0x00, 0x64,
            0x40, 0x03, 0x04,
        };
        attrs.AddRange(nextHop);
        return [.. attrs];
    }

    // ORIGIN + AS_PATH but no NEXT_HOP — Missing Well-known Attribute, so treat-as-withdraw applies.
    private static readonly byte[] AttributesMissingNextHop =
    [
        0x40, 0x01, 0x01, 0x00,
        0x40, 0x02, 0x06, 0x02, 0x01, 0x00, 0x00, 0x00, 0x64,
    ];

    private static byte[] AnnounceFrame(byte prefixLength, params byte[] addressBytes) =>
        BuildAnnounce(WellFormedAttributes, prefixLength, addressBytes);

    private static byte[] MalformedAnnounceFrame(byte prefixLength, params byte[] addressBytes) =>
        BuildAnnounce(AttributesMissingNextHop, prefixLength, addressBytes);

    private static byte[] BuildAnnounce(byte[] attributes, byte prefixLength, byte[] addressBytes)
    {
        var payload = new List<byte>
        {
            0x00, 0x00,                                                    // no withdrawn routes
            (byte)(attributes.Length >> 8), (byte)attributes.Length,
        };
        payload.AddRange(attributes);
        payload.Add(prefixLength);
        payload.AddRange(addressBytes);
        return Frame(BgpMessageType.Update, [.. payload]);
    }

    // ---- #313: a session's routes go when the session goes ----

    /// <summary>
    /// RFC 4271 §8.2.2: every transition out of Established "deletes all routes associated with this
    /// connection". #313: nothing did. A peer's announcements outlived its session, and since no
    /// other path in the server removes an entry, a peer could disconnect, reconnect and add another
    /// batch without limit — which is also why a per-session max-prefix cap (#304) could not have
    /// contained it on its own.
    /// </summary>
    [Fact]
    public async Task SessionClose_RemovesTheRoutesThatSessionAnnounced()
    {
        var routeTable = new RouteTable();
        var (session, run, conn) = await EstablishAsync(routeTable);

        conn.EnqueueFrame(AnnounceFrame(8, 0x0A));
        await SettleAsync(conn, () => routeTable.Count == 1);

        await TeardownAsync(session, run);

        Assert.Equal(0, routeTable.Count);
    }

    /// <summary>
    /// A reconnect must not accumulate. Two sessions in sequence announcing the same and a further
    /// prefix leave exactly what the second one announced — the growth-across-reconnects property.
    /// </summary>
    [Fact]
    public async Task Reconnecting_DoesNotAccumulateTheFormerSessionsRoutes()
    {
        var routeTable = new RouteTable();

        var (first, firstRun, firstConn) = await EstablishAsync(routeTable);
        firstConn.EnqueueFrame(AnnounceFrame(8, 0x0A));
        await SettleAsync(firstConn, () => routeTable.Count == 1);
        await TeardownAsync(first, firstRun);

        var (second, secondRun, secondConn) = await EstablishAsync(routeTable, routerId: 0x0A000003);
        secondConn.EnqueueFrame(AnnounceFrame(8, 0x0A));
        secondConn.EnqueueFrame(AnnounceFrame(24, 0xC0, 0x00, 0x02));
        await SettleAsync(secondConn, () => routeTable.Count == 2);

        Assert.Equal(2, routeTable.Count);

        await TeardownAsync(second, secondRun);
        Assert.Equal(0, routeTable.Count);
    }

    /// <summary>
    /// The mirror of #307's isolation property at teardown: the flush is scoped by owner, so the
    /// startup seed (written unowned by <c>RouteSeedingService</c>) survives a peer disconnecting.
    /// A flush by prefix rather than by owner would empty the table every time any peer left.
    /// </summary>
    [Fact]
    public async Task SessionClose_LeavesTheStartupSeedAndOtherPeersRoutes()
    {
        var routeTable = Seeded((TestNetSlash24, 24));
        var (keeper, keeperRun, keeperConn) = await EstablishAsync(routeTable, routerId: 0x0A000003);
        keeperConn.EnqueueFrame(AnnounceFrame(16, 0xAC, 0x10));
        await SettleAsync(keeperConn, () => routeTable.Count == 2);

        var (leaver, leaverRun, leaverConn) = await EstablishAsync(routeTable, routerId: 0x0A000004);
        leaverConn.EnqueueFrame(AnnounceFrame(8, 0x0A));
        await SettleAsync(leaverConn, () => routeTable.Count == 3);

        await TeardownAsync(leaver, leaverRun);

        Assert.Null(routeTable.Get(TenSlashEight, 8));                    // the leaver's route is gone
        Assert.NotNull(routeTable.Get(TestNetSlash24, 24));               // the seed stays
        Assert.NotNull(routeTable.Get(0xAC100000, 16));                   // the other peer's route stays

        await TeardownAsync(keeper, keeperRun);
    }

    /// <summary>
    /// The compare-and-remove rule from #289 applies to the bulk flush too: a route this session
    /// announced but another peer has since replaced belongs to the replacement, and this session
    /// closing must not take it with it.
    /// </summary>
    [Fact]
    public async Task SessionClose_DoesNotRemoveARouteAnotherPeerHasSinceReplaced()
    {
        var routeTable = new RouteTable();
        var (a, aRun, aConn) = await EstablishAsync(routeTable, routerId: 0x0A000002);
        aConn.EnqueueFrame(AnnounceFrame(8, 0x0A));
        await SettleAsync(aConn, () => routeTable.Count == 1);

        var (b, bRun, bConn) = await EstablishAsync(routeTable, routerId: 0x0A000003);
        // Same prefix, different next hop, so the installed route is observably B's.
        bConn.EnqueueFrame(BuildAnnounce(AttributesWithNextHop(0x0A, 0x0A, 0x0A, 0x0A), 8, [0x0A]));
        await SettleAsync(bConn, () => routeTable.Get(TenSlashEight, 8)?.NextHop == 0x0A0A0A0A);

        await TeardownAsync(a, aRun);

        var route = routeTable.Get(TenSlashEight, 8);
        Assert.NotNull(route);
        Assert.Equal(0x0A0A0A0Au, route!.NextHop);   // still B's

        await TeardownAsync(b, bRun);
        Assert.Equal(0, routeTable.Count);
    }

    // ---- harness ----

    private static RouteTable Seeded(params (uint Prefix, byte Length)[] routes)
    {
        var table = new RouteTable();
        foreach (var (prefix, length) in routes)
            table.AddOrUpdate(new Route { Prefix = prefix, PrefixLength = length, NextHop = 0x7F000001 });
        return table;
    }

    private static async Task<(BgpSession Session, Task Run, ScriptedConnection Conn)> EstablishAsync(
        RouteTable routeTable, uint routerId = 0x0A000002, IRouteFilter? filter = null)
    {
        var conn = new ScriptedConnection();
        var config = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        var session = new BgpSession(
            conn,
            new PeerConfig { Address = "127.0.0.1" },
            config,
            routeTable,
            filter ?? AllowAllFilter.Instance,
            new BgpMetrics(),
            NullLogger<BgpSession>.Instance);

        var run = session.RunAsync();
        conn.EnqueueMessage(new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = 0,
            RouterId = routerId,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)],
        });
        conn.EnqueueMessage(BgpKeepaliveMessage.Instance);

        for (var i = 0; i < 200 && !session.IsEstablished; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        Assert.True(session.IsEstablished, "session must reach Established");
        return (session, run, conn);
    }

    /// <summary>
    /// Waits until the scripted frames have been consumed, and — when <paramref name="until"/> is
    /// given — until the expected route-table state is reached. The frameless wait is bounded and
    /// deliberately followed by a short settle, since the assertions here are of the form "nothing
    /// happened" and need the read loop to have actually processed the frame.
    /// </summary>
    private static async Task SettleAsync(ScriptedConnection conn, Func<bool>? until = null)
    {
        var reached = false;
        for (var i = 0; i < 200; i++)
        {
            if (conn.Drained)
            {
                reached = until?.Invoke() ?? true;
                // A "nothing happened" assertion needs the read loop to have actually processed the
                // frame, not merely to have consumed its bytes — give it a few more ticks to settle.
                if (reached && (until is not null || i > 5)) break;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        // Asserted unconditionally: without it a negative test passes when the frame was never
        // delivered at all, which is exactly the failure mode it is supposed to rule out (#289 review).
        Assert.True(conn.Drained, "the scripted frame was never consumed by the read loop");
        if (until is not null)
            Assert.True(reached, "the expected route-table state was not reached in time");
    }

    private static async Task TeardownAsync(BgpSession session, Task run)
    {
        session.MarkSilentClose();
        try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { /* expected */ }
        catch (IOException) { /* expected */ }
        session.Dispose();
    }

    /// <summary>Rejects every inbound route, so nothing this peer announces reaches the table.</summary>
    /// <summary>
    /// #292 item 6: RFC 4271 §9.1.2 — a route whose AS_PATH contains the local system's ASN is
    /// excluded from selection (loop detection). It must not be installed (and the session must
    /// survive — route-level exclusion, not a protocol error), while an otherwise identical route
    /// without the local ASN installs normally.
    /// </summary>
    [Fact]
    public async Task Announce_WithLocalAsnInAsPath_IsExcludedNotInstalled()
    {
        var routeTable = new RouteTable();
        var (session, run, conn) = await EstablishAsync(routeTable);

        // AS_PATH seq [100, 65001] — 65001 is the session's own ASN (4-octet session).
        var loopingAttributes = new byte[]
        {
            0x40, 0x01, 0x01, 0x00,                                                     // ORIGIN igp
            0x40, 0x02, 0x0A, 0x02, 0x02, 0x00, 0x00, 0x00, 0x64, 0x00, 0x00, 0xFD, 0xE9, // AS_PATH [100, 65001]
            0x40, 0x03, 0x04, 0xC0, 0x00, 0x02, 0x01,                                   // NEXT_HOP 192.0.2.1
        };
        conn.EnqueueFrame(BuildAnnounce(loopingAttributes, 8, [0x0A]));
        await SettleAsync(conn); // frameless settle: the assertion is that NOTHING happened

        Assert.Equal(0, routeTable.Count);                      // RED pre-fix: the looping route installed
        Assert.True(session.IsEstablished, "a looping route is excluded, not a session error");

        // The same NLRI without the local ASN installs normally — proves exclusion, not rejection.
        conn.EnqueueFrame(AnnounceFrame(8, 0x0A));
        await SettleAsync(conn, () => routeTable.Count == 1);
        Assert.NotNull(routeTable.Get(TenSlashEight, 8));

        await TeardownAsync(session, run);
    }

    /// <summary>
    /// #265 item 1: a REPLACED session's slow finally must not flip the peer row back to
    /// inactive after the replacement wrote active. Two guards: the SilentClose teardown reason
    /// (replacement / GR-aware shutdown) skips the write, and the registration probe answers
    /// false once the session is no longer the registered one. A genuine teardown still writes.
    /// </summary>
    private sealed class RecordingPeerStore : IPeerStore
    {
        public List<(string Ip, uint Asn, bool Active)> StatusWrites = [];
        public string CreatePeer(string ip, uint asn, string? description) => "id";
        public void UpsertPeer(string ip, uint asn) { }
        public void UpdateSessionStatus(string ip, uint asn, bool active) => StatusWrites.Add((ip, asn, active));
        public void DeletePeer(string id) { }
        public PeerInfo? GetPeerByIp(string ip) => null;
        public PeerInfo? GetPeer(string ip, uint asn) => null;
        public PeerInfo? GetPeerById(string id) => null;
        public List<string> GetSubscriptions(string peerId) => [];
        public List<string> GetCustomPrefixes(string peerId) => [];
        public List<uint> GetCustomAsns(string peerId) => [];
        public HashSet<uint> GetCommunities(string peerId) => [];
        public HashSet<uint> GetCommunities(string ip, uint asn) => [];
        public void SetCommunities(string peerId, HashSet<uint> communities) { }
        public void ClearCommunities(string peerId) { }
        public void SetDescription(string id, string description) { }
        public PeerRoutingView? LoadPeerRoutingView(string ip, uint asn) => new("id", [], [], [], []);
    }

    private static async Task<(BgpSession Session, Task Run, RecordingPeerStore Store)> EstablishWithStoreAsync(
        RouteTable routeTable, Func<BgpSession, bool>? probe = null)
    {
        var conn = new ScriptedConnection();
        var store = new RecordingPeerStore();
        var config = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        var session = new BgpSession(
            conn,
            new PeerConfig { Address = "127.0.0.1" },
            config,
            routeTable,
            AllowAllFilter.Instance,
            new BgpMetrics(),
            NullLogger<BgpSession>.Instance,
            peerStore: store);
        session.StillRegisteredProbe = probe ?? (_ => true);
        var run = session.RunAsync();
        conn.EnqueueMessage(new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = 0,
            RouterId = 0x0A000002,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)],
        });
        conn.EnqueueMessage(BgpKeepaliveMessage.Instance);
        for (var i = 0; i < 200 && !session.IsEstablished; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        Assert.True(session.IsEstablished, "session must reach Established");
        return (session, run, store);
    }

    [Fact]
    public async Task ReplacedSession_Teardown_DoesNotOverwriteStatusInactive()
    {
        // The probe answers false — the registry no longer holds this session (TryUpdate
        // swapped a replacement in). Its finally must not write inactive.
        var (session, run, store) = await EstablishWithStoreAsync(new RouteTable(), probe: _ => false);

        await TeardownAsync(session, run);   // SilentClose + probe-false — both guards skip

        Assert.DoesNotContain(store.StatusWrites, w => !w.Active);
    }

    /// <summary>
    /// #366 review (TOCTOU in the #265 item 1 guard): a replacement can land in the registry in
    /// the window between the probe and the inactive write. A probe that flips true→false across
    /// those two calls models exactly that; the finally must REPAIR the row back to active — the
    /// final write must be active, not the stale inactive.
    /// </summary>
    [Fact]
    public async Task ReplacementLandingDuringStatusWrite_IsRepairedToActive()
    {
        // Non-silent teardown so the write path runs: peer NOTIFICATION.
        var conn = new ScriptedConnection();
        var store = new RecordingPeerStore();
        var config = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };
        var session = new BgpSession(
            conn, new PeerConfig { Address = "127.0.0.1" }, config, new RouteTable(),
            AllowAllFilter.Instance, new BgpMetrics(), NullLogger<BgpSession>.Instance, peerStore: store);
        var calls = 0;
        session.StillRegisteredProbe = _ => Interlocked.Increment(ref calls) switch { 1 => true, _ => false };
        var run = session.RunAsync();
        conn.EnqueueMessage(new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = 0,
            RouterId = 0x0A000002,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)],
        });
        conn.EnqueueMessage(BgpKeepaliveMessage.Instance);
        for (var i = 0; i < 200 && !session.IsEstablished; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        Assert.True(session.IsEstablished, "session must reach Established");

        conn.EnqueueMessage(new BgpNotificationMessage { ErrorCode = BgpConstants.Error.Cease, SubErrorCode = 0 });
        try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* teardown unwinds */ }

        Assert.NotEmpty(store.StatusWrites);
        Assert.True(store.StatusWrites[^1].Active, "the replacement-during-write race must end with the row ACTIVE");
        Assert.Contains(store.StatusWrites, w => !w.Active);   // the stale inactive write happened, then the repair
    }

    [Fact]
    public async Task GenuineTeardown_StillWritesInactive()
    {
        // Registered + non-silent teardown: the write must still happen (no regression of #325).
        var (session, run, store) = await EstablishWithStoreAsync(new RouteTable());

        // A peer NOTIFICATION tears down with RemoteNotification — not SilentClose, probe true.
        var connField = typeof(BgpSession).GetField("_connection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var conn = (ScriptedConnection)connField.GetValue(session)!;
        conn.EnqueueMessage(new BgpNotificationMessage { ErrorCode = BgpConstants.Error.Cease, SubErrorCode = 0 });
        try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* teardown unwinds */ }

        Assert.Contains(store.StatusWrites, w => !w.Active);
    }

    /// <summary>
    /// #304: exceeding Bgp.MaxPrefixesPerPeer tears the session down with NOTIFICATION
    /// (Cease, MaxPrefixesExceeded) per RFC 4271 §6.7 / RFC 4486 §2, and the finally flushes
    /// the peer's owned routes (RFC 4271 §8.2.2) — the table does not keep the attacker's rows.
    /// </summary>
    /// <summary>
    /// #377 review: a session's per-peer prefix set must stay aligned with route-table OWNERSHIP —
    /// when session B takes over a key session A installed, A stops counting it; otherwise A's
    /// cap count drifts upward on overlaps and trips a reset for prefixes it no longer owns.
    /// </summary>
    private static async Task<(BgpSession Session, Task Run, ScriptedConnection Conn)> EstablishCappedAsync(RouteTable routeTable, int cap, uint routerId)
    {
        var conn = new ScriptedConnection();
        var config = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0, MaxPrefixesPerPeer = cap };
        var session = new BgpSession(
            conn, new PeerConfig { Address = "127.0.0.1" }, config, routeTable,
            AllowAllFilter.Instance, new BgpMetrics(), NullLogger<BgpSession>.Instance);
        var run = session.RunAsync();
        conn.EnqueueMessage(new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = 0,
            RouterId = routerId,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)],
        });
        conn.EnqueueMessage(BgpKeepaliveMessage.Instance);
        for (var i = 0; i < 200 && !session.IsEstablished; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        Assert.True(session.IsEstablished, "session must reach Established");
        return (session, run, conn);
    }

    [Fact]
    public async Task TakenOverPrefix_FreesTheOriginalOwnersCapBudget()
    {
        var routeTable = new RouteTable();
        var (a, aRun, aConn) = await EstablishCappedAsync(routeTable, cap: 1, routerId: 0x0A000002);
        var (b, bRun, bConn) = await EstablishCappedAsync(routeTable, cap: 0, routerId: 0x0A000003);

        // A installs 10/8 (its single budget slot).
        aConn.EnqueueFrame(AnnounceFrame(8, 0x0A));
        await SettleAsync(aConn, () => routeTable.Count == 1);

        // B takes the SAME prefix over — A no longer owns it. #377 review: await the
        // ownership-loss callback deterministically instead of a fixed delay.
        var aLostOwnership = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLost(object owner, (uint Prefix, byte Length) key)
        {
            if (ReferenceEquals(owner, a) && key.Prefix == TenSlashEight)
                aLostOwnership.TrySetResult();
        }
        routeTable.EntryOwnershipLost += OnLost;
        try
        {
            bConn.EnqueueFrame(AnnounceFrame(8, 0x0A));
            await aLostOwnership.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            routeTable.EntryOwnershipLost -= OnLost;
        }

        // A announces a DIFFERENT prefix with cap=1 — pre-fix this tripped the cap (A still
        // counted the taken-over 10/8) and reset A; post-fix A owns only the new prefix.
        aConn.EnqueueFrame(AnnounceFrame(24, 0xC0, 0x00, 0x02));
        await SettleAsync(aConn, () => routeTable.Count == 2);

        Assert.True(a.IsEstablished, "the taken-over prefix must not count against A's cap");   // RED pre-fix
        Assert.Equal(2, routeTable.Count);

        await TeardownAsync(a, aRun);
        await TeardownAsync(b, bRun);
    }

    private static async Task<(BgpSession Session, Task Run, ScriptedConnection Conn, RouteTable Table)> EstablishCappedAsync(int cap)
    {
        var routeTable = new RouteTable();
        var conn = new ScriptedConnection();
        var config = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0, MaxPrefixesPerPeer = cap };
        var session = new BgpSession(
            conn, new PeerConfig { Address = "127.0.0.1" }, config, routeTable,
            AllowAllFilter.Instance, new BgpMetrics(), NullLogger<BgpSession>.Instance);
        var run = session.RunAsync();
        conn.EnqueueMessage(new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = 0,
            RouterId = 0x0A000002,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)],
        });
        conn.EnqueueMessage(BgpKeepaliveMessage.Instance);
        for (var i = 0; i < 200 && !session.IsEstablished; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        Assert.True(session.IsEstablished, "session must reach Established");
        return (session, run, conn, routeTable);
    }

    [Fact]
    public async Task ExceedingMaxPrefixes_SendsCeaseMaxPrefixes_AndFlushesOwned()
    {
        var (session, run, conn, routeTable) = await EstablishCappedAsync(cap: 2);

        conn.EnqueueFrame(AnnounceFrame(8, 0x0A));          // 1/2
        conn.EnqueueFrame(AnnounceFrame(24, 0xC0, 0x00, 0x02)); // 2/2 — at the limit, still up
        await SettleAsync(conn, () => routeTable.Count == 2);
        Assert.True(session.IsEstablished, "at exactly the limit the session stays up");

        conn.EnqueueFrame(AnnounceFrame(16, 0xC0, 0x01));   // 3rd distinct prefix — over
        var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(run, completed);                        // RED pre-fix: session keeps running
        Assert.False(session.IsEstablished);

        var notif = conn.Sent
            .Select(b => BgpMessageReader.ReadMessage(b.AsSpan()))
            .OfType<BgpNotificationMessage>()
            .SingleOrDefault(n => n.ErrorCode == BgpConstants.Error.Cease);
        Assert.NotNull(notif);
        Assert.Equal(BgpConstants.SubError.CeaseMaxPrefixes, notif.SubErrorCode);
        Assert.Equal(0, routeTable.Count);                  // owned routes flushed by the finally
    }

    [Fact]
    public async Task AtLimitWithdrawal_FreesBudget()
    {
        var (session, run, conn, routeTable) = await EstablishCappedAsync(cap: 1);
        conn.EnqueueFrame(AnnounceFrame(8, 0x0A));
        await SettleAsync(conn, () => routeTable.Count == 1);

        // Withdraw it — the budget frees — then a DIFFERENT prefix installs without a reset.
        conn.EnqueueFrame(WithdrawFrame([8, 0x0A]));
        await SettleAsync(conn, () => routeTable.Count == 0);
        conn.EnqueueFrame(AnnounceFrame(24, 0xC0, 0x00, 0x02));
        await SettleAsync(conn, () => routeTable.Count == 1);
        Assert.True(session.IsEstablished, "withdrawals free prefix budget");

        await TeardownAsync(session, run);
    }

    [Fact]
    public async Task ZeroCap_IsUnlimited()
    {
        var (session, run, conn, routeTable) = await EstablishCappedAsync(cap: 0);
        for (var i = 0; i < 5; i++)
            conn.EnqueueFrame(AnnounceFrame(24, 0xC0, 0x00, (byte)(10 + i)));
        await SettleAsync(conn, () => routeTable.Count == 5);
        Assert.True(session.IsEstablished, "cap 0 = unlimited");

        await TeardownAsync(session, run);
    }

    private sealed class RejectAllIncomingFilter : IRouteFilter
    {
        private static readonly IReadOnlySet<uint> Empty = new HashSet<uint>();
        public bool AcceptIncoming(Route route, PeerConfig peer) => false;
        public IReadOnlySet<uint> ResolveOutgoingAllowSet(PeerConfig peer) => Empty;
        public bool AcceptOutgoing(Route route, PeerConfig peer, IReadOnlySet<uint> allowSet) => true;
    }
}
