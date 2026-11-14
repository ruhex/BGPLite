using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BGPLite.Protocol;

namespace BGPLite.Tests.Integration;

/// <summary>One announced or withdrawn prefix as observed on the wire.</summary>
public sealed record WireRoute(
    string Prefix, IReadOnlyList<string> Communities, bool HasAsPath, bool HasNextHop, bool HasAs4Path = false);

/// <summary>One decoded UPDATE message: announcements, withdrawals, and the end-of-RIB flag.</summary>
public sealed record WireUpdate(IReadOnlyList<WireRoute> Announced, IReadOnlyList<string> Withdrawn, bool EndOfRib);

/// <summary>Which capabilities the client advertises in OPEN (defaults mirror a modern 4-byte router).</summary>
public sealed record BgpClientOptions(bool FourOctetAsn = true, bool RouteRefresh = true, bool MpV6Unicast = false);

/// <summary>
/// Minimal real-BGP client for the E2E suite: opens a TCP session against the loopback listener,
/// performs the RFC 4271 OPEN/KEEPALIVE handshake, decodes UPDATEs (classic NLRI, MP_REACH v4/v6,
/// withdrawn routes, COMMUNITY, AS_PATH, AS4_PATH, NEXT_HOP), sends ROUTE-REFRESH (RFC 2918),
/// announces routes toward the server, and captures NOTIFICATIONs. Server hold time is negotiated
/// to 0 in the fixture config, so no keepalive traffic is needed.
/// </summary>
public sealed class BgpTestClient : IDisposable
{
    public const ushort AsTrans = (ushort)BgpConstants.AsPath.AsTrans;

    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

    public uint EffectiveAsn { get; }
    public uint RouterId { get; }
    public ushort HoldTime { get; }
    /// <summary>Value placed in the OPEN My AS field: the ASN itself when 2-byte, AS_TRANS (RFC 6793 §4.1) otherwise.</summary>
    public ushort OpenAsn { get; }

    public BgpOpenMessage? ServerOpen { get; private set; }
    public bool Established { get; private set; }
    public bool SocketClosed { get; private set; }

    public List<WireUpdate> Updates { get; } = [];
    public List<(byte Code, byte SubCode)> Notifications { get; } = [];

    public BgpTestClient(uint asn, uint routerId, ushort holdTime = 0, BgpClientOptions? options = null)
    {
        EffectiveAsn = asn;
        RouterId = routerId;
        HoldTime = holdTime;
        Options = options ?? new BgpClientOptions();
        if (asn > ushort.MaxValue && !Options.FourOctetAsn)
            throw new ArgumentException($"ASN {asn} does not fit the 2-octet OPEN field; advertise the 4-octet capability.");
        OpenAsn = asn <= ushort.MaxValue ? (ushort)asn : AsTrans;
    }

    private BgpClientOptions Options { get; }

