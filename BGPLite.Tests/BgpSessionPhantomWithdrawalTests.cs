using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// #430: the send mirror (<c>_advertisedPrefixes</c>) recorded a batch BEFORE its UPDATE was
/// serialized. A batch whose composed UPDATE exceeded the 4096-byte maximum (constructible from
/// a peer-supplied route carrying ~1200 communities) threw out of the writer AFTER the mirror
/// already claimed the routes — so the NEXT refresh sent WITHDRAWALS for prefixes that were never
/// announced. Build-then-commit keeps the mirror equal to what is actually on the wire.
/// </summary>
public sealed class BgpSessionPhantomWithdrawalTests
{
    private static Route PoisonRoute() => new()
    {
        Prefix = 0x0A010000,        // 10.1.0.0/16
        PrefixLength = 16,
        NextHop = 1,
        // 1200 communities → a COMMUNITY attribute of ~4800 bytes → the composed UPDATE (NLRI +
        // ORIGIN + AS_PATH + NEXT_HOP + COMMUNITY) exceeds MaxMessageSize and the writer refuses it.
        Communities = Enumerable.Range(0, 1200).Select(i => (uint)i).ToArray()
    };

    private static async Task<(BgpSession Session, Task Run, ScriptedConnection Conn)> EstablishAsync(RouteTable routeTable)
    {
        var conn = new ScriptedConnection();
        var session = new BgpSession(
            conn,
            new PeerConfig { Address = "127.0.0.1" },
            new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 },
            routeTable,
            AllowAllFilter.Instance,
            new BgpMetrics(),
            NullLogger<BgpSession>.Instance);

        var run = session.RunAsync();
        conn.EnqueueMessage(new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = 65002,
            HoldTime = 0,
            RouterId = 0x0A000002,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)]
        });
        conn.EnqueueMessage(BgpKeepaliveMessage.Instance);

        for (var i = 0; i < 200 && !session.IsEstablished; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        Assert.True(session.IsEstablished, "session must reach Established");
        return (session, run, conn);
    }

    private static int CountUpdates(ScriptedConnection conn) =>
        conn.Sent.Select(f => BgpMessageReader.ReadMessage(f)).OfType<BgpUpdateMessage>().Count();

    [Fact]
    public async Task FailedOversizeSend_DoesNotLeavePhantomMirrorEntries()
    {
        var routeTable = new RouteTable();
        routeTable.AddOrUpdate(new Route { Prefix = 0x0A000000, PrefixLength = 8, NextHop = 1 }); // healthy seed
        var (session, run, conn) = await EstablishAsync(routeTable);

        // Healthy refresh #1: the /8 is advertised normally.
        await session.RefreshRoutesAsync();

        // Inject the poison route and refresh: the composed UPDATE exceeds MaxMessageSize and is
        // dropped before the wire (#457 pre-validation; pre-#457 the writer threw mid-send and
        // RefreshCycleAsync contained the failure — same observable outcome, session stays up).
        routeTable.AddOrUpdate(PoisonRoute());
        await session.RefreshRoutesAsync();

        var updatesBefore = CountUpdates(conn);

        // Healthy refresh #2: WithdrawAllAsync must withdraw ONLY what is actually on the wire —
        // pre-#430 the mirror also held the POISON route (recorded before the failed send), so
        // this refresh put a PHANTOM withdrawal for 10.1.0.0/16 on the wire. The healthy /8's
        // withdraw+re-announce may legitimately appear again.
        await session.RefreshRoutesAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.True(session.IsEstablished, "the oversize send is contained — the session stays up");
        var updatesAfter = conn.Sent
            .Skip(updatesBefore)
            .Select(f => BgpMessageReader.ReadMessage(f))
            .OfType<BgpUpdateMessage>()
            .ToList();
        Assert.DoesNotContain(updatesAfter,
            u => u.WithdrawnRoutes.Any(p => p.Address == 0x0A010000 && p.Length == 16));

        session.Dispose();
        try { await run.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
    }

    [Fact]
    public async Task HealthySend_MirrorMatchesTheWire()
    {
        // Control: on a normal send the mirror (observable via a later withdrawal) still matches
        // the wire — the reorder must not break the withdraw-what-we-advertised contract.
        var routeTable = new RouteTable();
        routeTable.AddOrUpdate(new Route { Prefix = 0x0A000000, PrefixLength = 8, NextHop = 1 });
        var (session, run, conn) = await EstablishAsync(routeTable);

        await session.RefreshRoutesAsync();                       // advertises the /8
        routeTable.Remove(0x0A000000, 8);                         // source disappears
        await session.RefreshRoutesAsync();                       // withdraws the /8

        var updates = conn.Sent.Select(f => BgpMessageReader.ReadMessage(f)).OfType<BgpUpdateMessage>().ToList();
        Assert.Contains(updates, u => u.Nlri.Any(p => p.Address == 0x0A000000 && p.Length == 8));
        var withdrawal = updates.LastOrDefault(u => u.WithdrawnRoutes.Any(p => p.Address == 0x0A000000));
        Assert.NotNull(withdrawal);

        session.Dispose();
        try { await run.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
    }

    [Fact]
    public async Task OversizeGroupAtInitialSend_SessionStaysUp_HealthyRoutesAdvertised()
    {
        // #457: the poison route present BEFORE establishment exercises the INITIAL-send path.
        // Pre-fix the composed overflow threw ArgumentOutOfRangeException out of the initial dump
        // into RunAsync's generic catch: best-effort Cease + teardown — the peer reconnected into
        // the same state with zero routes, every time. Post-fix the unsplittable group (attributes
        // are constant per community set; NLRI cannot overflow at the 100-per-UPDATE cap) is
        // dropped loudly: the session establishes, the healthy seed reaches the wire, no
        // NOTIFICATION is emitted, and the poison prefix never enters the mirror.
        var routeTable = new RouteTable();
        routeTable.AddOrUpdate(new Route { Prefix = 0x0A000000, PrefixLength = 8, NextHop = 1 }); // healthy seed
        routeTable.AddOrUpdate(PoisonRoute());
        var (session, run, conn) = await EstablishAsync(routeTable);
        await Task.Delay(TimeSpan.FromMilliseconds(100)); // let the initial dump finish

        Assert.True(session.IsEstablished, "the oversize group must not tear down the initial send");
        var frames = conn.Sent.Select(f => BgpMessageReader.ReadMessage(f)).ToList();
        Assert.DoesNotContain(frames, m => m is BgpNotificationMessage);
        var updates = frames.OfType<BgpUpdateMessage>().ToList();
        Assert.Contains(updates, u => u.Nlri.Any(p => p.Address == 0x0A000000 && p.Length == 8));
        Assert.DoesNotContain(updates, u => u.Nlri.Any(p => p.Address == 0x0A010000 && p.Length == 16));
        Assert.DoesNotContain(updates, u => u.WithdrawnRoutes.Count > 0);

        // Mirror consistency on top of the dropped group: a later refresh withdraws ONLY the
        // healthy /8 (the poison prefix was never advertised, so it must never be withdrawn).
        routeTable.Remove(0x0A000000, 8);
        await session.RefreshRoutesAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.DoesNotContain(conn.Sent.Select(f => BgpMessageReader.ReadMessage(f)).OfType<BgpUpdateMessage>(),
            u => u.WithdrawnRoutes.Any(p => p.Address == 0x0A010000 && p.Length == 16));

        session.Dispose();
        try { await run.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
    }
}
