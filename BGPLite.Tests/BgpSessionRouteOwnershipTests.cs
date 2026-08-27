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
        for (var i = 0; i < 200; i++)
        {
            if (conn.Drained && (until?.Invoke() ?? false)) return;
            if (conn.Drained && until is null && i > 5) return;
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
        Assert.True(until is null, "the expected route-table state was not reached in time");
    }

    private static async Task TeardownAsync(BgpSession session, Task run)
    {
        session.MarkSilentClose();
        try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { /* expected */ }
        catch (IOException) { /* expected */ }
        session.Dispose();
    }

    /// <summary>Scripts inbound frames and discards outbound bytes; reads block until a frame arrives.</summary>
    private sealed class ScriptedConnection : IBgpConnection
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private readonly Queue<byte> _readBuffer = new();
        private int _pending;

        /// <summary>True once every enqueued frame has been fully handed to the read loop.</summary>
        public bool Drained => Volatile.Read(ref _pending) == 0 && _readBuffer.Count == 0;

        public void EnqueueFrame(byte[] frame)
        {
            Interlocked.Increment(ref _pending);
            _inbound.Writer.TryWrite(frame);
        }

        public void EnqueueMessage(BgpMessage message)
        {
            var buf = new byte[BgpMessageWriter.GetBufferSize(message)];
            var n = BgpMessageWriter.WriteMessage(message, buf);
            EnqueueFrame(buf[..n]);
        }

        public async ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                while (_readBuffer.Count > 0 && offset < buffer.Length)
                    buffer.Span[offset++] = _readBuffer.Dequeue();
                if (offset >= buffer.Length) break;

                byte[] chunk;
                try { chunk = await _inbound.Reader.ReadAsync(cancellationToken); }
                catch (ChannelClosedException) { throw new IOException("Connection closed by peer"); }

                var toCopy = Math.Min(chunk.Length, buffer.Length - offset);
                for (var i = 0; i < toCopy; i++) buffer.Span[offset++] = chunk[i];
                for (var i = toCopy; i < chunk.Length; i++) _readBuffer.Enqueue(chunk[i]);
                Interlocked.Decrement(ref _pending);
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) => default;

        public bool IsPeerClosed => false;

        public void Dispose() => _inbound.Writer.TryComplete();
    }

    /// <summary>Rejects every inbound route, so nothing this peer announces reaches the table.</summary>
    private sealed class RejectAllIncomingFilter : IRouteFilter
    {
        private static readonly IReadOnlySet<uint> Empty = new HashSet<uint>();
        public bool AcceptIncoming(Route route, PeerConfig peer) => false;
        public IReadOnlySet<uint> ResolveOutgoingAllowSet(PeerConfig peer) => Empty;
        public bool AcceptOutgoing(Route route, PeerConfig peer, IReadOnlySet<uint> allowSet) => true;
    }
}
