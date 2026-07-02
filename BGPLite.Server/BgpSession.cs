using System.Buffers;
using System.Net;
using System.Net.Sockets;
using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Routing;
using Microsoft.Extensions.Logging;

namespace BGPLite.Server;

public sealed class BgpSession : IDisposable
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly PeerConfig _peerConfig;
    // "ip:port" label for session logs so the several peers that may share one source IP (behind a
    // NAT/VPN) can be told apart (issue #18). Peer-store lookups use _peerConfig.Address (IP only);
    // this label is for human-facing log lines only.
    private readonly string _peer;
    private readonly BgpConfig _bgpConfig;
    private readonly RouteTable _routeTable;
    private readonly IRouteFilter _routeFilter;
    private readonly BgpMetrics _metrics;
    private readonly ILogger<BgpSession> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string, uint>? _onPeerIdentified;
    private readonly IPeerStore? _peerStore;
    private readonly IPrefixService? _prefixService;
    private readonly AppConfig? _appConfig;
    private readonly IPrefixAggregator _prefixAggregator;

    // volatile: read by external threads (BgpServer.RefreshPeerAsync/StopAsync). Guarantees
    // acquire/release so IsEstablished reflects the most recent TransitionTo without JIT caching.
    private volatile BgpFsmState _state = BgpFsmState.Idle;
    // Split teardown reasons (RFC 4271 §8.1 mandates exactly one NOTIFICATION per teardown).
    // The finally-block only emits a best-effort Cease when the reason is still None (i.e. an
    // unexpected close from Established). All other reasons already produced — or deliberately
    // suppressed — a NOTIFICATION, so replying with Cease would be a protocol violation:
    //   - LocalCease:        we sent Cease (catch blocks, NotifyCeaseAsync) → no reply
    //   - RemoteNotification: peer sent NOTIFICATION → release resources/Idle, do NOT reply
    //   - HoldTimerExpired:  we sent Hold Timer Expired → no reply
    //   - SilentClose:       Graceful-Restart-aware shutdown / session replacement drops the TCP
    //                        connection silently so peers retain routes (RFC 4724 §4) → no reply
    // int + Interlocked.Exchange: written by RunAsync AND by external callers (BgpServer
    // StopAsync/replace path), read by the RunAsync finally-block on a different thread.
    private int _teardownReason = (int)TeardownReason.None;
    private int _disposed;
    private uint _remoteAsn;
    private bool _remoteFourByteAsn;
    private bool _localFourByteAsn; // derived from negotiated OPEN capability (RFC 6793)
    private ushort _negotiatedHoldTime;
    private List<IpPrefix> _advertisedPrefixes = [];
    private TimeSpan _keepAliveInterval;
    private long _lastReceivedTicks; // UTC ticks of last received message; drives the HoldTimer (Interlocked)

    public BgpFsmState State => _state;
    public PeerConfig Peer => _peerConfig;
    public bool IsEstablished => _state == BgpFsmState.Established;

    public async Task RefreshRoutesAsync()
    {
        if (!IsEstablished) return;

        // _sendLock is acquired inside SendMessageAsync, so each individual UPDATE is atomic on the
        // wire. _advertisedPrefixesLock serializes the (withdraw + re-announce) pair against the
        // initial-send, which mutates the same list concurrently. We do NOT hold _sendLock across
        // the whole pair: a HoldTimer expiry or peer NOTIFICATION that arrives between them would
        // otherwise deadlock waiting for the refresh to finish before it can send Cease/HoldTimerExpired.
        await _advertisedPrefixesLock.WaitAsync();
        try
        {
            _logger.LogInformation("Refreshing routes for {Peer}", _peer);
            await WithdrawAllAsync();
            await SendAllRoutesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh routes for {Peer}", _peer);
        }
        finally
        {
            _advertisedPrefixesLock.Release();
        }
    }

    private async Task WithdrawAllAsync()
    {
        var count = _advertisedPrefixes.Count;
        if (count == 0) return;

        const int maxPerUpdate = 100;
        for (var i = 0; i < count; i += maxPerUpdate)
        {
            var batch = _advertisedPrefixes.GetRange(i, Math.Min(maxPerUpdate, count - i));
            var update = new BgpUpdateMessage
            {
                WithdrawnRoutes = batch,
                PathAttributes = [],
                Nlri = []
            };
            await SendMessageAsync(update);
            _metrics.UpdateSent();
        }

        _logger.LogInformation("Withdrawn {Count} routes from {Peer}", count, _peer);
        _advertisedPrefixes.Clear();
    }

    public BgpSession(
        Socket socket,
        PeerConfig peerConfig,
        BgpConfig bgpConfig,
        RouteTable routeTable,
        IRouteFilter routeFilter,
        BgpMetrics metrics,
        ILogger<BgpSession> logger,
        Action<string, uint>? onPeerIdentified = null,
        IPeerStore? peerStore = null,
        IPrefixService? prefixService = null,
        AppConfig? appConfig = null,
        IPrefixAggregator? prefixAggregator = null)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
        _peerConfig = peerConfig;
        _peer = peerConfig.ToString();
        _bgpConfig = bgpConfig;
        _routeTable = routeTable;
        _routeFilter = routeFilter;
        _metrics = metrics;
        _logger = logger;
        _onPeerIdentified = onPeerIdentified;
        _peerStore = peerStore;
        _prefixService = prefixService;
        _appConfig = appConfig;
        _prefixAggregator = prefixAggregator ?? new ExactUnionPrefixAggregator();
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        try
        {
            TransitionTo(BgpFsmState.Connect);
            _metrics.PeerConnected();
            _logger.LogInformation("PeerConnected {Peer}", _peer);

            // Receive OPEN
            var openMessage = await ReceiveMessageAsync(linkedCts.Token);
            if (openMessage is not BgpOpenMessage remoteOpen)
            {
                await SendNotificationAsync(BgpConstants.Error.OpenMessageError, BgpConstants.SubError.Unspecific);
                return;
            }

            _logger.LogInformation("OpenReceived from {Peer} ASN={Asn} Capabilities=[{Caps}]",
                _peer, remoteOpen.Asn,
                string.Join(", ", remoteOpen.Capabilities.Select(c => c.Data.Length > 0
                    ? $"{c.Code}[{Convert.ToHexString(c.Data)}]"
                    : $"{c.Code}")));

            ValidateOpen(remoteOpen);

            TransitionTo(BgpFsmState.OpenSent);

            // Send our OPEN — adapt capabilities to peer
            await SendOpenAsync(remoteOpen);
            _logger.LogInformation("OpenSent to {Peer}", _peer);

            // Send KEEPALIVE (acknowledge OPEN)
            await SendKeepaliveAsync();
            _logger.LogDebug("KeepAliveSent to {Peer} (OPEN confirm)", _peer);

            TransitionTo(BgpFsmState.OpenConfirm);

            // Receive KEEPALIVE
            var response = await ReceiveMessageAsync(linkedCts.Token);
            _logger.LogInformation("Received {Type} from {Peer} in OpenConfirm", response.Type, _peer);

            switch (response)
            {
                case BgpKeepaliveMessage:
                    break;
                case BgpNotificationMessage notif:
                    var dataHex = notif.Data is { Length: > 0 }
                        ? Convert.ToHexString(notif.Data)
                        : "(no data)";
                    _logger.LogWarning(
                        "Peer {Peer} sent NOTIFICATION Error={Error} SubError={SubError} Data={Data}",
                        _peer, notif.ErrorCode, notif.SubErrorCode, dataHex);
                    return;
                default:
                    _logger.LogError("Unexpected message {Type} from {Peer} in OpenConfirm", response.Type, _peer);
                    await SendNotificationAsync(BgpConstants.Error.FiniteStateMachineError, BgpConstants.SubError.Unspecific);
                    return;
            }

            _logger.LogDebug("KeepAliveReceived from {Peer}", _peer);

            TransitionTo(BgpFsmState.Established);
            _metrics.SessionEstablished();
            _logger.LogInformation("SessionEstablished with {Peer} ASN={Asn}", _peer, _remoteAsn);

            // Send initial routes. _sendLock is acquired inside SendMessageAsync for byte-level
            // ordering; _advertisedPrefixesLock guards the list across the initial-send vs. a
            // RefreshRoutesAsync fired from the API the instant IsEstablished became true.
            await _advertisedPrefixesLock.WaitAsync(linkedCts.Token);
            try
            {
                await SendAllRoutesAsync();
                // End-of-RIB once the initial dump is complete (RFC 4724 §4.1): lets GR-capable
                // peers finalize stale routes. Tied to session establishment, so NOT sent on refresh.
                if (_bgpConfig.GracefulRestart)
                    await SendEndOfRibAsync();
            }
            finally { _advertisedPrefixesLock.Release(); }

            // Run main loop: read messages + send keepalives
            await RunEstablishedAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("SessionClosed (cancelled) with {Peer}", _peer);
        }
        catch (BgpNotificationException ex)
        {
            _logger.LogWarning(ex, "BGP error from {Peer}: {Error}/{SubError}", _peer, ex.ErrorCode, ex.SubErrorCode);
            // Atomically claim the teardown as LocalCease BEFORE sending. If a concurrent
            // MarkSilentClose (GR-aware shutdown / session replacement) or a peer NOTIFICATION
            // already latched a reason, the CAS fails and we send nothing — preserving the silent
            // close (RFC 4724 §4) / no-reply (RFC 4271 §6.3) and exactly-one-NOTIFICATION (§8.1).
            if (Interlocked.CompareExchange(ref _teardownReason, (int)TeardownReason.LocalCease, (int)TeardownReason.None) == (int)TeardownReason.None)
            {
                try { await SendNotificationAsync(ex.ErrorCode, ex.SubErrorCode); }
                catch { /* best-effort */ }
            }
        }
        catch (BgpParseException ex)
        {
            _logger.LogError(ex, "Parse error from {Peer}", _peer);
            if (Interlocked.CompareExchange(ref _teardownReason, (int)TeardownReason.LocalCease, (int)TeardownReason.None) == (int)TeardownReason.None)
            {
                try { await SendNotificationAsync(BgpConstants.Error.MessageHeaderError, BgpConstants.SubError.Unspecific); }
                catch { /* best-effort */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session error with {Peer}", _peer);
            // Best-effort Cease so the peer sees a clean close instead of a bare TCP RST.
            // CAS from None: if a concurrent silent close / peer NOTIFICATION already claimed the
            // teardown, do NOT emit a NOTIFICATION (RFC 4724 §4 / RFC 4271 §6.3 / §8.1).
            if (Interlocked.CompareExchange(ref _teardownReason, (int)TeardownReason.LocalCease, (int)TeardownReason.None) == (int)TeardownReason.None)
            {
                try { await SendNotificationAsync(BgpConstants.Error.Cease, BgpConstants.SubError.Unspecific); }
                catch { /* best-effort */ }
            }
        }
        finally
        {
            var wasEstablished = _state == BgpFsmState.Established;
            // RFC 4271 §8.1: graceful termination from Established MUST send Cease before close — but
            // only when no NOTIFICATION was already emitted and the close isn't a deliberate silent
            // close (GR-aware shutdown / session replacement, RFC 4724 §4) or a peer-initiated
            // NOTIFICATION (RFC 4271 §6.3: release resources/Idle, do NOT reply). The CAS both tests
            // AND atomically transitions None→LocalCease, so a concurrent MarkSilentClose that wins
            // the race suppresses this Cease (no read-then-write window as the prior CompareExchange
            // (...,0,0) + Exchange had).
            if (wasEstablished && Interlocked.CompareExchange(ref _teardownReason, (int)TeardownReason.LocalCease, (int)TeardownReason.None) == (int)TeardownReason.None)
            {
                try { await SendNotificationAsync(BgpConstants.Error.Cease, BgpConstants.SubError.Unspecific); }
                catch { /* best-effort */ }
            }
            TransitionTo(BgpFsmState.Idle);
            if (wasEstablished)
            {
                _metrics.SessionClosed();
                _peerStore?.UpdateSessionStatus(_peerConfig.Address, _remoteAsn, false);
            }
            _metrics.PeerDisconnected();
            _logger.LogInformation("SessionClosed with {Peer}", _peer);
        }
    }

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    // Guards mutations of _advertisedPrefixes so initial-send and RefreshRoutesAsync can't interleave.
    // SemaphoreSlim instead of lock{} so it composes correctly with await.
    private readonly SemaphoreSlim _advertisedPrefixesLock = new(1, 1);

    private async Task RunEstablishedAsync(CancellationToken cancellationToken)
    {
        // Hold time 0 -> KEEPALIVE timer and Hold Timer are disabled (RFC 4271 §4.2/§6.5).
        if (_negotiatedHoldTime == 0)
        {
            await ReadLoopAsync(cancellationToken);
            await _cts.CancelAsync();
            return;
        }

        Interlocked.Exchange(ref _lastReceivedTicks, DateTime.UtcNow.Ticks);

        using var keepaliveTimer = new PeriodicTimer(_keepAliveInterval);
        var readTask = ReadLoopAsync(cancellationToken);
        var keepaliveTask = HoldTimerLoopAsync(keepaliveTimer, cancellationToken);

        await Task.WhenAny(readTask, keepaliveTask);
        await _cts.CancelAsync();

        await AwaitLoopTaskAsync(readTask, "read");
        await AwaitLoopTaskAsync(keepaliveTask, "keepalive");
    }

    private async Task AwaitLoopTaskAsync(Task task, string label)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "{Label} loop faulted for {Peer}", label, _peer); }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveMessageAsync(cancellationToken);
            Interlocked.Exchange(ref _lastReceivedTicks, DateTime.UtcNow.Ticks);

            switch (message)
            {
                case BgpUpdateMessage update:
                    _metrics.UpdateReceived();
                    await HandleUpdateAsync(update);
                    break;
                case BgpKeepaliveMessage:
                    _logger.LogDebug("KeepAliveReceived from {Peer}", _peer);
                    break;
                case BgpNotificationMessage notif:
                    _logger.LogWarning("NotificationReceived from {Peer}: {Error}/{SubError}",
                        _peer, notif.ErrorCode, notif.SubErrorCode);
                    // RFC 4271 §6.3/§8.1: on receiving a NOTIFICATION, release resources, drop the
                    // TCP connection and move to Idle. Do NOT send a NOTIFICATION back. Latch the
                    // teardown reason (CAS from None — a concurrent silent close/hold-expiry wins
                    // either way, both suppress the finally-block Cease) so the RunAsync finally-block
                    // does not reply with a Cease.
                    Interlocked.CompareExchange(ref _teardownReason, (int)TeardownReason.RemoteNotification, (int)TeardownReason.None);
                    return;
            }
        }
    }

    private async Task HoldTimerLoopAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        var holdTime = TimeSpan.FromSeconds(_negotiatedHoldTime);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            // Hold timer: tear down if no message was received within the negotiated hold time
            // (RFC 4271 §6.6). Atomically claim the teardown as HoldTimerExpired BEFORE sending; if a
            // concurrent MarkSilentClose / peer NOTIFICATION already claimed it, send nothing. The
            // latch in a finally (matching the catch-block pattern) means a partial/failed write still
            // counts as the one NOTIFICATION for this teardown (RFC 4271 §8.1), so the finally-block
            // never double-emits a Cease.
            if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastReceivedTicks) >= holdTime.Ticks)
            {
                _logger.LogWarning("Hold timer expired for {Peer} (no message for {Hold}s)",
                    _peer, _negotiatedHoldTime);
                if (Interlocked.CompareExchange(ref _teardownReason, (int)TeardownReason.HoldTimerExpired, (int)TeardownReason.None) == (int)TeardownReason.None)
                {
                    try { await SendNotificationAsync(BgpConstants.Error.HoldTimerExpired, BgpConstants.SubError.Unspecific); }
                    catch { /* best-effort — partial write counts, see RFC 4271 §8.1 */ }
                }
                return;
            }

            await SendKeepaliveAsync();
            _logger.LogDebug("KeepAliveSent to {Peer}", _peer);
        }
    }

    private async Task HandleUpdateAsync(BgpUpdateMessage update)
    {
        _logger.LogInformation("UpdateReceived from {Peer}: {Withdrawn} withdrawn, {Nlri} announced",
            _peer, update.WithdrawnRoutes.Count, update.Nlri.Count);

        // Process withdrawals
        foreach (var w in update.WithdrawnRoutes)
        {
            _routeTable.Remove(w.Address, w.Length);
            _logger.LogDebug("Route withdrawn: {Prefix}", w);
        }

        // Process announcements
        if (update.Nlri.Count > 0)
        {
            var origin = BgpOrigin.Incomplete;
            uint nextHop = 0;
            uint[] asPath = [];
            uint[] communities = [];

            foreach (var attr in update.PathAttributes)
            {
                switch (attr.TypeCode)
                {
                    case BgpConstants.Attribute.Origin:
                        if (attr.Data.Length < 1)
                            throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.Unspecific, "Malformed ORIGIN attribute");
                        origin = AttributeHelper.ReadOrigin(attr);
                        break;
                    case BgpConstants.Attribute.AsPath:
                        asPath = AttributeHelper.ReadAsPath(attr, _remoteFourByteAsn);
                        break;
                    case BgpConstants.Attribute.NextHop:
                        if (attr.Data.Length < 4)
                            throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.Unspecific, "Malformed NEXT_HOP attribute");
                        nextHop = AttributeHelper.ReadNextHop(attr);
                        break;
                    case BgpConstants.Attribute.Community:
                        communities = AttributeHelper.ReadCommunities(attr);
                        break;
                }
            }

            foreach (var nlri in update.Nlri)
            {
                var route = new Route
                {
                    Prefix = nlri.Address,
                    PrefixLength = nlri.Length,
                    NextHop = nextHop,
                    AsPath = asPath,
                    Communities = communities
                };

                if (_routeFilter.AcceptIncoming(route, _peerConfig))
                {
                    _routeTable.AddOrUpdate(route);
                    _logger.LogDebug("Route added: {Prefix} via {NextHop}", nlri, BgpConstants.UintToIPAddress(nextHop));
                }
            }
        }

        _metrics.SetRouteCount(_routeTable.Count);
    }

    private async Task SendAllRoutesAsync()
    {
        var nextHop = BgpConstants.IPAddressToUint(_bgpConfig.GetRouterIdAddress());
        var routes = new List<Route>();

        if (_peerStore is not null && _prefixService is not null && _appConfig is not null)
        {
            var peer = _peerStore.GetPeer(_peerConfig.Address, _remoteAsn);
            if (peer is not null)
            {
                _peerStore.UpdateSessionStatus(_peerConfig.Address, _remoteAsn, true);

                var subscriptionIds = _peerStore.GetSubscriptions(peer.Id);
                var customPrefixes = _peerStore.GetCustomPrefixes(peer.Id);
                var customAsns = _peerStore.GetCustomAsns(peer.Id);

                // Unconfigured peer — send RU defaults
                if (subscriptionIds.Count == 0 && customPrefixes.Count == 0 && customAsns.Count == 0)
                {
                    _logger.LogInformation("Unconfigured peer {Peer}, sending RU defaults", _peer);
                    try
                    {
                        var ruPrefixes = await _prefixService.GetRuPrefixesAsync();
                        foreach (var (prefix, length, _) in ruPrefixes)
                        {
                            routes.Add(new Route
                            {
                                Prefix = prefix,
                                PrefixLength = length,
                                NextHop = nextHop
                            });
                        }
                        _logger.LogInformation("Sent {Count} RU prefixes to unconfigured peer {Peer}",
                            ruPrefixes.Count, _peer);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to fetch RU prefixes for {Peer}", _peer);
                    }

                    await SendRoutesAsync(nextHop, routes);
                    return;
                }

                _logger.LogInformation("Peer {Peer} subscriptions: [{Subs}]", _peer, string.Join(", ", subscriptionIds));

                var subscribedLists = _appConfig?.RipeStat?.AsnLists
                    .Where(l => subscriptionIds.Contains(l.Name))
                    .ToList() ?? [];

                // ASN-based lists
                var asns = subscribedLists
                    .Where(l => l.Asns.Count > 0)
                    .SelectMany(l => l.Asns)
                    .ToList();

                _logger.LogInformation("Peer {Peer} resolved {Count} ASNs from subscriptions", _peer, asns.Count);

                if (asns.Count > 0)
                {
                    try
                    {
                        var prefixes = await _prefixService.GetPrefixesForAsns(asns);
                        foreach (var (prefix, length, asn) in prefixes)
                        {
                            routes.Add(new Route
                            {
                                Prefix = prefix,
                                PrefixLength = length,
                                NextHop = nextHop,
                                AsPath = [asn]
                            });
                        }
                        _logger.LogInformation("Fetched {Count} prefixes for {Peer} from ASN subscriptions",
                            routes.Count, _peer);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to fetch prefixes for {Peer}", _peer);
                    }
                }

                // Country-based lists (e.g. RU with no ASNs → use local nets.txt)
                if (subscribedLists.Any(l => l.Asns.Count == 0 && l.Country is not null))
                {
                    try
                    {
                        var ruPrefixes = await _prefixService.GetRuPrefixesAsync();
                        foreach (var (prefix, length, _) in ruPrefixes)
                        {
                            routes.Add(new Route
                            {
                                Prefix = prefix,
                                PrefixLength = length,
                                NextHop = nextHop
                            });
                        }
                        _logger.LogInformation("Fetched {Count} RU prefixes for {Peer}", ruPrefixes.Count, _peer);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to fetch RU prefixes for {Peer}", _peer);
                    }
                }

                // Prefix-source subscriptions: subscribed names that aren't RIPE ASN/country lists but
                // match a configured PrefixSource (e.g. "microsoft", "aws"). "ru" is already resolved
                // as a country list above, so it isn't fetched twice.
                var resolvedAsRipe = subscribedLists.Select(l => l.Name).ToHashSet();
                var sourceNames = subscriptionIds
                    .Where(n => !resolvedAsRipe.Contains(n) && _appConfig!.PrefixSources.Any(s => s.Name == n))
                    .ToList();
                foreach (var name in sourceNames)
                {
                    try
                    {
                        var srcPrefixes = await _prefixService.GetSourcePrefixesAsync(name);
                        foreach (var (prefix, length) in srcPrefixes)
                        {
                            routes.Add(new Route
                            {
                                Prefix = prefix,
                                PrefixLength = length,
                                NextHop = nextHop
                            });
                        }
                        _logger.LogInformation("Fetched {Count} prefixes from source '{Source}' for {Peer}",
                            srcPrefixes.Count, name, _peer);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to fetch source '{Source}' for {Peer}", name, _peer);
                    }
                }

                // Add custom prefixes (already loaded above)
                _logger.LogInformation("Peer {Peer} has {SubRoutes} subscription routes + {CustomCount} custom prefixes",
                    _peer, routes.Count, customPrefixes.Count);

                foreach (var cidr in customPrefixes)
                {
                    var slash = cidr.IndexOf('/');
                    var ip = IPAddress.Parse(cidr[..slash]);
                    var length = byte.Parse(cidr[(slash + 1)..]);
                    var prefix = BgpConstants.IPAddressToUint(ip);
                    routes.Add(new Route
                    {
                        Prefix = prefix,
                        PrefixLength = length,
                        NextHop = nextHop
                    });
                }

                // Add custom AS prefixes (already loaded above)
                if (customAsns.Count > 0)
                {
                    try
                    {
                        var asnPrefixes = await _prefixService.GetPrefixesForAsns(customAsns);
                        foreach (var (prefix, length, asn) in asnPrefixes)
                        {
                            routes.Add(new Route
                            {
                                Prefix = prefix,
                                PrefixLength = length,
                                NextHop = nextHop,
                                AsPath = [asn]
                            });
                        }
                        _logger.LogInformation("Peer {Peer} custom AS: {Asns} -> {Count} prefixes",
                            _peer, string.Join(",", customAsns), asnPrefixes.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to fetch custom AS prefixes for {Peer}", _peer);
                    }
                }

                _logger.LogInformation("Sending {Count} total routes to {Peer}", routes.Count, _peer);

                // Configured peer resolved 0 prefixes — fall back to RU
                if (routes.Count == 0)
                {
                    _logger.LogInformation("Peer {Peer} resolved 0 prefixes, falling back to RU defaults", _peer);
                    try
                    {
                        var ruPrefixes = await _prefixService.GetRuPrefixesAsync();
                        foreach (var (prefix, length, _) in ruPrefixes)
                        {
                            routes.Add(new Route
                            {
                                Prefix = prefix,
                                PrefixLength = length,
                                NextHop = nextHop
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to fetch RU fallback for {Peer}", _peer);
                    }
                }

                await SendRoutesAsync(nextHop, routes);
                return;
            }
            else
            {
                // Unknown peer — auto-register and send default RU list
                _logger.LogInformation("Unknown peer {Ip}, auto-registering with RU defaults", _peer);

                _peerStore.CreatePeer(_peerConfig.Address, _remoteAsn, null);

                try
                {
                    var ruPrefixes = await _prefixService.GetRuPrefixesAsync();
                    foreach (var (prefix, length, _) in ruPrefixes)
                    {
                        routes.Add(new Route
                        {
                            Prefix = prefix,
                            PrefixLength = length,
                            NextHop = nextHop
                        });
                    }
                    _logger.LogInformation("Fetched {Count} RU prefixes for unknown peer {Peer}",
                        ruPrefixes.Count, _peer);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to fetch RU prefixes for {Peer}", _peer);
                }

                await SendRoutesAsync(nextHop, routes);
                return;
            }
        }

        // Final fallback: send from shared route table (single pass — one allocation, not two)
        var filtered = new List<Route>();
        foreach (var r in _routeTable.Enumerate())
        {
            if (_routeFilter.AcceptOutgoing(r, _peerConfig))
                filtered.Add(r);
        }
        if (filtered.Count == 0) return;

        await SendRoutesAsync(nextHop, filtered);
    }

    private async Task SendRoutesAsync(uint nextHop, List<Route> routes)
    {
        // Summarize before sending: merge adjacent/overlapping prefixes into the minimal
        // exact set (no extra IPs). Choke point for both initial send and RefreshRoutesAsync,
        // so _advertisedPrefixes stays consistent with what we later withdraw.
        var aggregated = _prefixAggregator.Aggregate(routes);
        if (_logger.IsEnabled(LogLevel.Information) && aggregated.Count != routes.Count)
            _logger.LogInformation("Aggregated {Before} -> {After} prefixes for {Peer}",
                routes.Count, aggregated.Count, _peer);
        routes = aggregated as List<Route> ?? aggregated.ToList();

        const int maxNlriPerUpdate = 100;
        _advertisedPrefixes.EnsureCapacity(_advertisedPrefixes.Count + routes.Count);
        var sent = 0;
        var batch = new List<Route>(maxNlriPerUpdate);

        foreach (var route in routes)
        {
            batch.Add(route);
            _advertisedPrefixes.Add(new IpPrefix(route.Prefix, route.PrefixLength));
            if (batch.Count >= maxNlriPerUpdate)
            {
                await SendRouteBatchAsync(nextHop, batch);
                sent += batch.Count;
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await SendRouteBatchAsync(nextHop, batch);
            sent += batch.Count;
        }

        _logger.LogInformation("UpdateSent {Count} routes to {Peer}", sent, _peer);
    }

    private async Task SendRouteBatchAsync(uint nextHop, List<Route> routes)
    {
        // The COMMUNITY path attribute applies to EVERY NLRI in an UPDATE, so partition the
        // batch by community set and emit one UPDATE per set. Otherwise prefixes belonging to
        // one group would be tagged with another group's communities on the wire.
        foreach (var groupRoutes in GroupByCommunitySet(routes))
        {
            var attrs = new List<PathAttribute>
            {
                AttributeHelper.WriteOrigin(BgpOrigin.Igp),
                AttributeHelper.WriteNextHop(nextHop)
            };

            // RFC 6793 §6: AS_PATH encoding depends on peer capability
            if (_localFourByteAsn)
            {
                // 4-byte peer: send AS_PATH in 4-byte form
                attrs.Insert(1, AttributeHelper.WriteAsPath([_bgpConfig.Asn], fourByteAsn: true));
            }
            else
            {
                // 2-byte-only peer: send AS_PATH in 2-byte form with AS_TRANS if needed
                var asPathAsn = _bgpConfig.Asn > ushort.MaxValue
                    ? BgpConstants.AsPathAsTrans // 23456
                    : _bgpConfig.Asn;
                attrs.Insert(1, AttributeHelper.WriteAsPath([asPathAsn], fourByteAsn: false));

                // If local ASN > 65535, append AS4_PATH (type 17) with true 4-byte ASN
                if (_bgpConfig.Asn > ushort.MaxValue)
                    attrs.Add(AttributeHelper.WriteAs4Path([_bgpConfig.Asn]));
            }

            var communities = groupRoutes[0].Communities;
            if (communities.Length > 0)
                attrs.Add(AttributeHelper.WriteCommunities(communities));

            var nlri = groupRoutes.Select(r => new IpPrefix(r.Prefix, r.PrefixLength)).ToList();
            await SendUpdateBatchAsync(attrs, nlri);
        }
    }

    /// <summary>
    /// Partitions routes into groups that share an identical community set, so each emitted
    /// UPDATE carries a single COMMUNITY attribute. Internal for test coverage.
    /// </summary>
    internal static List<List<Route>> GroupByCommunitySet(IReadOnlyList<Route> routes) =>
        routes.GroupBy(r => r.Communities, CommunitySetComparer.Instance)
              .Select(g => g.ToList())
              .ToList();

    /// <summary>Sequence equality over a route's community array (set-equivalence within a batch).</summary>
    private sealed class CommunitySetComparer : IEqualityComparer<uint[]>
    {
        public static readonly CommunitySetComparer Instance = new();

        public bool Equals(uint[]? x, uint[]? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null || x.Length != y.Length) return false;
            for (var i = 0; i < x.Length; i++)
                if (x[i] != y[i]) return false;
            return true;
        }

        public int GetHashCode(uint[] obj)
        {
            var hc = new HashCode();
            foreach (var c in obj) hc.Add(c);
            return hc.ToHashCode();
        }
    }

    private async Task SendUpdateBatchAsync(List<PathAttribute> attrs, List<IpPrefix> nlri)
    {
        var update = new BgpUpdateMessage
        {
            PathAttributes = attrs,
            Nlri = nlri
        };

        await SendMessageAsync(update);
        _metrics.UpdateSent();
    }

    /// <summary>
    /// End-of-RIB marker for IPv4 unicast (RFC 4724 §2): a minimum-length UPDATE (no withdrawn
    /// routes, no path attributes, no NLRI). Signals completion of the initial routing update so
    /// GR-capable peers finalize — replacing stale routes with what we re-advertised and purging
    /// the rest. Lock is acquired inside SendMessageAsync.
    /// </summary>
    private async Task SendEndOfRibAsync()
    {
        await SendMessageAsync(new BgpUpdateMessage());
        _metrics.UpdateSent();
        _logger.LogDebug("End-of-RIB sent to {Peer}", _peer);
    }

    #region Message I/O

    private async Task<BgpMessage> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        var headerBuffer = ArrayPool<byte>.Shared.Rent(BgpConstants.MessageHeaderSize);
        try
        {
            await ReadExactAsync(headerBuffer.AsMemory(0, BgpConstants.MessageHeaderSize), cancellationToken);

            var length = BgpMessageReader.GetMessageLength(headerBuffer);
            if (length is < BgpConstants.MinMessageSize or > BgpConstants.MaxMessageSize)
                throw new BgpParseException($"Invalid message length: {length}");

            var payloadSize = length - BgpConstants.MessageHeaderSize;
            var messageBuffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                Array.Copy(headerBuffer, messageBuffer, BgpConstants.MessageHeaderSize);

                if (payloadSize > 0)
                    await ReadExactAsync(messageBuffer.AsMemory(BgpConstants.MessageHeaderSize, payloadSize), cancellationToken);

                return BgpMessageReader.ReadMessage(messageBuffer.AsSpan(0, length));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(messageBuffer);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }
    }

    // Single synchronized entry point for ALL outbound BGP bytes (RFC 4271 framing requires a
    // continuous message stream; NetworkStream is not thread-safe). Callers do NOT need to
    // acquire _sendLock themselves — every send path goes through here.
    private async Task SendMessageAsync(BgpMessage message)
    {
        try
        {
            await _sendLock.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            var bufferSize = BgpMessageWriter.GetBufferSize(message);
            var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                var written = BgpMessageWriter.WriteMessage(message, buffer);
                await _stream.WriteAsync(buffer.AsMemory(0, written));
            }
            catch (ObjectDisposedException)
            {
                // Session disposed mid-send — best effort during teardown.
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            try { _sendLock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer[totalRead..], cancellationToken);
            if (read == 0)
                throw new IOException("Connection closed by peer");
            totalRead += read;
        }
    }

    #endregion

    #region BGP Messages

    private async Task SendOpenAsync(BgpOpenMessage remoteOpen)
    {
        var capabilities = new List<BgpCapabilityInfo>
        {
            BgpCapabilityInfo.FourOctetAsn(_bgpConfig.Asn)
        };

        // Only advertise MP IPv4/Unicast if peer specifically supports IPv4/Unicast
        if (PeerHasMpIpv4Unicast(remoteOpen.Capabilities))
            capabilities.Add(BgpCapabilityInfo.MultiprotocolIpv4Unicast());

        // Only advertise Route Refresh if peer also supports it
        if (remoteOpen.Capabilities.Any(c => c.Code == BgpConstants.Capability.RouteRefresh))
            capabilities.Add(BgpCapabilityInfo.RouteRefresh());

        // Advertise Graceful Restart (RFC 4724) so GR-capable peers retain our routes across our
        // restart. R=1: every app start is treated as a restart (harmless on first connect, reduces
        // churn on transient reconnects; proper restart detection would need a persisted generation
        // counter — future work). F reflects whether forwarding state is preserved and is configurable
        // (the peer keeps stale routes only while F=1, RFC 4724 §4.2). Restart Time is clamped to
        // <= HoldTime here and to the 12-bit field max in the codec. Advertised unconditionally when
        // enabled (RFC 4724 §4 recommends it; non-GR peers safely ignore it per RFC 5492).
        if (_bgpConfig.GracefulRestart)
        {
            var restartTime = (ushort)Math.Min(_bgpConfig.RestartTime, _bgpConfig.HoldTime);
            capabilities.Add(BgpCapabilityInfo.GracefulRestart(
                restartState: true, restartTime, forwardingState: _bgpConfig.GracefulRestartForwardingState));
        }

        var asn16 = _bgpConfig.Asn > ushort.MaxValue ? (ushort)23456 : (ushort)_bgpConfig.Asn;
        var routerId = BgpConstants.IPAddressToUint(_bgpConfig.GetRouterIdAddress());

        _logger.LogInformation(
            "Sending OPEN: ASN={Asn} RouterId={RouterId} Capabilities=[{Caps}]",
            asn16, BgpConstants.UintToIPAddress(routerId),
            string.Join(", ", capabilities.Select(c => $"Code={c.Code}")));

        var open = new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = asn16,
            HoldTime = (ushort)_bgpConfig.HoldTime,
            RouterId = routerId,
            Capabilities = capabilities
        };

        await SendMessageAsync(open);
    }

    private static bool PeerHasMpIpv4Unicast(List<BgpCapabilityInfo> caps)
    {
        foreach (var cap in caps)
        {
            if (cap.Code != BgpConstants.Capability.Multiprotocol || cap.Data.Length < 4) continue;
            var afi = (ushort)((cap.Data[0] << 8) | cap.Data[1]);
            var safi = cap.Data[3];
            if (afi == BgpConstants.Afi.IPv4 && safi == BgpConstants.Safi.Unicast)
                return true;
        }
        return false;
    }

    private Task SendKeepaliveAsync() => SendMessageAsync(BgpKeepaliveMessage.Instance);

    private async Task SendNotificationAsync(byte errorCode, byte subErrorCode)
    {
        var notification = new BgpNotificationMessage { ErrorCode = errorCode, SubErrorCode = subErrorCode };
        await SendMessageAsync(notification);
        _logger.LogInformation("NotificationSent to {Peer}: {Error}/{SubError}", _peer, errorCode, subErrorCode);
    }

    /// <summary>
    /// Best-effort Cease NOTIFICATION for graceful shutdown (RFC 4271 §6.2). The caller (BgpServer)
    /// should only invoke this on an Established session and only when Graceful Restart is disabled —
    /// a NOTIFICATION termination bypasses GR (RFC 4724 §4), so with GR on we drop the TCP connection
    /// instead to let peers retain our routes. Write/IO errors are swallowed (we are shutting down).
    /// </summary>
    public async Task NotifyCeaseAsync()
    {
        // Atomically claim the teardown as LocalCease BEFORE sending. If a concurrent
        // MarkSilentClose (GR-aware shutdown / session replacement) or a peer NOTIFICATION
        // or hold timer expiry already latched a reason, the CAS fails and we send nothing —
        // preserving the silent close (RFC 4724 §4), no-reply (RFC 4271 §6.3), and
        // exactly-one-NOTIFICATION (§8.1).
        if (Interlocked.CompareExchange(ref _teardownReason, (int)TeardownReason.LocalCease, (int)TeardownReason.None) != (int)TeardownReason.None)
            return;

        try
        {
            await SendNotificationAsync(BgpConstants.Error.Cease, BgpConstants.SubError.Unspecific);
            _logger.LogInformation("Cease sent to {Peer} on shutdown", _peer);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send Cease to {Peer} on shutdown", _peer);
        }
    }

    /// <summary>
    /// Marks this session for a silent teardown (no NOTIFICATION). Used by Graceful-Restart-aware
    /// shutdown (RFC 4724 §4) and by session replacement: a NOTIFICATION termination would bypass GR,
    /// so the TCP connection is dropped silently so peers retain routes across the restart. Also
    /// cancels the session's own CTS so the read/keepalive loops stop promptly instead of lingering
    /// until the peer closes the socket or the hold timer fires. Must be called BEFORE the session
    /// is removed/replaced; the RunAsync finally-block observes the latched reason and emits nothing.
    /// </summary>
    public void MarkSilentClose()
    {
        // Only latch SilentClose if no reason was latched yet. If a catch block already sent a
        // Cease (LocalCease) or the peer already sent a NOTIFICATION (RemoteNotification), respect
        // that reason — the session is already tearing down for it. Overwriting it would mask the
        // real cause and, combined with the finally-block CAS, could let a second NOTIFICATION slip
        // through. The CTS cancel is ALWAYS issued (we must unwind the loops regardless of reason).
        Interlocked.CompareExchange(ref _teardownReason, (int)TeardownReason.SilentClose, (int)TeardownReason.None);
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* already disposed — fine */ }
        _logger.LogInformation("Session {Peer} marked for silent close", _peer);
    }

    #endregion

    #region Validation

    private void ValidateOpen(BgpOpenMessage open)
    {
        if (open.Version != BgpConstants.BgpVersion)
            throw new BgpNotificationException(BgpConstants.Error.OpenMessageError, BgpConstants.SubError.UnsupportedVersion, $"Unsupported BGP version: {open.Version}");

        _remoteFourByteAsn = CapabilityHelper.GetRemoteAsn(open).HasValue;
        _remoteAsn = CapabilityHelper.GetEffectiveAsn(open);
        // RFC 6793 §6: derive AS_PATH encoding format from negotiated capability
        _localFourByteAsn = _remoteFourByteAsn;

        _onPeerIdentified?.Invoke(_peerConfig.Address, _remoteAsn);

        if (_peerConfig.RemoteAsn.HasValue && _remoteAsn != _peerConfig.RemoteAsn.Value)
            throw new BgpNotificationException(BgpConstants.Error.OpenMessageError, BgpConstants.SubError.BadPeerAs, $"Unexpected ASN: expected {_peerConfig.RemoteAsn}, got {_remoteAsn}");

        var holdTime = open.HoldTime;
        if (holdTime != 0 && holdTime < 3)
            throw new BgpNotificationException(BgpConstants.Error.OpenMessageError, BgpConstants.SubError.UnacceptableHoldTime, $"Unacceptable hold time: {holdTime}");

        // BGP Identifier must be non-zero and must not collide with our own (RFC 4271 §6.2).
        if (open.RouterId == 0)
            throw new BgpNotificationException(BgpConstants.Error.OpenMessageError, BgpConstants.SubError.BadBgpIdentifier, "Invalid BGP identifier: 0.0.0.0");

        var localRouterId = BgpConstants.IPAddressToUint(_bgpConfig.GetRouterIdAddress());
        if (open.RouterId == localRouterId)
            throw new BgpNotificationException(BgpConstants.Error.OpenMessageError, BgpConstants.SubError.BadBgpIdentifier, "BGP identifier collision with local RouterId");

        var peerGr = CapabilityHelper.GetGracefulRestart(open);
        _logger.LogInformation("Peer {Peer} Graceful Restart: {State}",
            _peer,
            peerGr.HasValue
                ? $"supported (restartState={peerGr.Value.RestartState}, restartTime={peerGr.Value.RestartTime}s, IPv4/Unicast forwarding={peerGr.Value.Ipv4UnicastForwarding})"
                : "not supported");

        _negotiatedHoldTime = holdTime;
        _keepAliveInterval = holdTime == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Max(holdTime / 3, 1));
    }

    #endregion

    private void TransitionTo(BgpFsmState newState)
    {
        _logger.LogDebug("FSM: {Old} → {New} for {Peer}", _state, newState, _peer);
        _state = newState;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _cts.Cancel();
        _stream.Dispose();
        _socket.Dispose();
        _cts.Dispose();
        _sendLock.Dispose();
        _advertisedPrefixesLock.Dispose();
    }
}

/// <summary>
/// Why a session is tearing down. Drives whether the RunAsync finally-block emits a best-effort
/// Cease (RFC 4271 §8.1: exactly one NOTIFICATION per teardown). Only <see cref="None"/> (an
/// unexpected close from Established) triggers a Cease from the finally; every other reason has
/// either already produced a NOTIFICATION or is a deliberate silent close (GR/replace).
/// </summary>
internal enum TeardownReason
{
    /// <summary>No teardown reason latched yet — the finally may send a Cease from Established.</summary>
    None = 0,
    /// <summary>We emitted a Cease (catch block or NotifyCeaseAsync) — do not double-send.</summary>
    LocalCease,
    /// <summary>Peer sent a NOTIFICATION — release resources/Idle, do NOT reply (RFC 4271 §6.3/§8.1).</summary>
    RemoteNotification,
    /// <summary>We emitted Hold Timer Expired — do not double-send.</summary>
    HoldTimerExpired,
    /// <summary>Silent close: Graceful-Restart-aware shutdown or session replacement drops the TCP
    /// connection so peers retain routes (RFC 4724 §4) — emit no NOTIFICATION.</summary>
    SilentClose,
}
