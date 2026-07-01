using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BGPLite.Server;

public sealed class BgpServer : IHostedService, ISessionManager, IDisposable
{
    private readonly AppConfig _config;
    private readonly RouteTable _routeTable;
    private readonly IRouteFilter _routeFilter;
    private readonly BgpMetrics _metrics;
    private readonly ILogger<BgpSession> _sessionLogger;
    private readonly ILogger<BgpServer> _logger;
    private readonly Action<string, uint>? _onPeerIdentified;
    private readonly IPeerStore? _peerStore;
    private readonly IPrefixService? _prefixService;
    private readonly IPrefixAggregator _prefixAggregator;
    // Keyed by the accepted TCP connection (remote IP + remote source port), NOT by remote IP
    // alone: per RFC 4271 §8.2.1 there is one session per TCP connection, so several distinct peers
    // arriving from the same source IP (different ephemeral source ports) must coexist as separate
    // entries. Keying by IP only made them clobber each other (issue #18).
    private readonly ConcurrentDictionary<SessionKey, BgpSession> _sessions = new();
    private readonly CancellationTokenSource _cts = new();
    private int _acceptingConnections = 1;
    private Socket? _listener;
    private Task? _acceptTask;
    private PeriodicTimer? _statusTimer;
    private Task? _statusTask;

    public BgpMetrics Metrics => _metrics;
    public RouteTable Routes => _routeTable;