    /// <summary>Connects TCP only — for OPEN-rejection scenarios the test sends a crafted OPEN itself.</summary>
    public async Task ConnectAsync(int port, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        await _socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cts.Token);
    }

    /// <summary>Connects, sends OPEN, completes the OPEN/KEEPALIVE handshake, and marks Established.</summary>
    public async Task<BgpOpenMessage> ConnectAndEstablishAsync(int port, TimeSpan? timeout = null)
    {
        await ConnectAsync(port, timeout);
        using var cts = new CancellationTokenSource((timeout ?? TimeSpan.FromSeconds(10)) * 2);
        var ct = cts.Token;

        var open = new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = OpenAsn,
            HoldTime = HoldTime,
            RouterId = RouterId,
            Capabilities = BuildCapabilities(),
        };
        Send(open);

        var received = await ReadMessageAsync(ct);
        var serverOpen = received as BgpOpenMessage
            ?? throw new InvalidOperationException($"Expected the server OPEN, got {Describe(received)}.");
        ServerOpen = serverOpen;

        var keepalive = await ReadMessageAsync(ct);
        if (keepalive is not BgpKeepaliveMessage)
            throw new InvalidOperationException($"Expected the server KEEPALIVE, got {Describe(keepalive)}.");
        Established = true;

        Send(BgpKeepaliveMessage.Instance);
        return serverOpen;
    }

    /// <summary>
    /// Reads UPDATEs until the End-of-RIB marker (RFC 4724 §4.1 — the empty UPDATE that follows
    /// the initial dump) or until no message arrives within <paramref name="quietWindow"/>.
    /// </summary>
    public async Task<List<WireUpdate>> CollectDumpAsync(TimeSpan? quietWindow = null, TimeSpan? maxTotal = null)
    {
        var quiet = quietWindow ?? TimeSpan.FromSeconds(3);
        var deadline = DateTime.UtcNow + (maxTotal ?? TimeSpan.FromSeconds(15));
        var collected = new List<WireUpdate>();
        while (DateTime.UtcNow < deadline)
        {
            var update = await NextUpdateAsync(quiet);
            if (update is null)
                break;
            collected.Add(update);
            if (update.EndOfRib)
                break;
        }
        return collected;
    }

    /// <summary>Waits until an UPDATE announces <paramref name="prefix"/>; returns all updates seen meanwhile.</summary>
    public async Task<List<WireUpdate>> WaitForAnnouncedPrefixAsync(string prefix, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        var seen = new List<WireUpdate>();
        while (DateTime.UtcNow < deadline)
        {
            var update = await NextUpdateAsync(TimeSpan.FromSeconds(2));
            if (update is null)
                continue;
            seen.Add(update);
            if (update.Announced.Any(r => r.Prefix == prefix))
                return seen;
        }
        throw new TimeoutException($"No UPDATE announcing {prefix} within the budget; notifications: {FormatNotifications()}.");
    }

    /// <summary>Waits until an UPDATE withdraws <paramref name="prefix"/>; returns all updates seen meanwhile.</summary>
    public async Task<List<WireUpdate>> WaitForWithdrawnAsync(string prefix, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        var seen = new List<WireUpdate>();
        while (DateTime.UtcNow < deadline)
        {
            var update = await NextUpdateAsync(TimeSpan.FromSeconds(2));
            if (update is null)
                continue;
            seen.Add(update);
            if (update.Withdrawn.Contains(prefix))
                return seen;
        }
        throw new TimeoutException($"No UPDATE withdrawing {prefix} within the budget; notifications: {FormatNotifications()}.");
    }

    /// <summary>Waits for a NOTIFICATION and returns (code, subcode).</summary>
    public async Task<(byte Code, byte SubCode)> WaitForNotificationAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            var message = await ReadMessageAsync(TimeSpan.FromSeconds(2));
            if (message is BgpNotificationMessage n)
                return (n.ErrorCode, n.SubErrorCode);
            if (message is null)
                break;
        }
        throw new TimeoutException($"No NOTIFICATION within the budget; notifications so far: {FormatNotifications()}.");
    }

    /// <summary>Sends ROUTE-REFRESH (RFC 2918) for IPv4/Unicast.</summary>
    public void SendRouteRefresh()
    {
        Send(new BgpRouteRefreshMessage { Afi = 1, Safi = 1 });
    }

    /// <summary>
    /// Announces IPv4 prefixes TO the server with the mandatory well-known attributes
    /// (ORIGIN, AS_PATH, NEXT_HOP) — the inbound path the per-peer MaxPrefix ceiling limits.
    /// </summary>
    public void SendUpdate(IEnumerable<string> announced)
    {
        var nlri = announced.Select(cidr =>
        {
            if (!PrefixCidr.TryParse(cidr, out var prefix))
                throw new FormatException($"Invalid test NLRI: {cidr}");
            return prefix;
        }).ToList();
        var attributes = UpdateCodec.BuildUpdateAttributes(EffectiveAsn, localFourByteAsn: true, nextHop: 0x0A000001, []);
        Send(new BgpUpdateMessage { Nlri = nlri, PathAttributes = attributes });
    }

    /// <summary>Reads until the socket closes; false on timeout.</summary>
    public async Task<bool> WaitForClosedAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!SocketClosed && DateTime.UtcNow < deadline)
            await ReadMessageAsync(TimeSpan.FromMilliseconds(200));
        return SocketClosed;
    }

    /// <summary>Reads pending UPDATEs, tolerating interleaved KEEPALIVEs; null on read timeout.</summary>
    private async Task<WireUpdate?> NextUpdateAsync(TimeSpan quietWindow)
    {
        while (true)
        {
            var message = await ReadMessageAsync(quietWindow);
            if (message is null)
                return null;
            if (message is BgpUpdateMessage update)
            {
                var decoded = Decode(update);
                Updates.Add(decoded);
                return decoded;
            }
            // OPEN/NOTIFICATION during collection are surfaced via properties; keepalives ignored.
            if (message is BgpNotificationMessage n)
                Notifications.Add((n.ErrorCode, n.SubErrorCode));
            if (message is BgpOpenMessage o)
                ServerOpen = o;
        }
    }

    private static WireUpdate Decode(BgpUpdateMessage update)
    {
        var communities = update.PathAttributes
            .Where(a => a.TypeCode == BgpConstants.Attribute.Community)
            .SelectMany(a => DecodeCommunities(a.Data))
            .Select(CommunityCodec.Format)
            .ToList();
        var hasAsPath = update.PathAttributes.Any(a => a.TypeCode == BgpConstants.Attribute.AsPath);
        var hasAs4Path = update.PathAttributes.Any(a => a.TypeCode == BgpConstants.Attribute.As4Path);
        var hasNextHop = update.PathAttributes.Any(a => a.TypeCode == BgpConstants.Attribute.NextHop);
        var announced = update.Nlri
            .Select(p => new WireRoute(p.ToString(), communities, hasAsPath, hasNextHop, hasAs4Path))
            .ToList();
        // MP_REACH/MP_UNREACH NLRI ride the same WireRoute shape, family-tagged by the prefix text.
        if (update.MpReachV6 is { } v6)
            announced.AddRange(v6.Prefixes.Select(p => new WireRoute(p.ToString(), communities, hasAsPath, hasNextHop, hasAs4Path)));
        if (update.MpReachV4 is { } v4)
            announced.AddRange(v4.Prefixes.Select(p => new WireRoute(p.ToString(), communities, hasAsPath, hasNextHop, hasAs4Path)));
        var withdrawn = update.WithdrawnRoutes.Select(p => p.ToString()).ToList();
        if (update.MpUnreachV6 is not null)
            withdrawn.AddRange(update.MpUnreachV6.Select(p => p.ToString()));
        if (update.MpUnreachV4 is not null)
            withdrawn.AddRange(update.MpUnreachV4.Select(p => p.ToString()));
        var endOfRib = update.WithdrawnRoutes.Count == 0
            && announced.Count == 0
            && update.PathAttributes.Count == 0
            && update.MpReachV4 is null
            && update.MpUnreachV4 is null
            && update.MpReachV6 is null
            && update.MpUnreachV6 is null;
        return new WireUpdate(announced, withdrawn, endOfRib);
    }

    private List<BgpCapabilityInfo> BuildCapabilities()
    {
        var capabilities = new List<BgpCapabilityInfo>();
        if (Options.FourOctetAsn)
            capabilities.Add(BgpCapabilityInfo.FourOctetAsn(EffectiveAsn));
        if (Options.RouteRefresh)
            capabilities.Add(BgpCapabilityInfo.RouteRefresh());
        if (Options.MpV6Unicast)
            capabilities.Add(BgpCapabilityInfo.MultiprotocolIpv6Unicast());
        return capabilities;
    }

    private static IEnumerable<uint> DecodeCommunities(byte[] data)
    {
        for (var offset = 0; offset + 4 <= data.Length; offset += 4)
            yield return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    }

    private string FormatNotifications() =>
        Notifications.Count == 0 ? "none" : string.Join(", ", Notifications.Select(n => $"({n.Code},{n.SubCode})"));

    private static string Describe(BgpMessage? message) => message?.Type.ToString() ?? "nothing";

    /// <summary>Encodes and sends one BGP message (public: rejection tests craft their own OPEN).</summary>
    public void SendMessage(BgpMessage message)
    {
        var buffer = new byte[BgpMessageWriter.GetBufferSize(message)];
        var length = BgpMessageWriter.WriteMessage(message, buffer);
        _socket.Send(buffer, 0, length, SocketFlags.None);
    }

    private void Send(BgpMessage message) => SendMessage(message);

    /// <summary>Reads one full BGP message; null when the quiet window elapsed or the socket closed.</summary>
    private async Task<BgpMessage?> ReadMessageAsync(TimeSpan wait, CancellationToken? outer = null)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer ?? CancellationToken.None);
            cts.CancelAfter(wait);

            var header = new byte[BgpConstants.MessageHeaderSize];
            if (!await TryReadExactAsync(header, cts.Token))
            {
                SocketClosed = true;
                return null;
            }
            var length = BgpMessageReader.GetMessageLength(header);
            var full = new byte[length];
            header.CopyTo(full, 0);
            if (!await TryReadExactAsync(full.AsMemory(BgpConstants.MessageHeaderSize), cts.Token))
            {
                SocketClosed = true;
                return null;
            }
            var message = BgpMessageReader.ReadMessage(full);
            return message;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException)
        {
            SocketClosed = true;
            return null;
        }
        catch (ObjectDisposedException)
        {
            SocketClosed = true;
            return null;
        }
    }

    private async Task<BgpMessage?> ReadMessageAsync(CancellationToken ct) =>
        await ReadMessageAsync(TimeSpan.FromSeconds(10), ct);

    private async Task<bool> TryReadExactAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await _socket.ReceiveAsync(buffer[total..], ct);
            if (read == 0)
                return false;
            total += read;
        }
        return true;
    }

    public void Dispose()
    {
        Established = false;
        _socket.Dispose();
    }
}
