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
    // #96: transport seam — the concrete Socket/NetworkStream are owned by IBgpConnection
    // (SocketBgpConnection in production, a fake in unit tests). Replaces the prior _socket/_stream
    // pair. The send serialization (_sendLock) stays here — it's a BGP-framing concern, not transport.
    private readonly IBgpConnection _connection;
    // #96: time seam — TimeProvider replaces direct DateTime.UtcNow reads so the hold-timer expiry,
    // keepalive interval, and ROUTE_REFRESH debounce are deterministic-testable. Defaults to
    // TimeProvider.System (wall-clock) in production; tests inject a FakeTimeProvider.
    private readonly TimeProvider _timeProvider;
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
    // #93 Phase 2: the outbound route-assembly policy lives here, not in the session. The session
    // delegates to BuildOutboundRoutesAsync and keeps the send/withdraw mirror (_advertisedPrefixes)
    // and the codec glue (SendRoutesAsync).
    private readonly RouteAssembler _routeAssembler;

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
    /// <summary>The remote ASN negotiated from the peer's OPEN (#206). Set after ValidateOpen; used by
    /// BgpServer.RefreshPeerAsync to filter sessions by (Ip, Asn) on shared IPs.</summary>
    public uint RemoteAsn => _remoteAsn;
    public bool IsEstablished => _state == BgpFsmState.Established;

    public async Task RefreshRoutesAsync(CancellationToken ct = default)
    {
        if (!IsEstablished) return;

        // _sendLock is acquired inside SendMessageAsync, so each individual UPDATE is atomic on the
        // wire. _advertisedPrefixesLock serializes the (withdraw + re-announce) pair against the
        // initial-send, which mutates the same list concurrently. We do NOT hold _sendLock across
        // the whole pair: a HoldTimer expiry or peer NOTIFICATION that arrives between them would
        // otherwise deadlock waiting for the refresh to finish before it can send Cease/HoldTimerExpired.
        // The token (default: the session's own _cts) bounds how long a management-API caller
        // (RefreshPeerAsync) blocks here — a prior send stuck on a slow peer previously pinned the
        // HTTP request thread indefinitely (#160).
        try
        {
            await _advertisedPrefixesLock.WaitAsync(ct);
        }
        catch (OperationCanceledException) { return; }
        catch (ObjectDisposedException)
        {
            // Session disposed while we were queued on the lock — mirror SendMessageAsync's handling
            // and unwind cleanly instead of letting ODE escape to the API caller (#160).
            return;
        }

        try
        {
            _logger.LogInformation("Refreshing routes for {Peer}", _peer);
            await WithdrawAllAsync();
            await SendAllRoutesAsync();
        }
        catch (OperationCanceledException) { /* shutdown / caller cancel — best effort */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh routes for {Peer}", _peer);
        }
        finally
        {
            try { _advertisedPrefixesLock.Release(); }
            catch (ObjectDisposedException) { /* session disposed — fine */ }
            catch (SemaphoreFullException) { /* double-release guard, shouldn't happen */ }
        }
    }

    private async Task WithdrawAllAsync()
    {
        var count = _advertisedPrefixes.Count;
        if (count == 0) return;

        const int maxPerUpdate = 100;
        // #85: reuse a single batch list instead of GetRange (which allocates a new List per batch).
        var batch = new List<IpPrefix>(Math.Min(maxPerUpdate, count));
        for (var i = 0; i < count; i += maxPerUpdate)
        {
            batch.Clear();
            var end = Math.Min(i + maxPerUpdate, count);
            for (var j = i; j < end; j++)
                batch.Add(_advertisedPrefixes[j]);
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
        IBgpConnection connection,
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
        ICommunityResolver? communityResolver = null,
        TimeProvider? timeProvider = null)
    {
        _connection = connection;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        _routeAssembler = new RouteAssembler(
            prefixService, _peerStore, _communityResolver, _routeFilter,
            _appConfig, _bgpConfig, _routeTable, logger, _peer);
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
                // OPEN timeout: cancel if the peer doesn't send OPEN within the configured window.
                // The timeout CTS uses _timeProvider (#96) so tests can advance the clock instead of
                // waiting wall-clock seconds. CancellationTokenSource(TimeSpan, TimeProvider) ctor is
                // the .NET 8+ TimeProvider-aware path (there is no CancelAfter(TimeSpan, TimeProvider)
                // instance overload, so we bake the timeout into the timer CTS directly).
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(openTimeoutSeconds), _timeProvider);
                using var openCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token, timeoutCts.Token);
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

        Interlocked.Exchange(ref _lastReceivedTicks, _timeProvider.GetUtcNow().Ticks);

        using var keepaliveTimer = new PeriodicTimer(_keepAliveInterval, _timeProvider);
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
            Interlocked.Exchange(ref _lastReceivedTicks, _timeProvider.GetUtcNow().Ticks);

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
                    var nowTicks = _timeProvider.GetUtcNow().Ticks;
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
                    await RefreshRoutesAsync(cancellationToken);
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
            if (_timeProvider.GetUtcNow().Ticks - Interlocked.Read(ref _lastReceivedTicks) >= holdTime.Ticks)
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

                UpdateCodec.ValidateMandatoryAttributes(originSeen, asPathSeen, nextHopSeen);
                asPath = UpdateCodec.MergeAsPathWithAs4Path(asPath, as4Path);
                UpdateCodec.ValidateAggregatorReconstruction(aggregatorAsn, as4AggregatorAsn);

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
                        // #85: guard the UintToIPAddress allocation behind IsEnabled — LogDebug
                        // evaluates the arg eagerly even when Debug is filtered out.
                        if (_logger.IsEnabled(LogLevel.Debug))
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

    /// <summary>
    /// Thin wrapper over <see cref="RouteAssembler.BuildOutboundRoutesAsync"/> (#93 Phase 2): resolves
    /// the per-peer route set, then delegates the aggregate + batch + send to <see cref="SendRoutesAsync"/>.
    /// The decision tree (RU defaults / subscriptions / custom prefixes / custom AS / user sources) and
    /// the outgoing filter live in RouteAssembler; the send/withdraw mirror stays here.
    /// </summary>
    private async Task SendAllRoutesAsync()
    {
        var nextHop = BgpConstants.IPAddressToUint(_bgpConfig.GetRouterIdAddress());
        var routes = await _routeAssembler.BuildOutboundRoutesAsync(
            _peerConfig.Address, _remoteAsn, GetFilterPeerConfig(), _cts.Token);
        if (routes.Count > 0)
            await SendRoutesAsync(nextHop, routes);
    }

    private async Task SendRoutesAsync(uint nextHop, List<Route> routes)
    {
        // Summarize before sending: merge adjacent/overlapping prefixes into the minimal
        // exact set (no extra IPs). Choke point for both initial send and RefreshRoutesAsync,
        // so _advertisedPrefixes stays consistent with what we later withdraw.
        var aggregated = _prefixAggregator.Aggregate(routes);

        // #209: merge duplicate NLRI across community groups. When the same prefix appears in
        // multiple sources (e.g. AWS and Cloudflare both announce it), the aggregator keeps them
        // separate (different community sets). A standard BGP router keeps one best path per NLRI,
        // so the second UPDATE for the same prefix is silently discarded — the peer loses the
        // community from the other source. Union the communities of duplicate prefixes into a
        // single route so the peer sees one UPDATE with ALL source communities.
        var deduped = MergeDuplicatePrefixes(aggregated);
        if (_logger.IsEnabled(LogLevel.Information) && (aggregated.Count != routes.Count || deduped.Count != aggregated.Count))
            _logger.LogInformation("Aggregated {Before} -> {Agg} -> {After} prefixes for {Peer}",
                routes.Count, aggregated.Count, deduped.Count, _peer);
        routes = deduped;

        const int maxNlriPerUpdate = 100;
        _advertisedPrefixes.EnsureCapacity(_advertisedPrefixes.Count + routes.Count);
        var sent = 0;
        var batch = new List<Route>(maxNlriPerUpdate);

        // Path attributes for a community set are byte-identical across every 100-NLRI batch of
        // a single send (localAsn/localFourByteAsn/nextHop are constant for the whole send), so
        // build them once per community set and reuse instead of rebuilding on each batch (#87).
        // Scoped to this send only: the cache dies with the dictionary, so it can never serve a
        // later send that carries a different nextHop or renegotiated ASN.
        var attrCache = UpdateCodec.CreateUpdateAttributeCache();

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

    /// <summary>
    /// Merges routes that share the same (Prefix, PrefixLength) by unioning their communities and
    /// large communities into a single route (#209). Without this, a prefix present in two sources
    /// (e.g. AWS and Cloudflare) is sent as two separate UPDATEs with different communities — but a
    /// BGP router keeps only one best path per NLRI, silently discarding the second UPDATE and its
    /// community. After merging, the peer sees one UPDATE per prefix with ALL source communities.
    /// </summary>
    private static List<Route> MergeDuplicatePrefixes(IReadOnlyList<Route> routes)
    {
        if (routes.Count <= 1) return routes as List<Route> ?? routes.ToList();

        var merged = new Dictionary<(uint Prefix, byte Length), Route>(routes.Count);
        foreach (var route in routes)
        {
            var key = (route.Prefix, route.PrefixLength);
            if (merged.TryGetValue(key, out var existing))
            {
                // Union communities — keep both source tags so the peer can filter by either.
                var comms = existing.Communities.Concat(route.Communities).Distinct().OrderBy(c => c).ToArray();
                var large = existing.LargeCommunities.Concat(route.LargeCommunities).Distinct().ToArray();
                // Route is a class (init-only props), not a record — mutate via reassignment.
                merged[key] = new Route
                {
                    Prefix = existing.Prefix,
                    PrefixLength = existing.PrefixLength,
                    NextHop = existing.NextHop,
                    AsPath = existing.AsPath,
                    Communities = comms,
                    LargeCommunities = large
                };
            }
            else
            {
                merged[key] = route;
            }
        }
        return [.. merged.Values];
    }

    private async Task SendRouteBatchAsync(uint nextHop, List<Route> routes, Dictionary<uint[], List<PathAttribute>> attrCache)
    {
        // The COMMUNITY/LARGE_COMMUNITY path attributes apply to EVERY NLRI in an UPDATE, so
        // partition the batch by (community set, large-community set) and emit one UPDATE per
        // set. Otherwise prefixes belonging to one group would be tagged with another group's
        // communities on the wire.
        foreach (var groupRoutes in GroupByCommunitySet(routes))
        {
            var attrs = UpdateCodec.GetCachedUpdateAttributes(_bgpConfig.Asn, _localFourByteAsn, nextHop, groupRoutes[0].Communities, attrCache);
            // LARGE_COMMUNITY is appended per group AFTER fetching the cached base attrs, so the
            // #87 cache stays keyed by regular communities only and is never mutated. Routes that
            // share regular communities but differ in large communities reuse the same base attrs
            // and only diverge in this final attribute.
            attrs = UpdateCodec.WithLargeCommunityAttribute(attrs, groupRoutes[0].LargeCommunities);

            var nlri = groupRoutes.Select(r => new IpPrefix(r.Prefix, r.PrefixLength)).ToList();
            await SendUpdateBatchAsync(attrs, nlri);
        }
    }

    /// <summary>
    /// Partitions routes into groups that share an identical (regular + large) community set,
    /// so each emitted UPDATE carries a single COMMUNITY and a single LARGE_COMMUNITY attribute.
    /// Delegates to <see cref="RouteAssembler.GroupByCommunitySet"/> (#93 Phase 2).
    /// </summary>
    private static List<List<Route>> GroupByCommunitySet(IReadOnlyList<Route> routes)
        => RouteAssembler.GroupByCommunitySet(routes);

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
    // Returns true if the message was fully written; false if the send was cancelled (e.g. the
    // shutdown grace elapsed) or the session was disposed mid-send — callers that need accurate
    // teardown logging (NotifyCeaseAsync) branch on this instead of assuming success.
    private async Task<bool> SendMessageAsync(BgpMessage message, CancellationToken ct = default)
    {
        try
        {
            await _sendLock.WaitAsync(ct);
        }
        catch (OperationCanceledException) { return false; }
        catch (ObjectDisposedException)
        {
            return false;
        }

        try
        {
            var bufferSize = BgpMessageWriter.GetBufferSize(message);
            var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                var written = BgpMessageWriter.WriteMessage(message, buffer);
                await _connection.WriteAsync(buffer.AsMemory(0, written), ct);
                return true;
            }
            catch (OperationCanceledException)
            {
                // Caller cancelled the send (e.g. shutdown grace expired) — best effort during teardown.
                return false;
            }
            catch (ObjectDisposedException)
            {
                // Session disposed mid-send — best effort during teardown.
                return false;
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

    // #96: delegates to the transport seam. The loop-to-fill + EOF→IOException semantics now live
    // in IBgpConnection (SocketBgpConnection / fakes), preserving the exact contract the FSM relies on.
    private Task ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => _connection.ReadExactAsync(buffer, cancellationToken).AsTask();

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
        // Graceful Restart capability (RFC 4724 §2). R=0 on a fresh session — the R bit means
        // "I am restarting, please retain my routes" and must NOT be set on the initial session
        // establishment. It would only be set if BGPLite were recovering from a crash/restart and
        // wanted peers to re-send their routes. BGPLite always re-advertises its full route set on
        // reconnect, so R=0 is correct (#203). Restart Time tells peers how long to retain stale
        // routes if BGPLite disappears (silent TCP close during docker stop). F reflects whether
        // forwarding state is preserved (configurable, default false). Advertised unconditionally
        // when enabled (RFC 4724 §4; non-GR peers safely ignore it per RFC 5492).
        if (_bgpConfig.GracefulRestart)
        {
            var restartTime = (ushort)Math.Min(_bgpConfig.RestartTime, _negotiatedHoldTime > 0 ? _negotiatedHoldTime : 120);
            capabilities.Add(BgpCapabilityInfo.GracefulRestart(
                restartState: false, restartTime, forwardingState: _bgpConfig.GracefulRestartForwardingState));
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

    private Task<bool> SendNotificationAsync(byte errorCode, byte subErrorCode, CancellationToken ct = default)
        => SendNotificationAsync(errorCode, subErrorCode, null, ct);

    private async Task<bool> SendNotificationAsync(byte errorCode, byte subErrorCode, byte[]? data, CancellationToken ct = default)
    {
        var notification = new BgpNotificationMessage
        {
            ErrorCode = errorCode,
            SubErrorCode = subErrorCode,
            Data = data is null ? null : (byte[])data.Clone()
        };
        var sent = await SendMessageAsync(notification, ct);
        if (sent)
            _logger.LogInformation("NotificationSent to {Peer}: {Error}/{SubError}", _peer, errorCode, subErrorCode);
        return sent;
    }

    /// <summary>
    /// Best-effort Cease NOTIFICATION for graceful shutdown (RFC 4271 §6.2). The caller (BgpServer)
    /// should only invoke this on an Established session and only when Graceful Restart is disabled —
    /// a NOTIFICATION termination bypasses GR (RFC 4724 §4), so with GR on we drop the TCP connection
    /// instead to let peers retain our routes. Write/IO errors are swallowed (we are shutting down).
    /// Accepts a <see cref="CancellationToken"/> so the host's shutdown grace can bound how long a
    /// single Cease send blocks (a slow/stuck peer otherwise pins the send lock indefinitely).
    /// </summary>
    public async Task NotifyCeaseAsync(CancellationToken ct = default)
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
            var sent = await SendNotificationAsync(BgpConstants.Error.Cease, BgpConstants.SubError.CeaseAdministrativeReset, ct);
            if (sent)
                _logger.LogInformation("Cease sent to {Peer} on shutdown", _peer);
            else
                // Cancellation (host grace elapsed) or session disposed mid-send — best effort during
                // teardown; the socket close below is the ultimate signal to the peer.
                _logger.LogDebug("Cease to {Peer} on shutdown did not complete (cancelled or disposed)", _peer);
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

        var malformedFourOctetAsnCapability = UpdateCodec.GetMalformedFourOctetAsnCapabilityData(open);
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
        _connection.Dispose();   // owns the socket (SocketBgpConnection wraps NetworkStream ownsSocket:true)
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
