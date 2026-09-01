using System.Buffers;
using System.Net;
using System.Net.Sockets;
using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Routing;
using Microsoft.Extensions.Logging;
using BGPLite.Contracts;

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
    private readonly Func<string, uint, CancellationToken, Task>? _onPeerIdentified;
    // #391: the EFFECTIVE per-peer prefix ceiling — the peer row's MaxPrefix override when the
    // peer is configured, else the global Bgp.MaxPrefixesPerPeer. Resolved once per
    // establish/refresh cycle in SendAllRoutesAsync (never per UPDATE); read on the UPDATE path
    // via Volatile.Read. Int semantics: 0 = unlimited, > 0 = the cap.
    private int _effectiveMaxPrefix;
    private readonly IPeerStore? _peerStore;
    // #265 item 1: set by BgpServer right after creation — "is this session still the registered
    // one?" A false answer at teardown-time means a replacement took the slot and owns the
    // (Ip, Asn) status now; this session must not overwrite it back to inactive.
    private Func<BgpSession, bool>? _stillRegisteredProbe;
    internal Func<BgpSession, bool>? StillRegisteredProbe { set => _stillRegisteredProbe = value; }
    private readonly IPrefixAggregator _prefixAggregator;
    // #93 Phase 2: the outbound route-assembly policy lives here, not in the session. The session
    // delegates to BuildOutboundRoutesAsync and keeps the send/withdraw mirror (_advertisedPrefixes)
    // and the codec glue (SendRoutesAsync). #263: injected rather than constructed here — the
    // session no longer carries the assembler's own dependencies (prefix service, AppConfig,
    // community resolver) just to hand them on.
    private readonly IRouteAssembler _routeAssembler;

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
    // #304: distinct NLRI this session currently owns in the shared table — drives the per-peer
    // prefix cap (Bgp.MaxPrefixesPerPeer) and its 75% warning. #377 review: ConcurrentDictionary,
    // not HashSet — ownership can be taken over by ANOTHER session's install (the
    // RouteTable.EntryOwnershipLost handler below runs on that session's thread), and the
    // per-announce cap check reads the count on this session's read loop.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<IpPrefix, byte> _installedPrefixes = new();
    private bool _maxPrefixesWarned;
    // #212: actual count sent on the wire (after aggregation + dedup). Updated at the end of
    // SendRoutesAsync. Read via AdvertisedPrefixCount for the management API/UI so operators see
    // the real number their peer's router receives, not the raw pre-aggregation count.
    private int _advertisedCount;
    private TimeSpan _keepAliveInterval;
    private long _lastReceivedTicks; // UTC ticks of last received message; drives the HoldTimer (Interlocked)
    // Debounce ROUTE_REFRESH (RFC 2918): rate-limit per-session route re-announcements to avoid
    // DoS where a peer spams type-5 and forces a full re-advertise. Initial 0 = never refreshed.
    // Read/written via Interlocked so RefreshRoutesAsync and ReadLoopAsync can't race.
    private long _lastRouteRefreshTicks;
    // OpenConfirm bound when the negotiated hold time is 0 (#286). RFC 4271 §4.2 disables the Hold
    // Timer at 0, but §8.2.2 also gives OpenSent a "large value" initial Hold Time with a suggested
    // 4 minutes — a handshake that never completes is not the same thing as an established session
    // that deliberately runs without timers, and leaving it unbounded is the resource hole this
    // constant closes. The Established phase still honors hold time 0 as "no timer".
    private static readonly TimeSpan OpenConfirmFallbackHoldTime = TimeSpan.FromMinutes(4);
    // Minimum gap between peer-triggered route refreshes. 1s is a reasonable default:
    // long enough to make flood-DoS impractical, short enough that a legitimate peer retry
    // after a lost UPDATE still gets a fresh advertisement promptly.
    private static readonly TimeSpan MinRouteRefreshInterval = TimeSpan.FromSeconds(1);

    public BgpFsmState State => _state;
    public PeerConfig Peer => _peerConfig;
    /// <summary>The remote ASN negotiated from the peer's OPEN (#206). Set after ValidateOpen; used by
    /// BgpServer.RefreshPeerAsync to filter sessions by (Ip, Asn) on shared IPs.</summary>
    public uint RemoteAsn => _remoteAsn;
    /// <summary>Actual prefix count sent on the wire (post-aggregation, post-dedup). 0 = never sent.</summary>
    public int AdvertisedPrefixCount => Volatile.Read(ref _advertisedCount);
    public bool IsEstablished => _state == BgpFsmState.Established;

    public async Task RefreshRoutesAsync(CancellationToken ct = default)
    {
        // #254: default(CancellationToken) is CancellationToken.None — NOT "the session's own _cts"
        // the previous comment claimed. Normalize so every token-less caller (management API
        // RefreshPeerAsync / RefreshAllEstablishedAsync, onSourceChanged) has its refresh cancelled
        // at session teardown instead of outliving the session.
        if (ct == default)
        {
            CancellationToken sessionToken;
            try { sessionToken = _cts.Token; }
            catch (ObjectDisposedException) { return; } // session disposed — nothing to refresh
            ct = sessionToken;
        }

        if (!IsEstablished) return;

        // #254 debounce: N stacked triggers must not produce N sequential full withdraw+re-announce
        // dumps on the wire. One cycle runs; requests arriving mid-cycle set _refreshPending and
        // return immediately — the runner's do/while coalesces them into a single extra lap, so the
        // worst case is one in-flight cycle + one pending lap regardless of trigger count.
        if (Interlocked.CompareExchange(ref _refreshRunning, 1, 0) != 0)
        {
            _refreshPending = true;
            return;
        }

        try
        {
            do
            {
                _refreshPending = false;
                await RefreshCycleAsync(ct);
            } while (_refreshPending && !ct.IsCancellationRequested);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshRunning, 0);
        }
    }

    private async Task RefreshCycleAsync(CancellationToken ct)
    {
        // _sendLock is acquired inside SendMessageAsync, so each individual UPDATE is atomic on the
        // wire. _advertisedPrefixesLock serializes the (withdraw + re-announce) pair against the
        // initial-send, which mutates the same list concurrently. We do NOT hold _sendLock across
        // the whole pair: a HoldTimer expiry or peer NOTIFICATION that arrives between them would
        // otherwise deadlock waiting for the refresh to finish before it can send Cease/HoldTimerExpired.
        // The token (normalized to the session's own _cts by RefreshRoutesAsync) bounds how long a
        // management-API caller (RefreshPeerAsync) blocks here — a prior send stuck on a slow peer
        // previously pinned the HTTP request thread indefinitely (#160).
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
        catch (IOException ex)
        {
            // #285: the outbound byte stream is in an unknown state. A send either failed outright
            // or was aborted by the per-send budget AFTER the kernel had accepted part of the frame,
            // leaving the peer mid-frame. Swallowing this (the previous generic catch) kept the
            // session Established on a stream where every later frame is read by the peer as the
            // truncated frame's payload — silent route corruption with both sides reporting a
            // healthy session. Tear down instead, matching HoldTimerLoopAsync's handling of a failed
            // KEEPALIVE send. No NOTIFICATION is attempted: the peer is either not reading or the
            // stream is already corrupt, so it would only block for another budget window.
            _logger.LogWarning(ex, "Route refresh to {Peer} failed on the wire — tearing down the session", _peer);
            FaultSession();
        }
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

    /// <summary>
    /// Tears the session down after an unrecoverable outbound failure (#285). Latches
    /// <see cref="TeardownReason.LocalCease"/> so the <c>RunAsync</c> finally-block emits no
    /// NOTIFICATION — RFC 4271 §8.1 allows exactly one per teardown, and here the right number is
    /// zero because the wire is not usable — then cancels the session CTS so the read/keepalive
    /// loops unwind promptly instead of waiting on the peer or the hold timer. Unlike
    /// <see cref="HoldTimerLoopAsync"/>, which can simply return and let
    /// <see cref="RunEstablishedAsync"/>'s <c>Task.WhenAny</c> cancel, a refresh runs off the
    /// session's own loops (background ROUTE_REFRESH task or the management API), so the cancel
    /// must be explicit.
    /// </summary>
    private void FaultSession()
    {
        Interlocked.CompareExchange(ref _teardownReason, (int)TeardownReason.LocalCease, (int)TeardownReason.None);
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* session already disposed — nothing to unwind */ }
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
        Volatile.Write(ref _advertisedCount, 0); // #212: routes withdrawn — no longer advertised
    }

    public BgpSession(
        IBgpConnection connection,
        PeerConfig peerConfig,
        BgpConfig bgpConfig,
        RouteTable routeTable,
        IRouteFilter routeFilter,
        BgpMetrics metrics,
        ILogger<BgpSession> logger,
        Func<string, uint, CancellationToken, Task>? onPeerIdentified = null,
        IPeerStore? peerStore = null,
        IPrefixAggregator? prefixAggregator = null,
        IRouteAssembler? routeAssembler = null,
        TimeProvider? timeProvider = null)
    {
        _connection = connection;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _peerConfig = peerConfig;
        _peer = peerConfig.ToString();
        _bgpConfig = bgpConfig;
        _effectiveMaxPrefix = bgpConfig.MaxPrefixesPerPeer;
        _routeTable = routeTable;
        _routeFilter = routeFilter;
        _metrics = metrics;
        _logger = logger;
        _onPeerIdentified = onPeerIdentified;
        _peerStore = peerStore;
        _prefixAggregator = prefixAggregator ?? new ExactUnionPrefixAggregator();
        // #263: no assembler supplied means no per-peer configuration is reachable. That is a real
        // (test-only) composition, so it gets a real, named implementation that says so out loud —
        // not a RouteAssembler quietly holding nulls.
        _routeAssembler = routeAssembler ?? new SharedTableRouteAssembler(_routeTable, _routeFilter, logger);

        // #377 review: when another session takes over a key this one installed, drop it from the
        // per-peer prefix set — otherwise the cap count drifts upward on overlapping NLRI and can
        // trip a reset for prefixes this session no longer owns. Any thread; remove-if-present.
        _routeTable.EntryOwnershipLost += OnEntryOwnershipLost;
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

            await ValidateOpenAsync(remoteOpen, linkedCts.Token);

            TransitionTo(BgpFsmState.OpenSent);

            // Send our OPEN — adapt capabilities to peer
            await SendOpenAsync(remoteOpen);
            _logger.LogInformation("OpenSent to {Peer}", _peer);

            // Send KEEPALIVE (acknowledge OPEN)
            await SendKeepaliveAsync();
            _logger.LogDebug("KeepAliveSent to {Peer} (OPEN confirm)", _peer);

            TransitionTo(BgpFsmState.OpenConfirm);

            // Receive KEEPALIVE, bounded by the OpenConfirm hold timer (#286). RFC 4271 §8.2.2 runs
            // the Hold Timer in OpenSent/OpenConfirm as well, with the negotiated value once the
            // OPEN exchange has happened. Without it this read was unbounded: #115's
            // OpenTimeoutSeconds only covers the read that RECEIVES the OPEN, and the keepalive/hold
            // loop does not start until RunEstablishedAsync — so a peer that sent a well-formed OPEN
            // and then went silent pinned a session, a socket FD and a task indefinitely, walking
            // straight past the Slowloris defence (it is not slow; it completes the OPEN and stops).
            var confirmHoldTime = _negotiatedHoldTime > 0
                ? TimeSpan.FromSeconds(_negotiatedHoldTime)
                : OpenConfirmFallbackHoldTime;

            BgpMessage response;
            using (var confirmTimeoutCts = new CancellationTokenSource(confirmHoldTime, _timeProvider))
            using (var confirmCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token, confirmTimeoutCts.Token))
            {
                try
                {
                    response = await ReceiveMessageAsync(confirmCts.Token);
                }
                catch (OperationCanceledException) when (!linkedCts.IsCancellationRequested)
                {
                    // Only the OpenConfirm hold timer fired (the external/session token is still
                    // alive). RFC 4271 §8.2.2, OpenConfirm + HoldTimer_Expires: send a NOTIFICATION
                    // with Hold Timer Expired, release resources, go to Idle. Unlike the OPEN
                    // timeout above we DO notify — the peer completed the OPEN exchange, so it is
                    // reading the socket and the diagnostic reaches its operator.
                    _logger.LogWarning(
                        "Hold timer expired for {Peer} in OpenConfirm (no KEEPALIVE within {Hold}s) — closing (#286)",
                        _peer, confirmHoldTime.TotalSeconds);
                    if (Interlocked.CompareExchange(ref _teardownReason, (int)TeardownReason.HoldTimerExpired, (int)TeardownReason.None) == (int)TeardownReason.None)
                    {
                        try { await SendNotificationAsync(BgpConstants.Error.HoldTimerExpired, BgpConstants.SubError.Unspecific); }
                        catch { /* best-effort — partial write counts, see RFC 4271 §8.1 */ }
                    }
                    return;
                }
            }

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
            // #223: emit the RFC 4271 §6 error code the parser recorded (Open/Update for a body
            // failure, MessageHeaderError for a fixed-header failure). Defaults to MessageHeaderError
            // when the parser did not specify one (e.g. marker/length/type validation in ReadMessage).
            if (Interlocked.CompareExchange(ref _teardownReason, (int)TeardownReason.LocalCease, (int)TeardownReason.None) == (int)TeardownReason.None)
            {
                // #300: the parser may also supply the NOTIFICATION Data field — RFC 4271 §6.1
                // requires the erroneous Length for Bad Message Length and the erroneous Message
                // Type for Bad Message Type, so the peer's operator sees what was wrong.
                try { await SendNotificationAsync(ex.ErrorCode ?? BgpConstants.Error.MessageHeaderError, ex.SubErrorCode ?? BgpConstants.SubError.Unspecific, ex.NotificationData); }
                catch { /* best-effort */ }
            }
        }
        catch (IOException ex)
        {
            // Peer closed the TCP connection (EOF) — the single most common teardown cause. State the
            // FSM phase explicitly so the operator sees WHY the session never established: a peer that
            // connects and drops the socket before sending OPEN otherwise surfaces as a generic Error
            // with a stack trace, hiding the (peer-side) root cause. Warning, not Error: a network
            // close is a normal event, not a server fault (AGENTS.md: "treat partial failure as normal
            // for network operations"). Stack trace demoted to Debug. _state is volatile, safe to read
            // here. The Established case covers the window between TransitionTo(Established) and
            // RunEstablishedAsync (initial route dump / End-of-RIB); once the read loop is running,
            // Established-phase closes are logged inside ReadLoopAsync (#217).
            var phase = _state switch
            {
                BgpFsmState.Connect => "before sending OPEN",
                BgpFsmState.OpenSent => "while sending local OPEN/KEEPALIVE (OpenSent)",
                BgpFsmState.OpenConfirm => "during OPEN/KEEPALIVE handshake (OpenConfirm)",
                BgpFsmState.Established => "while sending initial routes (Established)",
                _ => $"in state {_state}"
            };
            _logger.LogWarning("Peer {Peer} closed the TCP connection {Phase}", _peer, phase);
            _logger.LogDebug(ex, "IOException details for {Peer}", _peer);
            // Best-effort Cease so the peer sees a clean close instead of a bare TCP RST.
            // CAS from None: if a concurrent silent close / peer NOTIFICATION already claimed the
            // teardown, do NOT emit a NOTIFICATION (RFC 4724 §4 / RFC 4271 §6.3 / §8.1).
            if (Interlocked.CompareExchange(ref _teardownReason, (int)TeardownReason.LocalCease, (int)TeardownReason.None) == (int)TeardownReason.None)
            {
                try { await SendNotificationAsync(BgpConstants.Error.Cease, BgpConstants.SubError.Unspecific); }
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

            // RFC 4271 §8.2.2: every transition out of Established "deletes all routes associated
            // with this connection". #313: nothing did. A peer's announcements outlived its session
            // forever — no other path in the server removes an entry — so a peer could disconnect,
            // reconnect and add another batch without limit, and both GET /api/routes and the route
            // count kept reporting peers that were long gone. Unconditional, not gated on
            // wasEstablished: a session that installed nothing removes nothing, and the guard would
            // only be a hole if the FSM ever grew another way to install routes.
            //
            // Not GR-exempt. RFC 4724 lets a receiver RETAIN a restarting peer's routes as stale for
            // its advertised Restart Time and flush them when it expires; BGPLite implements no part
            // of that (HandleOpen only logs the peer's GR capability), and retention without the
            // timer is not Graceful Restart, it is the leak.
            var flushed = _routeTable.RemoveAllOwnedBy(this);
            if (flushed > 0)
            {
                _logger.LogInformation("Removed {Count} route(s) learned from {Peer} on session close", flushed, _peer);
                _metrics.SetRouteCount(_routeTable.Count);
            }

            if (wasEstablished)
            {
                _metrics.SessionClosed();
                // Best-effort, like SendNotificationAsync above: this sync DB write runs in the
                // finally of a fire-and-forget task (RunSessionAsync has no catch), so a transient
                // store failure (SQLite "database is locked" past busy_timeout) must not escape —
                // it would fault RunAsync unobserved, skip PeerDisconnected() below, and leak
                // PeerCount plus the row's Status=active forever (#325).
                // #265 item 1: the write must not clobber a REPLACEMENT session's Status=active.
                // Two guards: (a) SilentClose teardowns (session replacement, GR-aware shutdown —
                // RFC 4724 §4) skip the write outright; (b) when a registration probe is wired
                // (BgpServer), a session no longer present in the registry was replaced mid-unwind
                // and also skips. Covers the slow-unwind race (e.g. a sender parked on _sendLock
                // inside the #285 budget) whose finally runs after the new session's first
                // LoadPeerRoutingView already wrote active.
                var silent = (TeardownReason)Interlocked.CompareExchange(ref _teardownReason, 0, 0) == TeardownReason.SilentClose;
                var stillRegistered = _stillRegisteredProbe?.Invoke(this) ?? true;
                if (!silent && stillRegistered)
                {
                    if (_peerStore is not null)
                        try { await _peerStore.UpdateSessionStatusAsync(_peerConfig.Address, _remoteAsn, false); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist session status for {Peer}", _peer); }

                    // #366 review: a replacement can land between the probe and the write —
                    // re-probe and REPAIR. A false second probe means the registry swapped us out
                    // mid-write and the replacement owns the (Ip, Asn): restore the row to active.
                    // Idempotent with the replacement's own LoadPeerRoutingView write, and
                    // race-free in the other direction — the own-runner's registry removal happens
                    // only AFTER RunAsync returns, so during this finally a false probe can only
                    // mean replacement. Genuine teardowns (still registered) never repair.
                    if (_stillRegisteredProbe?.Invoke(this) == false)
                    {
                        _logger.LogInformation("Replacement detected after status write for {Peer} — restoring active", _peer);
                        if (_peerStore is not null)
                            try { await _peerStore.UpdateSessionStatusAsync(_peerConfig.Address, _remoteAsn, true); }
                            catch (Exception ex) { _logger.LogWarning(ex, "Failed to restore session status for {Peer}", _peer); }
                    }
                }
            }
            _metrics.PeerDisconnected();
            _logger.LogInformation("SessionClosed with {Peer}", _peer);
        }
    }

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    // #341: set in Dispose() BEFORE _sendLock.Dispose() — SemaphoreSlim.Dispose never wakes
    // queued waiters, so sends parked on _sendLock race their wait against this signal
    // (SendMessageAsync) and unwind as "not sent" instead of hanging RunAsync forever.
    private readonly TaskCompletionSource _sendLockDisposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Guards mutations of _advertisedPrefixes so initial-send and RefreshRoutesAsync can't interleave.
    // SemaphoreSlim instead of lock{} so it composes correctly with await.
    private readonly SemaphoreSlim _advertisedPrefixesLock = new(1, 1);

    // #254 refresh debounce: 0 = idle, 1 = a refresh cycle is executing; late requesters set
    // _refreshPending and the running cycle performs one coalesced extra lap for them.
    private int _refreshRunning;
    private volatile bool _refreshPending;

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
        // Read-loop IOException is now logged and swallowed inside ReadLoopAsync (#217), so it never
        // surfaces here as a faulting task; only genuine loop faults reach the generic catch.
        // #223: a fixed-header BgpParseException (ErrorCode == null — invalid marker/length/type)
        // MUST propagate to RunAsync's catch(BgpParseException) so the right NOTIFICATION
        // (MessageHeaderError) is emitted before teardown. Without this rethrow the generic catch
        // below would swallow it and the finally-block would send a generic Cease(6,0) instead —
        // a regression of #223 for the HoldTime > 0 path (HoldTime == 0 awaits ReadLoopAsync
        // directly and already propagates).
        try { await task; }
        catch (OperationCanceledException) { }
        catch (BgpParseException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "{Label} loop faulted for {Peer}", label, _peer); }
    }

    /// <summary>
    /// Logs the explicit Established-phase TCP-close diagnostic. Used from <see cref="ReadLoopAsync"/>
    /// both on a direct <see cref="IOException"/> (EOF) and on an
    /// <see cref="OperationCanceledException"/> that masks an EOF under the EOF↔cancel race (#217,
    /// dotnet/runtime #16025). Centralised so the message is byte-identical in both branches.
    /// Warning, not Error: a network close is a normal event, not a server fault (AGENTS.md:
    /// "treat partial failure as normal for network operations"). The stack trace, when present,
    /// is demoted to Debug.
    /// </summary>
    private void LogPeerClosedEstablished(Exception? ioException)
    {
        _logger.LogWarning("Peer {Peer} closed the TCP connection during Established", _peer);
        if (ioException is not null)
            _logger.LogDebug(ioException, "IOException details for {Peer}", _peer);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            BgpMessage message;
            try
            {
                message = await ReceiveMessageAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Cancellation can be (a) pure cancel — hold-timer expiry / external shutdown / graceful
                // teardown — while the peer is still connected, OR (b) the EOF↔cancel race where the
                // peer closed the TCP connection in the same window the token was cancelled. Under the
                // race, .NET may surface OperationCanceledException even though the kernel has already
                // processed the FIN (dotnet/runtime #16025, non-deterministic). Probe the transport to
                // tell them apart: if the peer closed, log the explicit Established-phase cause here
                // (the only deterministic place — AwaitLoopTaskAsync sees only the masked OCE). #217.
                if (_connection.IsPeerClosed)
                    LogPeerClosedEstablished(null);
                throw;
            }
            catch (IOException ex)
            {
                // Direct EOF read (no concurrent cancellation): explicit cause + Debug stack. This is
                // the deterministic path; the OCE-branch above covers the race. #217.
                // Return (not throw): the explicit cause is already logged here, and RunEstablishedAsync
                // routes hold-time>0 reads through AwaitLoopTaskAsync, but hold-time==0 (RFC 4271 §4.2/§6.5)
                // awaits ReadLoopAsync directly — a re-thrown IOException would propagate to RunAsync's
                // catch(IOException) and produce a SECOND, generic "in state Established" line. Returning
                // exits the loop cleanly so the finally-block Cease runs exactly once and the diagnostic
                // is emitted exactly once, for both hold-time paths.
                LogPeerClosedEstablished(ex);
                return;
            }
            catch (BgpParseException ex) when (ex.ErrorCode is not null)
            {
                // #222: a malformed message BODY (truncated path attribute, out-of-range NLRI length,
                // truncated OPEN/UPDATE) is a per-message content error, not stream-level corruption.
                // RFC 7606 §2 / RFC 4271 §6.3 say: discard the bad message and KEEP THE SESSION up,
                // reserving teardown for stream-level errors (FSM / message-header). The previous
                // behavior let the exception escape the read loop → AwaitLoopTaskAsync logged
                // "read loop faulted" → RunAsync finally sent a generic Cease(6,0), tearing down a
                // long-lived session over a single bad/adversarial UPDATE.
                //
                // The `when (ex.ErrorCode is not null)` filter is critical: only body-parse failures
                // (ParseOpen → Open Message Error, ParseUpdate/ParseAttribute/PrefixCodec → Update
                // Message Error) set ErrorCode. Fixed-header failures (invalid marker/length/type from
                // ReadMessage/ReceiveMessageAsync) leave ErrorCode == null and are NOT caught here —
                // they propagate to RunAsync, which tears down the session with NOTIFICATION
                // (MessageHeaderError). This preserves RFC 4271 §6.1 (stream-level corruption MUST
                // tear down) AND avoids a desync hazard: a length-out-of-range from ReceiveMessageAsync
                // is thrown AFTER the 19-byte header is read but BEFORE the payload, so continuing here
                // would read payload bytes as the next header and desync the stream permanently.
                // (Mirrors the existing treat-as-withdraw catch in HandleUpdateAsync dispatch at :508,
                //  which only covers BgpNotificationException thrown AFTER successful parsing.)
                //
                // No NOTIFICATION is sent: RFC 7606 treat-as-withdraw keeps the session up without
                // notifying (RFC 4271 §6.3 mandates the receiver of a NOTIFICATION tear down, which
                // would defeat the point of preserving the session).
                //
                // Note this branch cannot apply true treat-as-withdraw and does not claim to: the
                // frame failed to parse, so the NLRI list is not recoverable and there is nothing to
                // remove (contrast HandleUpdateAsync, where the NLRI IS known — #288). RFC 7606 §3(j)
                // says that when the NLRI field cannot be parsed, "the 'session reset' approach ...
                // MUST be followed"; BGPLite deliberately discards and keeps the session instead,
                // because resetting on a single malformed frame is precisely the remote-DoS lever
                // #222/#284 closed. Deviation recorded here rather than silently implied.
                _metrics.UpdateRejected();
                _logger.LogWarning(
                    "Rejected malformed message from {Peer}: {Error}/{SubError} — {Reason}; session stays up",
                    _peer, ex.ErrorCode, ex.SubErrorCode ?? BgpConstants.SubError.Unspecific, ex.Message);
                continue;
            }
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
                    // #253: run the re-announcement OFF the read loop. A refresh is a full
                    // withdraw + re-announce (plus network fetches on a cold TTL cache) — awaiting
                    // it inline starved this loop: the peer's KEEPALIVEs sat unread in the socket
                    // buffer and a completely live session was killed by a false Hold Timer
                    // Expired. Fire-and-forget: stacking is bounded by the CAS rate-limit above
                    // and coalesced by the RefreshRoutesAsync debounce (one in-flight cycle + one
                    // pending lap — a refresh slower than the rate-limit window does NOT queue
                    // further full dumps); faults log instead of tearing down the read loop.
                    _ = Task.Run(() => RefreshInBackgroundAsync(cancellationToken), CancellationToken.None);
                    break;

                default:
                    // RFC 4271 FSM: in Established only UPDATE, KEEPALIVE, NOTIFICATION and
                    // ROUTE_REFRESH are legal inputs; anything else (e.g. an OPEN) is an FSM
                    // error — NOTIFICATION 5/0, release resources, Idle. Previously such messages
                    // were silently swallowed (#265 item 3). The CAS claims the teardown BEFORE
                    // sending so the finally-block cannot double-emit a Cease (§8.1).
                    _logger.LogWarning("Unexpected message {Type} from {Peer} in Established — FSM error",
                        message.Type, _peer);
                    if (Interlocked.CompareExchange(ref _teardownReason, (int)TeardownReason.LocalCease, (int)TeardownReason.None) == (int)TeardownReason.None)
                    {
                        try { await SendNotificationAsync(BgpConstants.Error.FiniteStateMachineError, BgpConstants.SubError.Unspecific); }
                        catch { /* best-effort — partial write counts, see RFC 4271 §8.1 */ }
                    }
                    return;
            }
        }
    }

    /// <summary>
    /// #253: fire-and-forget wrapper for the read loop's ROUTE_REFRESH handling — the loop must
    /// keep reading (KEEPALIVEs feed the hold timer) while the refresh runs. RefreshRoutesAsync
    /// already swallows its own errors; this wrapper only guards against the unexpected.
    /// </summary>
    private async Task RefreshInBackgroundAsync(CancellationToken ct)
    {
        try
        {
            await RefreshRoutesAsync(ct);
        }
        catch (OperationCanceledException) { /* teardown — fine */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background refresh faulted for {Peer}", _peer);
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

            // #252: a failed/timed-out send (per-send budget in SocketBgpConnection) means the
            // outbound path is dead — most commonly a peer that stopped reading. Claim the
            // teardown and end this loop: Task.WhenAny in RunEstablishedAsync then cancels the
            // read loop and the session unwinds (no NOTIFICATION is attempted — the peer is not
            // reading by definition, so it would only block for another budget window).
            try
            {
                await SendKeepaliveAsync();
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Keepalive send to {Peer} failed/timed out — tearing down", _peer);
                Interlocked.CompareExchange(ref _teardownReason, (int)TeardownReason.LocalCease, (int)TeardownReason.None);
                return;
            }
            _logger.LogDebug("KeepAliveSent to {Peer}", _peer);
        }
    }

    private async Task HandleUpdateAsync(BgpUpdateMessage update)
    {
        // #344: per-UPDATE at Debug — a full-table dump sends hundreds-to-thousands of UPDATEs and
        // a flap storm floods the log pipeline on the read loop, drowning the Warning-level
        // signals; the per-dump aggregation summaries one level up stay at Information.
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("UpdateReceived from {Peer}: {Withdrawn} withdrawn, {Nlri} announced",
                _peer, update.WithdrawnRoutes.Count, update.Nlri.Count);

        // Process withdrawals. RFC 4271 §3.2/§9: a withdrawal removes the route received FROM THIS
        // PEER, not whatever happens to sit at that prefix. BGPLite has one shared RouteTable rather
        // than per-peer Adj-RIBs-In, so ownership is tracked here (#289) — otherwise any peer that
        // completed a handshake could delete prefixes seeded at startup by RouteSeedingService or
        // announced by another peer, simply by listing them as withdrawn.
        foreach (var w in update.WithdrawnRoutes)
            WithdrawIfOwned(w, "Route withdrawn");

        // Process announcements
        if (update.Nlri.Count > 0)
        {
            // #270: the inbound attribute pipeline (per-attribute validation, mandatory set,
            // AS4_PATH reconstruction, aggregator consistency — subcodes 3/6/8/9/11) lives in the
            // protocol library. Its BgpNotificationException propagates to the treat-as-withdraw
            // catch in the dispatch loop, which logs and counts it.
            UpdateCodec.RouteAttributes attrs;
            try
            {
                // #292 item 1: local router-id for the §6.8 self-address check on NEXT_HOP.
                attrs = UpdateCodec.ParseRouteAttributes(update, _remoteFourByteAsn,
                    BgpConstants.IPAddressToUint(_bgpConfig.GetRouterIdAddress()));
            }
            catch (BgpNotificationException ex) when (ex.ErrorCode == BgpConstants.Error.UpdateMessageError)
            {
                // RFC 7606 §2, "treat-as-withdraw": "the UPDATE message containing the path
                // attribute in question MUST be treated as though all contained routes had been
                // withdrawn just as if they had been listed in the WITHDRAWN ROUTES field ... thus
                // causing them to be removed from the Adj-RIB-In."
                //
                // #288: only the "treat" half was implemented. The UPDATE was discarded and the
                // session kept, but its NLRI stayed installed carrying the attributes of the
                // PREVIOUS announcement — so a peer whose route changed in a way we cannot parse
                // kept its stale next hop indefinitely, with no NOTIFICATION (correctly) and no
                // removal (incorrectly). Nothing on either end could observe it.
                //
                // The withdrawn-routes half of this same UPDATE was already applied above, before
                // the parse; this closes the asymmetry on the NLRI side.
                // Same ownership rule as an explicit withdrawal (#289): treat-as-withdraw removes
                // what this peer installed, not whatever is at that prefix.
                foreach (var nlri in update.Nlri)
                    WithdrawIfOwned(nlri, "Route withdrawn (treat-as-withdraw)");
                _metrics.SetRouteCount(_routeTable.Count);
                throw;
            }

            // #306: RFC 7606 attribute-discard surfaced — the UPDATE is otherwise fine and its
            // routes install; a Warning shows WHICH attributes were dropped (the session stays up,
            // so this is the only trace an operator gets).
            if (attrs.DiscardedAttributes is { Count: > 0 } dropped)
                _logger.LogWarning("Discarded malformed attribute(s) [{Types}] from {Peer} — routes kept (RFC 7606 attribute discard)",
                    string.Join(",", dropped), _peer);

            var filterPeerConfig = GetFilterPeerConfig();

            foreach (var nlri in update.Nlri)
            {
                var route = new Route
                {
                    Prefix = nlri.Address,
                    PrefixLength = nlri.Length,
                    NextHop = attrs.NextHop,
                    AsPath = attrs.AsPath,
                    Communities = attrs.Communities,
                    LargeCommunities = attrs.LargeCommunities
                };

                // RFC 4271 §9.1.2 (#292 item 6): "AS loop detection is done by scanning the full
                // AS path ... and checking that the autonomous system number of the local system
                // does not appear in the AS path" — such routes "should be excluded from the
                // Phase 2 decision function". A route carrying our own ASN would loop straight
                // back to us if re-advertised (with our ASN prepended yet again). Route-level
                // exclusion, not a session error (the old subcode 7 is deprecated); excluded
                // routes are never installed, so a later withdrawal for them removes nothing.
                if (attrs.AsPath.AsSpan().Contains(_bgpConfig.Asn))
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("Excluded looping route {Prefix} from {Peer}: local AS {Asn} in AS_PATH",
                            nlri, _peer, _bgpConfig.Asn);
                    continue;
                }

                if (_routeFilter.AcceptIncoming(route, filterPeerConfig))
                {
                    // #304: per-peer prefix ceiling (RFC 4271 §6.7 / RFC 4486 §2) — counted on the
                    // distinct NLRI this session currently owns; replacements of an owned prefix
                    // do not grow the count, withdrawals shrink it. Exceeding the cap throws
                    // Cease/MaxPrefixesExceeded: deliberately NOT UpdateMessageError, so the read
                    // loop's treat-as-withdraw filter does not swallow it — it unwinds to RunAsync's
                    // BgpNotificationException handler, which sends the NOTIFICATION and tears the
                    // session down (owned routes are flushed by the finally, RFC 4271 §8.2.2).
                    var cap = Volatile.Read(ref _effectiveMaxPrefix);
                    if (cap > 0 && !_installedPrefixes.ContainsKey(nlri) && _installedPrefixes.Count >= cap)
                        throw new BgpNotificationException(
                            BgpConstants.Error.Cease, BgpConstants.SubError.CeaseMaxPrefixes,
                            $"Peer {_peer} exceeded the per-peer prefix limit ({cap}); session reset per RFC 4486");
                    // #377 review: record membership BEFORE publishing — a concurrent takeover
                    // fires EntryOwnershipLost synchronously inside AddOrUpdate, and the handler's
                    // remove would no-op against a key not yet recorded, leaving this session
                    // counting a prefix it no longer owns.
                    _installedPrefixes.TryAdd(nlri, 0);

                    // Tagged with this session as the owner, so only this peer's own withdrawal can
                    // remove it (#289). A route the filter dropped is never installed and therefore
                    // never owned, so a later withdrawal for it removes nothing.
                    _routeTable.AddOrUpdate(route, owner: this);

                    // #377 review: warn AFTER the install with the actual count — the pre-install
                    // count logged "0/1" for the very first route under cap=1 (threshold floor 0).
                    if (cap > 0 && !_maxPrefixesWarned && _installedPrefixes.Count >= MaxPrefixWarningThreshold(cap))
                    {
                        _maxPrefixesWarned = true;
                        _logger.LogWarning("Peer {Peer} at {Count}/{Cap} of the per-peer prefix limit", _peer, _installedPrefixes.Count, cap);
                    }
                    // #85: guard the UintToIPAddress allocation behind IsEnabled — LogDebug
                    // evaluates the arg eagerly even when Debug is filtered out.
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("Route added: {Prefix} via {NextHop}", nlri, BgpConstants.UintToIPAddress(attrs.NextHop));
                }
            }
        }

        _metrics.SetRouteCount(_routeTable.Count);
    }

    /// <summary>
    /// Removes <paramref name="prefix"/> from the shared route table only if this session still owns
    /// the entry (#289). RFC 4271 §9 withdraws the route received from that peer; with one shared
    /// table the alternative is letting any peer delete the startup seed, another peer's route, or
    /// one of its own that another peer has since replaced. A withdrawal that owns nothing is logged
    /// and ignored — not a protocol error, since a stale withdrawal after a reconverge is ordinary.
    /// </summary>
    /// <summary>
    /// #377 review: another session took over a key we installed — stop counting it. Runs on the
    /// REPLACING session's thread (see RouteTable.EntryOwnershipLost); the concurrent set makes
    /// that safe, and a late/duplicate notification only removes an entry already gone.
    /// </summary>
    private void OnEntryOwnershipLost(object previousOwner, (uint Prefix, byte Length) key)
    {
        if (!ReferenceEquals(previousOwner, this))
            return;
        _installedPrefixes.TryRemove(new IpPrefix(key.Prefix, key.Length), out _);
    }

    /// <summary>
    /// 75% of the per-peer prefix cap, overflow-safe (#377 review): cap*3 in int arithmetic
    /// overflows for large caps and can arm the warning at a wrong (even zero) count. 75% of any
    /// positive int always fits int (¾·MaxValue &lt; MaxValue), so a widened multiply is exact —
    /// no saturation branch.
    /// </summary>
    internal static int MaxPrefixWarningThreshold(int cap) =>
        (int)((long)cap * 3 / 4);

    private void WithdrawIfOwned(IpPrefix prefix, string reason)
    {
        if (!_routeTable.RemoveOwnedBy(prefix.Address, prefix.Length, this))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Ignoring withdrawal of {Prefix} from {Peer}: not owned by this session", prefix, _peer);
            return;
        }

        // #304: the cap counts what this session currently owns — a withdrawal frees budget.
        // (A takeover already removed it via the ownership handler; TryRemove tolerates that.)
        _installedPrefixes.TryRemove(prefix, out _);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("{Reason}: {Prefix}", reason, prefix);
    }

    /// <summary>
    /// Thin wrapper over <see cref="IRouteAssembler.BuildOutboundRoutesAsync"/> (#93 Phase 2): resolves
    /// the per-peer route set, then delegates the aggregate + batch + send to <see cref="SendRoutesAsync"/>.
    /// The decision tree (RU defaults / subscriptions / custom prefixes / custom AS / user sources) and
    /// the outgoing filter live in the assembler; the send/withdraw mirror stays here.
    /// </summary>
    private async Task SendAllRoutesAsync()
    {
        // #391: refresh the effective per-peer prefix ceiling once per cycle — the peer row's
        // MaxPrefix override when the peer is configured (operator edits apply on the next
        // refresh), else the global default. Unknown peers (auto-register path) read null here
        // and keep the global cap until their row exists.
        if (_peerStore is not null)
        {
            var overrideCap = await _peerStore.GetPeerMaxPrefixAsync(_peerConfig.Address, _remoteAsn, _cts.Token);
            Volatile.Write(ref _effectiveMaxPrefix, overrideCap ?? _bgpConfig.MaxPrefixesPerPeer);
        }

        var nextHop = BgpConstants.IPAddressToUint(_bgpConfig.GetRouterIdAddress());
        var routes = await _routeAssembler.BuildOutboundRoutesAsync(
            _peerConfig.Address, _remoteAsn, GetFilterPeerConfig(), _peer, _cts.Token);
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
        // #212: cache the actual wire count for the API/UI.
        Volatile.Write(ref _advertisedCount, sent);
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

    private async Task SendRouteBatchAsync(uint nextHop, List<Route> routes, Dictionary<IReadOnlyList<uint>, List<PathAttribute>> attrCache)
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
                // RFC 4271 §6.1: Bad Message Length, with the erroneous Length in the Data field.
                // ErrorCode stays null so this remains a fixed-header failure — ReadLoopAsync must
                // NOT treat it as withdraw-able, both per §6.1 and because the payload has not been
                // read yet, so continuing would desync the stream (#223, #300).
                throw new BgpParseException($"Invalid message length: {length}",
                    subErrorCode: BgpConstants.SubError.BadMessageLength,
                    notificationData: [(byte)(length >> 8), (byte)length]);

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
    //
    // INVARIANT (#285): every route-carrying send — WithdrawAllAsync, SendUpdateBatchAsync,
    // SendEndOfRibAsync — and the OPEN/KEEPALIVE sends pass NO token, so `ct` is None for them and
    // a per-send budget abort can only surface as IOException, which RefreshCycleAsync turns into a
    // teardown. NotifyCeaseAsync is the ONLY caller that passes a real token (the host's shutdown
    // grace), and there BgpServer.StopAsync disposes the session immediately afterwards.
    //
    // That matters because SocketBgpConnection latches its send-fault on a caller-cancelled write
    // too: an aborted socket write is not rolled back regardless of which token fired. If a future
    // caller threads a cancellable token into a route-carrying send, the `return false` below would
    // let RefreshCycleAsync finish normally on a poisoned transport — the session would stay
    // Established with the peer mid-frame. Thread a token here only together with a way for that
    // failure to reach RefreshCycleAsync.
    private async Task<bool> SendMessageAsync(BgpMessage message, CancellationToken ct = default)
    {
        // #341: SemaphoreSlim.Dispose never wakes queued waiters (verified: a waiter parked with
        // CancellationToken.None stays WaitingForActivation forever), so a send queued on
        // _sendLock while Dispose runs would hang RunAsync. The wait is therefore raced against
        // a dispose signal (set in Dispose BEFORE the semaphore goes away) — the loser unwinds as
        // "not sent". The wait itself keeps the CALLER's token (None for keepalive/route sends):
        // RunEstablishedAsync cancels _cts BEFORE RunAsync's catch blocks send their
        // best-effort NOTIFICATION, so binding the wait to _cts would suppress those sends
        // (regression caught by InvalidHeaderLength_OnWire…). Fast path: an uncontended
        // WaitAsync completes synchronously and skips the WhenAny entirely.
        Task waitTask;
        try
        {
            waitTask = _sendLock.WaitAsync(ct);
        }
        catch (ObjectDisposedException)
        {
            return false; // semaphore already disposed — nothing to send
        }

        if (!waitTask.IsCompletedSuccessfully)
        {
            var winner = await Task.WhenAny(waitTask, _sendLockDisposed.Task);
            if (winner != waitTask)
                return false; // _sendLock disposed while queued (#341) — abandon the wait
            await waitTask; // propagate OCE (caller token) / complete the acquisition
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

        // #318: the Graceful Restart capability is deliberately NOT advertised. RFC 4724 §4.2 obliges
        // a speaker engaging GR procedures to retain and stale-mark a restarting peer's routes;
        // BGPLite implements none of that receiving half, so advertising the <AFI, SAFI, F> tuple
        // promised behavior the code does not have (D6). Reintroduce the advertisement only together
        // with receiving-speaker retention. The sending-side conveniences gated on the
        // GracefulRestart config (End-of-RIB after the initial dump, silent close on server
        // shutdown) are unchanged.

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
    /// instead to let peers retain our routes. The one GR-on exception is peer deletion (#323, D16):
    /// a deleted peer is a permanent removal, not a restart, so the Cease is what tells the peer to
    /// flush our routes. Write/IO errors are swallowed (we are shutting down).
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

    private async Task ValidateOpenAsync(BgpOpenMessage open, CancellationToken ct)
    {
        var localRouterId = BgpConstants.IPAddressToUint(_bgpConfig.GetRouterIdAddress());
        // #269: OPEN negotiation/validation lives in the protocol library (OpenNegotiator); the
        // session applies the negotiated values and owns only the I/O side effects below.
        var negotiation = OpenNegotiator.Validate(open, _peerConfig.RemoteAsn, localRouterId, _bgpConfig.HoldTime);

        _remoteAsn = negotiation.RemoteAsn;
        _remoteFourByteAsn = negotiation.RemoteFourByteAsn;
        _localFourByteAsn = negotiation.LocalFourByteAsn;
        _remoteRouteRefresh = negotiation.RemoteRouteRefresh;
        _negotiatedHoldTime = negotiation.NegotiatedHoldTime;
        _keepAliveInterval = negotiation.KeepAliveInterval;

        // Announce/persist the peer only after the OPEN passes validation. Previously this fired
        // before the expected-ASN check, upserting a configured peer that declared a mismatched ASN
        // (BadPeerAs) just before the session was torn down.
        // CodeRabbit (integration review): propagate the session token into the upsert so a
        // locked SQLite cannot out-wait shutdown/replacement before cancellation is observed.
        if (_onPeerIdentified is not null) await _onPeerIdentified(_peerConfig.Address, _remoteAsn, ct);

        var peerGr = CapabilityHelper.GetGracefulRestart(open);
        _logger.LogInformation("Peer {Peer} Graceful Restart: {State}",
            _peer,
            peerGr.HasValue
                ? $"supported (restartState={peerGr.Value.RestartState}, restartTime={peerGr.Value.RestartTime}s, IPv4/Unicast forwarding={peerGr.Value.Ipv4UnicastForwarding})"
                : "not supported");
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

        // #341: wake any send parked on _sendLock BEFORE the semaphore (and its waiter queue)
        // goes away — SendMessageAsync abandons such waits and reports "not sent".
        _sendLockDisposed.TrySetResult();
        _routeTable.EntryOwnershipLost -= OnEntryOwnershipLost;
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