    public BgpServer(
        AppConfig config,
        RouteTable routeTable,
        IRouteFilter routeFilter,
        BgpMetrics metrics,
        ILogger<BgpSession> sessionLogger,
        ILogger<BgpServer> logger,
        Action<string, uint>? onPeerIdentified = null,
        IPeerStore? peerStore = null,
        IPrefixService? prefixService = null,
        IPrefixAggregator? prefixAggregator = null)
    {
        _config = config;
        _routeTable = routeTable;
        _routeFilter = routeFilter;
        _metrics = metrics;
        _sessionLogger = sessionLogger;
        _logger = logger;
        _onPeerIdentified = onPeerIdentified;
        _peerStore = peerStore;
        _prefixService = prefixService;
        _prefixAggregator = prefixAggregator ?? new ExactUnionPrefixAggregator();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Bind(new IPEndPoint(IPAddress.Any, BgpConstants.BgpPort));
        _listener.Listen(16);

        _logger.LogInformation("BGP server listening on port {Port}", BgpConstants.BgpPort);
        _logger.LogInformation("Local ASN={Asn}, RouterId={RouterId}", _config.Bgp.Asn, _config.Bgp.RouterId);

        _acceptTask = AcceptLoopAsync(_cts.Token);

        _statusTimer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        _statusTask = LogStatusLoopAsync(_cts.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("BGP server shutting down");

        // Stop accepting new connections before we snapshot/mark the current sessions.
        // Otherwise a connection that sneaks in between the GR mark loop and _cts.Cancel()
        // can miss SilentClose and later emit a protocol-incorrect Cease.
        Volatile.Write(ref _acceptingConnections, 0);

        if (_listener is not null)
        {
            _listener.Close();
        }

        if (_acceptTask is not null)
        {
            try { await _acceptTask; }
            catch { }
        }

        // Graceful Restart-aware shutdown (RFC 4724 §4): a NOTIFICATION termination bypasses GR, so
        // send a Cease only when GR is disabled — peers then tear down cleanly instead of waiting on
        // the hold timer. With GR enabled we deliberately just drop the TCP connection so peers
        // engage GR and retain our routes across the restart. Must run BEFORE _cts.Cancel() tears
        // the sessions down: the sessions' RunAsync finally-blocks would otherwise see no teardown
        // reason (None) and emit a best-effort Cease — which would bypass GR exactly as a Cease would.
        // MarkSilentClose latches SilentClose and cancels each session's own CTS so the read/keepalive
        // loops stop promptly, then _cts.Cancel() handles the accept loop and anything still pending.
        if (_config.Bgp.GracefulRestart)
        {
            foreach (var session in _sessions.Values)
                session.MarkSilentClose();
        }
        else
        {
            var ceases = _sessions.Values
                .Where(s => s.IsEstablished)
                .Select(s => s.NotifyCeaseAsync())
                .ToArray();
            if (ceases.Length > 0)
                await Task.WhenAll(ceases);
        }

        _cts.Cancel();

        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();

        _statusTimer?.Dispose();
        if (_statusTask is not null)
        {
            try { await _statusTask; } catch { }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var socket = await _listener!.AcceptAsync(cancellationToken);
                var remoteEndpoint = (IPEndPoint)socket.RemoteEndPoint!;
                // Session identity = the accepted TCP connection (remote IP + remote source port),
                // so peers sharing a source IP but on different source ports get distinct slots and
                // coexist (RFC 4271 §8.2.1; issue #18). peerAddress stays the IP-only form for the
                // PeerStore, which is still keyed by IP.
                var key = new SessionKey(remoteEndpoint.Address, remoteEndpoint.Port);
                var peerAddress = remoteEndpoint.Address.ToString();

                _logger.LogInformation("Incoming connection from {Peer} ({Key})", peerAddress, key);

                var peerConfig = new PeerConfig { Address = peerAddress };

                var session = new BgpSession(
                    socket, peerConfig, _config.Bgp, _routeTable,
                    _routeFilter, _metrics, _sessionLogger,
                    _onPeerIdentified,
                    _peerStore, _prefixService, _config, _prefixAggregator);

                if (Volatile.Read(ref _acceptingConnections) == 0)
                {
                    session.Dispose();
                    break;
                }

                // Register under the connection key. Two distinct peers from the same source IP
                // have distinct (IP, port) keys, so they coexist instead of replacing each other
                // (issue #18). A key collision now only happens for a genuine duplicate of the SAME
                // connection — e.g. the OS reusing a source port on reconnect while the old entry
                // has not been cleaned up — which is exactly the "silently close the stale one and
                // swap" case the CAS below handles. max-active is not enforced at accept here, so
                // the simple swap is safe.
                //
                // Replacement policy: the old session must actually stop, not just be told to Cease.
                // MarkSilentClose latches SilentClose (so the old RunAsync finally emits no
                // NOTIFICATION — RFC 4724 §4 / §8.1) AND cancels the old CTS so the loops unwind
                // promptly. No Cease is sent on replacement: a Cease to the old socket is noise
                // (and, with GR enabled, would bypass GR). The peer sees a TCP close instead.
                //
                // Use TryUpdate (atomic CAS) so two concurrent accept threads for the same key
                // cannot both pass TryGetValue and both install their session. If the CAS fails,
                // another thread already swapped the entry — retry from the top.
                var sessionRegistered = _sessions.TryAdd(key, session);
                if (!sessionRegistered)
                {
                    while (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _acceptingConnections) != 0)
                    {
                        if (_sessions.TryGetValue(key, out var existing))
                        {
                            // Atomic CAS: only swap if the registered value is still 'existing'.
                            if (_sessions.TryUpdate(key, session, existing))
                            {
                                _logger.LogInformation("Replacing existing session for {Key}", key);
                                existing.MarkSilentClose();
                                sessionRegistered = true;
                                break;
                            }
                            // CAS failed — another thread replaced it; loop and retry.
                        }
                        else
                        {
                            // Existing was concurrently removed — try to add ours.
                            if (_sessions.TryAdd(key, session))
                            {
                                sessionRegistered = true;
                                break;
                            }
                            // TryAdd failed — another thread re-added for this key; loop and retry.
                        }
                    }

                    if (!sessionRegistered)
                    {
                        session.Dispose();
                        break;
                    }
                }

                if (sessionRegistered)
                    _ = RunSessionAsync(key, session, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _acceptingConnections) == 0)
                    break;
                _logger.LogError(ex, "Error accepting connection");
            }
        }
    }

    private async Task RunSessionAsync(SessionKey key, BgpSession session, CancellationToken cancellationToken)
    {
        try
        {
            await session.RunAsync(cancellationToken);
        }
        finally
        {
            // Atomically remove our registration ONLY if we are still the current session for this
            // connection key. The previous TryGetValue + TryRemove was not atomic: a racing re-accept
            // could install a newer session between those two calls, and our TryRemove would then
            // erase the newer session from the dictionary. ConcurrentDictionary has no public
            // TryRemove(key, expectedValue), but its explicit ICollection<KeyValuePair<TKey,TValue>>
            // implementation removes the pair only when both key AND value match — an atomic
            // compare-and-remove. So a newer session installed after our exit is left untouched.
            RemoveSessionIfOwner(key, session);
            session.Dispose();
        }
    }

    /// <summary>
    /// Atomically removes <paramref name="session"/> from <see cref="_sessions"/> only if it is
    /// still the registered session for <paramref name="key"/>. Uses the explicit
    /// <see cref="ICollection{T}"/>.Remove on ConcurrentDictionary, which is documented to remove
    /// the pair only when both key and value match — a compare-and-remove that closes the race the
    /// earlier TryGetValue+TryRemove had (a newer re-accepted session would otherwise be erased).
    /// </summary>
    private void RemoveSessionIfOwner(SessionKey key, BgpSession session)
    {
        var removed = ((ICollection<KeyValuePair<SessionKey, BgpSession>>)_sessions)
            .Remove(new KeyValuePair<SessionKey, BgpSession>(key, session));
        if (removed)
            _logger.LogDebug("Removed session for {Key} (we owned it)", key);
        else
            _logger.LogDebug("Did not remove session for {Key} (replaced by a newer session)", key);
    }

    private async Task LogStatusLoopAsync(CancellationToken cancellationToken)
    {
        while (await _statusTimer!.WaitForNextTickAsync(cancellationToken))
        {
            var peers = string.Join(", ", _sessions.Keys);
            _logger.LogInformation("Active sessions: {Count} [{Peers}]", _sessions.Count, peers);
        }
    }

    public async Task RefreshPeerAsync(string peerIp)
    {
        // Several peers may share one source IP (distinct source ports — keyed by SessionKey, see
        // issue #18), so refresh every established session for that IP rather than a single slot.
        if (!IPAddress.TryParse(peerIp, out var ip))
        {
            _logger.LogWarning("RefreshPeer: invalid IP {Ip}", peerIp);
            return;
        }

        var sessions = _sessions
            .Where(kvp => kvp.Key.Address.Equals(ip))
            .Select(kvp => kvp.Value)
            .ToList();

        if (sessions.Count == 0)
        {
            _logger.LogWarning("RefreshPeer: no session for {Ip} (active: [{Peers}])",
                peerIp, string.Join(", ", _sessions.Keys));
            return;
        }

        var established = sessions.Where(s => s.IsEstablished).ToList();
        if (established.Count == 0)
        {
            _logger.LogWarning("RefreshPeer: session(s) for {Ip} not established (states=[{States}])",
                peerIp, string.Join(", ", sessions.Select(s => s.State)));
            return;
        }

        foreach (var session in established)
            await session.RefreshRoutesAsync();
    }

    public List<string> GetActivePeerIps() =>
        _sessions.Where(kvp => kvp.Value.IsEstablished)
                 .Select(kvp => kvp.Key.Address.ToString())
                 .Distinct()
                 .ToList();

    public void Dispose()
    {
        Volatile.Write(ref _acceptingConnections, 0);
        _listener?.Close();
        _cts.Cancel();
        foreach (var session in _sessions.Values)
            session.Dispose();
    }
}
