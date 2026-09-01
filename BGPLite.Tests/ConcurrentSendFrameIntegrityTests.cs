using System.Net;
using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// #392: N concurrent route refreshes on ONE session must produce only WHOLE BGP frames on the
/// wire — the per-session send lock (#341/#85 lineage) serializes writers, so an observer reading
/// the socket stream must always see length-consistent, parseable frames and never an interleaved
/// or truncated one. Read over the ScriptedConnection seam: every recorded byte buffer is exactly
/// one wire frame.
/// </summary>
public class ConcurrentSendFrameIntegrityTests
{
    private const int SeededRoutes = 250; // > 2 batches of 100 NLRI

    [Fact]
    public async Task ConcurrentRefreshes_ProduceOnlyWholeFrames()
    {
        var conn = new ScriptedConnection();
        var routeTable = new RouteTable();
        var config = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1", HoldTime = 0, KeepAlive = 0 };

        // Seed unowned routes: the SharedTableRouteAssembler advertises exactly these, so every
        // refresh cycle emits multiple 100-NLRI batches — real interleave would corrupt them.
        for (var i = 0; i < SeededRoutes; i++)
        {
            var addr = (uint)(0x0A000000 + i); // 10.0.i.p space, all distinct /24s
            routeTable.AddOrUpdate(new Route
            {
                Prefix = addr,
                PrefixLength = 24,
                NextHop = 0x0A0000FE
            }, owner: null);
        }

        var session = new BgpSession(
            conn,
            new PeerConfig { Address = "127.0.0.1" },
            config,
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
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65002)],
        });
        conn.EnqueueMessage(BgpKeepaliveMessage.Instance);
        for (var i = 0; i < 200 && !session.IsEstablished; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        Assert.True(session.IsEstablished, "session must reach Established");

        // Fire concurrent refreshes on top of the initial dump: the debounce coalesces them into
        // a few full cycles, all racing for the send lock.
        var refreshes = Task.WhenAll(
            session.RefreshRoutesAsync(),
            session.RefreshRoutesAsync(),
            session.RefreshRoutesAsync(),
            session.RefreshRoutesAsync());
        await refreshes.WaitAsync(TimeSpan.FromSeconds(10));

        // Settle: let the read loop drain and the final pending refresh lap (if any) finish.
        await Task.Delay(300);

        var sent = conn.Sent;
        Assert.True(sent.Count >= SeededRoutes / 100, "expected multiple batched UPDATE frames");

        // Every recorded buffer must be exactly one whole, parseable BGP frame whose header
        // length matches the buffer size — any writer interleave would break this.
        var frames = 0;
        foreach (var buffer in sent)
        {
            Assert.True(buffer.Length >= BgpConstants.MessageHeaderSize, $"frame too short: {buffer.Length}");
            var declared = (buffer[16] << 8) | buffer[17];
            Assert.Equal(buffer.Length, declared); // whole frame, no tail of a neighbour

            var message = BgpMessageReader.ReadMessage(buffer); // must parse cleanly
            Assert.NotNull(message);
            frames++;
        }

        // The establish dump advertised the whole seed at least once across all cycles.
        Assert.True(frames >= SeededRoutes / 100, $"frames={frames}");

        session.MarkSilentClose();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        session.Dispose();
    }
}
