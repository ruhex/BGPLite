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
    private readonly ICommunityResolver _communityResolver;

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
    private bool _remoteRouteRefresh;
    private bool _localFourByteAsn; // derived from negotiated OPEN capability (RFC 6793)
    private ushort _negotiatedHoldTime;
    private List<IpPrefix> _advertisedPrefixes = [];
    private TimeSpan _keepAliveInterval;
    private long _lastReceivedTicks; // UTC ticks of last received message; drives the HoldTimer (Interlocked)
    // Debounce ROUTE_REFRESH (RFC 2918): rate-limit per-session route re-announcements to avoid
    // DoS where a peer spams type-5 and forces a full re-advertise. Initial 0 = never refreshed.
    // Read/written via Interlocked so RefreshRoutesAsync and ReadLoopAsync can't race.
    private long _lastRouteRefreshTicks;
    // Minimum gap between peer-triggered route refreshes. 1s is a reasonable default:
    // long enough to make flood-DoS impractical, short enough that a legitimate peer retry
    // after a lost UPDATE still gets a fresh advertisement promptly.
    private static readonly TimeSpan MinRouteRefreshInterval = TimeSpan.FromSeconds(1);

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
        IPrefixAggregator? prefixAggregator = null,
        ICommunityResolver? communityResolver = null)
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
        _communityResolver = communityResolver ?? NullCommunityResolver.Instance;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        try
        {
            TransitionTo(BgpFsmState.Connect);
            _metrics.PeerConnected();
            _logger.LogInformation("PeerConnected {Peer}", _peer);

            // Receive OPEN — bounded by a connect-to-OPEN timeout (#115, Slowloris defense). The
            // negotiated hold timer only starts AFTER the handshake, so without this bound a
            // connection that opens TCP but never sends OPEN pins a BgpSession + task + socket FD
            // until the OS TCP timeout (minutes). OpenTimeoutSeconds=0 disables the timeout (legacy
            // behavior). The timeout CTS is linked to linkedCts and disposed right after OPEN is
            // received so later receives fall back to the session-wide linkedCts / negotiated hold
            // timer. On a pure timeout (external/session token NOT cancelled) we drop the peer.
            var openTimeoutSeconds = _bgpConfig.OpenTimeoutSeconds;
            BgpMessage openMessage;
            if (openTimeoutSeconds > 0)
            {
                using var openCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);
                openCts.CancelAfter(TimeSpan.FromSeconds(openTimeoutSeconds));
                try
                {
                    openMessage = await ReceiveMessageAsync(openCts.Token);
                }
                catch (OperationCanceledException) when (!linkedCts.IsCancellationRequested)
                {
                    // Only the OPEN timeout fired (the external/session token is still alive) — the
                    // peer never completed the handshake. Drop it; do not emit a NOTIFICATION (the
                    // FSM never reached OpenSent, and a Slowloris socket would not read it anyway).
                    _logger.LogWarning(
                        "No OPEN received from {Peer} within {Timeout}s — closing (Slowloris defense, #115)",
                        _peer, openTimeoutSeconds);
                    return;
                }
            }
            else
            {
                openMessage = await ReceiveMessageAsync(linkedCts.Token);
            }

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
                try { await SendNotificationAsync(ex.ErrorCode, ex.SubErrorCode, ex.NotificationData); }
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
                    try
                    {
                        await HandleUpdateAsync(update);
                    }
                    catch (BgpNotificationException ex) when (ex.ErrorCode == BgpConstants.Error.UpdateMessageError)
                    {
                        // Per-UPDATE content error (malformed attribute, missing mandatory attr, bad
                        // AS_PATH/AS4_PATH merge, …): the message was framed correctly, so this is one bad
                        // route, not a broken stream. RFC 7606 "treat-as-withdraw": discard the UPDATE and
                        // keep the session — a route-server should not lose a long-lived session over a
                        // single bad/adversarial UPDATE (#94). Reserve teardown for stream-level errors
                        // (FSM / message-header), which surface as other error codes and propagate.
                        // NOTE: deliberately do NOT send a NOTIFICATION — RFC 4271 §6.1 requires the
                        // receiver of a NOTIFICATION to tear down, so notifying would make the peer kill
                        // the very session we are trying to preserve.
                        _metrics.UpdateRejected();
                        _logger.LogWarning(
                            "Rejected malformed UPDATE from {Peer}: {Error}/{SubError} — {Reason}; session stays up",
                            _peer, ex.ErrorCode, ex.SubErrorCode, ex.Message);
                    }
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
                case BgpRouteRefreshMessage refresh:
                    _logger.LogInformation("RouteRefresh received from {Peer} for AFI={Afi} SAFI={Safi}", _peer, refresh.Afi, refresh.Safi);
                    if (!_remoteRouteRefresh)
                    {
                        _logger.LogWarning("RouteRefresh received from {Peer} without negotiated capability, ignoring", _peer);
                        break;
                    }
                    if (refresh.Afi != BgpConstants.Afi.IPv4 || refresh.Safi != BgpConstants.Safi.Unicast)
                    {
                        _logger.LogDebug("RouteRefresh ignored: unsupported AFI/SAFI from {Peer}", _peer);
                        break;
                    }
                    // Debounce: ignore ROUTE_REFRESH floods. Atomic check-and-set so a burst of N
                    // concurrent route refreshes from the peer can't all slip through and trigger
                    // N full re-announcements. First caller wins; the rest see a non-zero
                    // previous-timestamp and bail out cheaply with a debug log.
                    var nowTicks = DateTime.UtcNow.Ticks;
                    var prevTicks = Interlocked.Read(ref _lastRouteRefreshTicks);
                    if (prevTicks != 0 && new TimeSpan(nowTicks - prevTicks) < MinRouteRefreshInterval)
                    {
                        _logger.LogDebug("RouteRefresh rate-limited from {Peer} (last refresh {Ago} ago)",
                            _peer, new TimeSpan(nowTicks - prevTicks));
                        break;
                    }
                    if (Interlocked.CompareExchange(ref _lastRouteRefreshTicks, nowTicks, prevTicks) != prevTicks)
                    {
                        // Another refresh raced ahead of us; the winning call will do the work.
                        break;
                    }
                    await RefreshRoutesAsync();
                    break;
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
            try
            {
                var originSeen = false;
                var asPathSeen = false;
                var nextHopSeen = false;
                uint nextHop = 0;
                uint[] asPath = [];
                uint[] communities = [];
                (uint Global, uint Local1, uint Local2)[] largeCommunities = [];
                uint[] as4Path = [];
                uint? aggregatorAsn = null;
                uint? as4AggregatorAsn = null;
                var filterPeerConfig = GetFilterPeerConfig();

                foreach (var attr in update.PathAttributes)
                {
                    switch (attr.TypeCode)
                    {
                        case BgpConstants.Attribute.Origin:
                            if (attr.Data.Length < 1)
                                throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.Unspecific, "Malformed ORIGIN attribute");
                            AttributeHelper.ReadOrigin(attr);
                            originSeen = true;
                            break;
                        case BgpConstants.Attribute.AsPath:
                            asPath = AttributeHelper.ReadAsPath(attr, _remoteFourByteAsn);
                            asPathSeen = true;
                            break;
                        case BgpConstants.Attribute.As4Path when !_remoteFourByteAsn:
                            as4Path = AttributeHelper.ReadAs4Path(attr);
                            break;
                        case BgpConstants.Attribute.NextHop:
                            if (attr.Data.Length < 4)
                                throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.Unspecific, "Malformed NEXT_HOP attribute");
                            nextHop = AttributeHelper.ReadNextHop(attr);
                            nextHopSeen = true;
                            break;
                        case BgpConstants.Attribute.Community:
                            communities = AttributeHelper.ReadCommunities(attr);
                            break;
                        case BgpConstants.Attribute.LargeCommunity:
                            largeCommunities = AttributeHelper.ReadLargeCommunities(attr);
                            break;
                        case BgpConstants.Attribute.Aggregator:
                            aggregatorAsn = AttributeHelper.ReadAggregatorAsn(attr, _remoteFourByteAsn);
                            break;
                        case BgpConstants.Attribute.As4Aggregator when !_remoteFourByteAsn:
                            as4AggregatorAsn = AttributeHelper.ReadAs4AggregatorAsn(attr);
                            break;
                    }
                }

                ValidateMandatoryAttributes(originSeen, asPathSeen, nextHopSeen);
                asPath = MergeAsPathWithAs4Path(asPath, as4Path);
                ValidateAggregatorReconstruction(aggregatorAsn, as4AggregatorAsn);

                foreach (var nlri in update.Nlri)
                {
                    var route = new Route
                    {
                        Prefix = nlri.Address,
                        PrefixLength = nlri.Length,
                        NextHop = nextHop,
                        AsPath = asPath,
                        Communities = communities,
                        LargeCommunities = largeCommunities
                    };

                    if (_routeFilter.AcceptIncoming(route, filterPeerConfig))
                    {
                        _routeTable.AddOrUpdate(route);
                        _logger.LogDebug("Route added: {Prefix} via {NextHop}", nlri, BgpConstants.UintToIPAddress(nextHop));
                    }
                }
            }
            catch (BgpParseException ex)
            {
                throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.Unspecific, ex.Message);
            }
        }

        _metrics.SetRouteCount(_routeTable.Count);
    }

    private async Task SendAllRoutesAsync()
    {
        var nextHop = BgpConstants.IPAddressToUint(_bgpConfig.GetRouterIdAddress());
        var routes = new List<Route>();
        // Community for the default (RU) prefix source, if any — stamped on RU-default routes.
        var defaultComms = _communityResolver.Resolve(
            new CommunitySource(CommunitySourceKind.PrefixSource, _appConfig?.DefaultPrefixSource));

        if (_peerStore is not null && _prefixService is not null && _appConfig is not null)
        {
            var peer = _peerStore.LoadPeerRoutingView(_peerConfig.Address, _remoteAsn);
            if (peer is not null)
            {
                // LoadPeerRoutingView folds GetPeer + UpdateSessionStatus + GetSubscriptions +
                // GetCustomPrefixes + GetCustomAsns into a single DbContext (one read+write
                // roundtrip), replacing five separate PeerStore calls (issue #84).
                var subscriptionIds = peer.Subscriptions;
                var customPrefixes = peer.CustomPrefixes;
                var customAsns = peer.CustomAsns;

                // Unconfigured peer — send RU defaults
                if (subscriptionIds.Count == 0 && customPrefixes.Count == 0 && customAsns.Count == 0)
                {
                    _logger.LogInformation("Unconfigured peer {Peer}, sending RU defaults", _peer);
                    try
                    {
                        var ruPrefixes = await _prefixService.GetRuPrefixesAsync(_cts.Token);
                        foreach (var (prefix, length, _) in ruPrefixes)
                        {
                            routes.Add(MakeRoute(prefix, length, nextHop, null, defaultComms));
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

                // ASN-based lists — resolve per list so each list's community is stamped on its prefixes
                // (PrefixService caches per ASN, so per-list calls don't multiply RIPEstat traffic).
                // Each list is fetched in its own try/catch: one failing list does not drop the others'
                // prefixes (intentional resilience vs the old single-batch all-or-nothing path).
                var asnLists = subscribedLists.Where(l => l.Asns.Count > 0).ToList();

                _logger.LogInformation("Peer {Peer} resolved {Count} ASNs from subscriptions",
                    _peer, asnLists.SelectMany(l => l.Asns).Count());

                if (asnLists.Count > 0)
                {
                    var before = routes.Count;
                    foreach (var list in asnLists)
                    {
                        try
                        {
                            var comms = _communityResolver.Resolve(
                                new CommunitySource(CommunitySourceKind.AsnList, list.Name));
                            var prefixes = await _prefixService.GetPrefixesForAsns(list.Asns, _cts.Token);
                            foreach (var (prefix, length, asn) in prefixes)
                                routes.Add(MakeRoute(prefix, length, nextHop, [asn], comms));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to fetch prefixes for {Peer} (list '{List}')", _peer, list.Name);
                        }
                    }
                    _logger.LogInformation("Fetched {Count} prefixes for {Peer} from ASN subscriptions",
                        routes.Count - before, _peer);
                }

                // Country-based lists (e.g. RU with no ASNs → use local nets.txt). All country lists
                // currently resolve to the RU default prefix set; stamp the configured community
                // (if any) of the first country list on those prefixes.
                var countryLists = subscribedLists.Where(l => l.Asns.Count == 0 && l.Country is not null).ToList();
                if (countryLists.Count > 0)
                {
                    try
                    {
                        var comms = _communityResolver.Resolve(
                            new CommunitySource(CommunitySourceKind.Country, countryLists[0].Name));
                        var ruPrefixes = await _prefixService.GetRuPrefixesAsync(_cts.Token);
                        foreach (var (prefix, length, _) in ruPrefixes)
                            routes.Add(MakeRoute(prefix, length, nextHop, null, comms));
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
                        var comms = _communityResolver.Resolve(new CommunitySource(CommunitySourceKind.PrefixSource, name));
                        var srcPrefixes = await _prefixService.GetSourcePrefixesAsync(name, _cts.Token);
                        foreach (var (prefix, length) in srcPrefixes)
                            routes.Add(MakeRoute(prefix, length, nextHop, null, comms));
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

                // Custom prefixes carry the static "custom prefix" community (<Asn>:100).
                var customPrefixComms = _communityResolver.Resolve(new CommunitySource(CommunitySourceKind.Custom));
                foreach (var cidr in customPrefixes)
                {
                    var slash = cidr.IndexOf('/');
                    var ip = IPAddress.Parse(cidr[..slash]);
                    var length = byte.Parse(cidr[(slash + 1)..]);
                    var prefix = BgpConstants.IPAddressToUint(ip);
                    routes.Add(MakeRoute(prefix, length, nextHop, null, customPrefixComms));
                }

                // Add custom AS prefixes (already loaded above). Custom-AS routes carry the static
                // "custom AS" community (<Asn>:200).
                if (customAsns.Count > 0)
                {
                    try
                    {
                        var customAsnComms = _communityResolver.Resolve(new CommunitySource(CommunitySourceKind.CustomAsn));
                        var asnPrefixes = await _prefixService.GetPrefixesForAsns(customAsns, _cts.Token);
                        foreach (var (prefix, length, asn) in asnPrefixes)
                            routes.Add(MakeRoute(prefix, length, nextHop, [asn], customAsnComms));
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
                        var ruPrefixes = await _prefixService.GetRuPrefixesAsync(_cts.Token);
                        foreach (var (prefix, length, _) in ruPrefixes)
                            routes.Add(MakeRoute(prefix, length, nextHop, null, defaultComms));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to fetch RU fallback for {Peer}", _peer);
                    }
                }

                // Apply the per-peer outgoing community filter (same rule as the shared-table path).
                // Resolve the community allow-set ONCE for the whole send — not once per route (#79).
                var filterPeerConfig = GetFilterPeerConfig();
                var allowSet = _routeFilter.ResolveOutgoingAllowSet(filterPeerConfig);
                routes = routes.Where(r => _routeFilter.AcceptOutgoing(r, filterPeerConfig, allowSet)).ToList();
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
                    var ruPrefixes = await _prefixService.GetRuPrefixesAsync(_cts.Token);
                    foreach (var (prefix, length, _) in ruPrefixes)
                        routes.Add(MakeRoute(prefix, length, nextHop, null, defaultComms));
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
        var sharedFilterPeerConfig = GetFilterPeerConfig();
        var sharedAllowSet = _routeFilter.ResolveOutgoingAllowSet(sharedFilterPeerConfig);
        var filtered = new List<Route>();
        foreach (var r in _routeTable.Enumerate())
        {
            if (_routeFilter.AcceptOutgoing(r, sharedFilterPeerConfig, sharedAllowSet))
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

        // Path attributes for a community set are byte-identical across every 100-NLRI batch of
        // a single send (localAsn/localFourByteAsn/nextHop are constant for the whole send), so
        // build them once per community set and reuse instead of rebuilding on each batch (#87).
        // Scoped to this send only: the cache dies with the dictionary, so it can never serve a
        // later send that carries a different nextHop or renegotiated ASN.
        var attrCache = CreateUpdateAttributeCache();

        foreach (var route in routes)
        {
            batch.Add(route);
            _advertisedPrefixes.Add(new IpPrefix(route.Prefix, route.PrefixLength));
            if (batch.Count >= maxNlriPerUpdate)
            {
                await SendRouteBatchAsync(nextHop, batch, attrCache);
                sent += batch.Count;
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await SendRouteBatchAsync(nextHop, batch, attrCache);
            sent += batch.Count;
        }

        _logger.LogInformation("UpdateSent {Count} routes to {Peer}", sent, _peer);
    }

    private async Task SendRouteBatchAsync(uint nextHop, List<Route> routes, Dictionary<uint[], List<PathAttribute>> attrCache)
    {
        // The COMMUNITY/LARGE_COMMUNITY path attributes apply to EVERY NLRI in an UPDATE, so
        // partition the batch by (community set, large-community set) and emit one UPDATE per
        // set. Otherwise prefixes belonging to one group would be tagged with another group's
        // communities on the wire.
        foreach (var groupRoutes in GroupByCommunitySet(routes))
        {
            var attrs = GetCachedUpdateAttributes(_bgpConfig.Asn, _localFourByteAsn, nextHop, groupRoutes[0].Communities, attrCache);
            // LARGE_COMMUNITY is appended per group AFTER fetching the cached base attrs, so the
            // #87 cache stays keyed by regular communities only and is never mutated. Routes that
            // share regular communities but differ in large communities reuse the same base attrs
            // and only diverge in this final attribute.
            attrs = WithLargeCommunityAttribute(attrs, groupRoutes[0].LargeCommunities);

            var nlri = groupRoutes.Select(r => new IpPrefix(r.Prefix, r.PrefixLength)).ToList();
            await SendUpdateBatchAsync(attrs, nlri);
        }
    }

    /// <summary>
    /// Builds AS_PATH and optionally AS4_PATH attributes per RFC 6793 §6.
    /// - <paramref name="localFourByteAsn"/>=true: single 4-byte AS_PATH.
    /// - <paramref name="localFourByteAsn"/>=false: 2-byte AS_PATH (AS_TRANS(23456) if
    ///   <paramref name="localAsn"/> &gt; 65535) plus AS4_PATH carrying the true 4-byte ASN.
    /// Internal for test coverage.
    /// </summary>
    internal static List<PathAttribute> BuildAsPathAttributes(uint localAsn, bool localFourByteAsn)
    {
        var attrs = new List<PathAttribute>(2);
        if (localFourByteAsn)
        {
            attrs.Add(AttributeHelper.WriteAsPath([localAsn], fourByteAsn: true));
        }
        else
        {
            var asPathAsn = localAsn > ushort.MaxValue ? BgpConstants.AsPath.AsTrans : localAsn;
            attrs.Add(AttributeHelper.WriteAsPath([asPathAsn], fourByteAsn: false));

            if (localAsn > ushort.MaxValue)
                attrs.Add(AttributeHelper.WriteAs4Path([localAsn]));
        }
        return attrs;
    }

    /// <summary>
    /// Builds outbound UPDATE path attributes in RFC order: ORIGIN, AS_PATH, NEXT_HOP,
    /// COMMUNITY, AS4_PATH. Internal for test coverage.
    /// </summary>
    internal static List<PathAttribute> BuildUpdateAttributes(uint localAsn, bool localFourByteAsn, uint nextHop, uint[] communities)
    {
        var attrs = new List<PathAttribute>(5)
        {
            AttributeHelper.WriteOrigin(BgpOrigin.Igp),
        };

        var asPathAttrs = BuildAsPathAttributes(localAsn, localFourByteAsn);
        attrs.Add(asPathAttrs[0]);
        attrs.Add(AttributeHelper.WriteNextHop(nextHop));

        if (communities.Length > 0)
            attrs.Add(AttributeHelper.WriteCommunities(communities));

        if (asPathAttrs.Count > 1)
            attrs.Add(asPathAttrs[1]);

        return attrs;
    }

    /// <summary>
    /// Creates a per-send cache of built UPDATE path attributes, keyed by community set. The
    /// cache is scoped to a single <see cref="SendRoutesAsync"/> invocation: the ASN/nextHop
    /// inputs are constant for that whole send, so identical community sets yield byte-identical
    /// <see cref="PathAttribute"/> lists that can be reused across the N 100-NLRI batches (#87).
    /// Internal for test coverage.
    /// </summary>
    internal static Dictionary<uint[], List<PathAttribute>> CreateUpdateAttributeCache() =>
        new(CommunitySetComparer.Instance);

    /// <summary>
    /// Returns the UPDATE path attributes for <paramref name="communities"/>, building them on
    /// first request for a community set and returning the cached list thereafter. The cached
    /// <see cref="PathAttribute"/> payloads are immutable, so the same list is safely shared by
    /// every UPDATE emitted for that community set. Internal for test coverage.
    /// </summary>
    internal static List<PathAttribute> GetCachedUpdateAttributes(
        uint localAsn, bool localFourByteAsn, uint nextHop, uint[] communities,
        Dictionary<uint[], List<PathAttribute>> cache)
    {
        if (cache.TryGetValue(communities, out var cached))
            return cached;

        var attrs = BuildUpdateAttributes(localAsn, localFourByteAsn, nextHop, communities);
        cache[communities] = attrs;
        return attrs;
    }

    /// <summary>
    /// Returns the path attributes for an UPDATE carrying the given Large Community set: the
    /// cached base attributes (ORIGIN/AS_PATH/NEXT_HOP/COMMUNITY/AS4_PATH) untouched when
    /// <paramref name="largeCommunities"/> is empty, otherwise a shallow copy with a
    /// LARGE_COMMUNITY attribute appended. The cached base list is never mutated, so other
    /// batches in the same send that share regular communities but carry a different (or empty)
    /// large-community set still observe the correct base. Appended last, which keeps the
    /// emitted attributes in ascending type-code order (32 sorts after AS4_PATH 17). Internal
    /// for test coverage.
    /// </summary>
    internal static List<PathAttribute> WithLargeCommunityAttribute(
        List<PathAttribute> baseAttrs, (uint Global, uint Local1, uint Local2)[] largeCommunities)
    {
        if (largeCommunities.Length == 0)
            return baseAttrs;

        var withLarge = new List<PathAttribute>(baseAttrs.Count + 1);
        withLarge.AddRange(baseAttrs);
        withLarge.Add(AttributeHelper.WriteLargeCommunities(largeCommunities));
        return withLarge;
    }

    /// <summary>
    /// Validates that a route announcement carried the mandatory well-known attributes.
    /// Internal for test coverage.
    /// </summary>
    internal static void ValidateMandatoryAttributes(bool originSeen, bool asPathSeen, bool nextHopSeen)
    {
        if (!originSeen)
            throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.MissingWellKnownAttribute, "Missing mandatory ORIGIN attribute");
        if (!asPathSeen)
            throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.MissingWellKnownAttribute, "Missing mandatory AS_PATH attribute");
        if (!nextHopSeen)
            throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.MissingWellKnownAttribute, "Missing mandatory NEXT_HOP attribute");
    }

    /// <summary>
    /// Reconstructs the true AS path for a 2-byte peer using RFC 6793 trailing-sequence
    /// reconstruction. The last N ASNs in AS_PATH are replaced with the AS4_PATH values,
    /// where N = min(AS_PATH length, AS4_PATH length). Internal for test coverage.
    /// </summary>
    internal static uint[] MergeAsPathWithAs4Path(uint[] asPath, uint[] as4Path)
    {
        if (as4Path.Length == 0)
            return asPath;

        if (as4Path.Length > asPath.Length)
            throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.Unspecific, "AS4_PATH longer than AS_PATH");

        if (as4Path.Length == asPath.Length)
            return as4Path;

        var leadingCount = asPath.Length - as4Path.Length;
        for (var i = 0; i < leadingCount; i++)
        {
            if (asPath[i] == BgpConstants.AsPath.AsTrans)
                throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.Unspecific, "Unresolved AS_TRANS in AS_PATH");
        }

        var merged = new uint[asPath.Length];
        Array.Copy(asPath, 0, merged, 0, leadingCount);
        Array.Copy(as4Path, 0, merged, leadingCount, as4Path.Length);

        return merged;
    }

    /// <summary>
    /// Partitions routes into groups that share an identical (regular + large) community set,
    /// so each emitted UPDATE carries a single COMMUNITY and a single LARGE_COMMUNITY
    /// attribute. Internal for test coverage.
    /// </summary>
    internal static List<List<Route>> GroupByCommunitySet(IReadOnlyList<Route> routes)
    {
        if (routes.Count == 0)
            return [];

        // Fast path: the common case where every route in the batch carries the same
        // community set (both regular and large). Skip the GroupBy lookup-dictionary
        // allocation (and the per-route hashing it implies) and emit a single group directly.
        // Output is identical to the GroupBy path: one group holding every route in order.
        var first = routes[0];
        for (var i = 1; i < routes.Count; i++)
        {
            if (!SameCommunitySet(first, routes[i]))
                return PartitionByCommunitySet(routes);
        }

        return [new List<Route>(routes)];
    }

    /// <summary>
    /// True when two routes carry identical regular and large community sets (sequence
    /// equality), i.e. they are interchangeable as the community tag of a single UPDATE.
    /// </summary>
    private static bool SameCommunitySet(Route a, Route b) =>
        CommunitySetComparer.Instance.Equals(a.Communities, b.Communities) &&
        LargeCommunitySetComparer.Instance.Equals(a.LargeCommunities, b.LargeCommunities);

    /// <summary>Slow path: partition a batch whose routes span more than one community set.</summary>
    private static List<List<Route>> PartitionByCommunitySet(IReadOnlyList<Route> routes) =>
        routes.GroupBy(r => (r.Communities, r.LargeCommunities), CommunitySetPairComparer.Instance)
              .Select(g => g.ToList())
              .ToList();

    /// <summary>
    /// Builds a <see cref="Route"/> carrying the given regular and large community sets.
    /// Internal so the per-list community stamping logic is unit-testable without a live
    /// session (Route is init-only).
    /// </summary>
    internal static Route MakeRoute(
        uint prefix, byte length, uint nextHop, uint[]? asPath, uint[] communities,
        (uint Global, uint Local1, uint Local2)[]? largeCommunities = null) => new()
        {
            Prefix = prefix,
            PrefixLength = length,
            NextHop = nextHop,
            AsPath = asPath ?? [],
            Communities = communities,
            LargeCommunities = largeCommunities ?? []
        };

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

    /// <summary>Sequence equality over a route's Large Community array (RFC 8092 triplets).</summary>
    private sealed class LargeCommunitySetComparer : IEqualityComparer<(uint Global, uint Local1, uint Local2)[]>
    {
        public static readonly LargeCommunitySetComparer Instance = new();

        public bool Equals((uint Global, uint Local1, uint Local2)[]? x, (uint Global, uint Local1, uint Local2)[]? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null || x.Length != y.Length) return false;
            for (var i = 0; i < x.Length; i++)
                if (x[i] != y[i]) return false;
            return true;
        }

        public int GetHashCode((uint Global, uint Local1, uint Local2)[] obj)
        {
            var hc = new HashCode();
            foreach (var c in obj) hc.Add(c);
            return hc.ToHashCode();
        }
    }

    /// <summary>
    /// Composite sequence equality over a route's (regular, large) community pair, used to
    /// partition a send batch that spans more than one community set.
    /// </summary>
    private sealed class CommunitySetPairComparer
        : IEqualityComparer<(uint[] Communities, (uint Global, uint Local1, uint Local2)[] LargeCommunities)>
    {
        public static readonly CommunitySetPairComparer Instance = new();

        public bool Equals(
            (uint[] Communities, (uint Global, uint Local1, uint Local2)[] LargeCommunities) x,
            (uint[] Communities, (uint Global, uint Local1, uint Local2)[] LargeCommunities) y) =>
            CommunitySetComparer.Instance.Equals(x.Communities, y.Communities) &&
            LargeCommunitySetComparer.Instance.Equals(x.LargeCommunities, y.LargeCommunities);

        public int GetHashCode(
            (uint[] Communities, (uint Global, uint Local1, uint Local2)[] LargeCommunities) obj)
        {
            var hc = new HashCode();
            foreach (var c in obj.Communities) hc.Add(c);
            foreach (var l in obj.LargeCommunities) hc.Add(l);
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

    private PeerConfig GetFilterPeerConfig() => new()
    {
        Address = _peerConfig.Address,
        RemoteAsn = _remoteAsn,
        Description = _peerConfig.Description,
        Port = _peerConfig.Port
    };

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

        var asn16 = _bgpConfig.Asn > ushort.MaxValue ? (ushort)BgpConstants.AsPath.AsTrans : (ushort)_bgpConfig.Asn;
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
        await SendNotificationAsync(errorCode, subErrorCode, null);
    }

    private async Task SendNotificationAsync(byte errorCode, byte subErrorCode, byte[]? data)
    {
        var notification = new BgpNotificationMessage
        {
            ErrorCode = errorCode,
            SubErrorCode = subErrorCode,
            Data = data is null ? null : (byte[])data.Clone()
        };
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
            await SendNotificationAsync(BgpConstants.Error.Cease, BgpConstants.SubError.CeaseAdministrativeReset);
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
        var localRouterId = BgpConstants.IPAddressToUint(_bgpConfig.GetRouterIdAddress());
        var negotiation = ValidateOpen(open, _peerConfig.RemoteAsn, localRouterId);

        _remoteAsn = negotiation.RemoteAsn;
        _remoteFourByteAsn = negotiation.RemoteFourByteAsn;
        _localFourByteAsn = negotiation.LocalFourByteAsn;
        _remoteRouteRefresh = negotiation.RemoteRouteRefresh;
        _negotiatedHoldTime = negotiation.NegotiatedHoldTime;
        _keepAliveInterval = negotiation.KeepAliveInterval;

        // Announce/persist the peer only after the OPEN passes validation. Previously this fired
        // before the expected-ASN check, upserting a configured peer that declared a mismatched ASN
        // (BadPeerAs) just before the session was torn down.
        _onPeerIdentified?.Invoke(_peerConfig.Address, _remoteAsn);

        var peerGr = CapabilityHelper.GetGracefulRestart(open);
        _logger.LogInformation("Peer {Peer} Graceful Restart: {State}",
            _peer,
            peerGr.HasValue
                ? $"supported (restartState={peerGr.Value.RestartState}, restartTime={peerGr.Value.RestartTime}s, IPv4/Unicast forwarding={peerGr.Value.Ipv4UnicastForwarding})"
                : "not supported");
    }

    /// <summary>
    /// Negotiated OPEN parameters produced by <see cref="ValidateOpen(BgpOpenMessage, uint?, uint)"/>.
    /// </summary>
    internal sealed record OpenNegotiation(
        uint RemoteAsn,
        bool RemoteFourByteAsn,
        bool LocalFourByteAsn,
        bool RemoteRouteRefresh,
        ushort NegotiatedHoldTime,
        TimeSpan KeepAliveInterval);

    /// <summary>
    /// Pure OPEN validation + capability negotiation. Verifies BGP version, 4-octet-ASN capability
    /// well-formedness, the expected peer ASN (RFC 4271 §6.2 — <paramref name="expectedRemoteAsn"/>
    /// is null for auto-registered/unknown peers, accepting any declared ASN), the hold time
    /// (RFC 4271 §4.2 — 0 or ≥ 3), and the BGP Identifier (non-zero, no collision with the local
    /// router ID), then derives the negotiated session parameters. Throws
    /// <see cref="BgpNotificationException"/> with the RFC-mandated error/sub-error on rejection.
    /// Extracted as <c>internal static</c> so every branch is unit-testable without a live socket
    /// (mirrors <see cref="GetMalformedFourOctetAsnCapabilityData"/> / MergeAsPathWithAs4Path).
    /// </summary>
    internal static OpenNegotiation ValidateOpen(BgpOpenMessage open, uint? expectedRemoteAsn, uint localRouterId)
    {
        if (open.Version != BgpConstants.BgpVersion)
            throw new BgpNotificationException(BgpConstants.Error.OpenMessageError, BgpConstants.SubError.UnsupportedVersion, $"Unsupported BGP version: {open.Version}");

        var malformedFourOctetAsnCapability = GetMalformedFourOctetAsnCapabilityData(open);
        if (malformedFourOctetAsnCapability.Length > 0)
        {
            throw new BgpNotificationException(
                BgpConstants.Error.OpenMessageError,
                BgpConstants.SubError.UnsupportedCapability,
                "Malformed 4-octet ASN capability",
                malformedFourOctetAsnCapability);
        }

        var remoteFourByteAsn = CapabilityHelper.GetRemoteAsn(open).HasValue;
        var remoteAsn = CapabilityHelper.GetEffectiveAsn(open);
        var remoteRouteRefresh = open.Capabilities.Any(c => c.Code == BgpConstants.Capability.RouteRefresh);

        if (expectedRemoteAsn.HasValue && remoteAsn != expectedRemoteAsn.Value)
            throw new BgpNotificationException(BgpConstants.Error.OpenMessageError, BgpConstants.SubError.BadPeerAs, $"Unexpected ASN: expected {expectedRemoteAsn}, got {remoteAsn}");

        var holdTime = open.HoldTime;
        if (holdTime != 0 && holdTime < 3)
            throw new BgpNotificationException(BgpConstants.Error.OpenMessageError, BgpConstants.SubError.UnacceptableHoldTime, $"Unacceptable hold time: {holdTime}");

        // BGP Identifier must be non-zero and must not collide with our own (RFC 4271 §6.2).
        if (open.RouterId == 0)
            throw new BgpNotificationException(BgpConstants.Error.OpenMessageError, BgpConstants.SubError.BadBgpIdentifier, "Invalid BGP identifier: 0.0.0.0");

        if (open.RouterId == localRouterId)
            throw new BgpNotificationException(BgpConstants.Error.OpenMessageError, BgpConstants.SubError.BadBgpIdentifier, "BGP identifier collision with local RouterId");

        var keepAliveInterval = holdTime == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Max(holdTime / 3, 1));

        return new OpenNegotiation(
            remoteAsn,
            remoteFourByteAsn,
            remoteFourByteAsn, // RFC 6793 §6: AS_PATH encoding follows the negotiated capability
            remoteRouteRefresh,
            holdTime,
            keepAliveInterval);
    }

    internal static byte[] GetMalformedFourOctetAsnCapabilityData(BgpOpenMessage open)
    {
        foreach (var cap in open.Capabilities)
        {
            if (cap.Code == BgpConstants.Capability.FourOctetAsn && cap.Data.Length != 4)
                return [BgpConstants.Capability.FourOctetAsn, (byte)cap.Data.Length,
                    ..cap.Data];
        }

        return [];
    }

    internal static void ValidateAggregatorReconstruction(uint? aggregatorAsn, uint? as4AggregatorAsn)
    {
        if (aggregatorAsn == BgpConstants.AsPath.AsTrans && as4AggregatorAsn is null)
            throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.Unspecific, "Missing AS4_AGGREGATOR for AGGREGATOR AS_TRANS");

        if (!aggregatorAsn.HasValue && as4AggregatorAsn.HasValue)
            throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.Unspecific, "Missing AGGREGATOR attribute for AS4_AGGREGATOR");
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
