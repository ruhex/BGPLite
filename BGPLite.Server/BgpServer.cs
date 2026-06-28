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
    private readonly ConcurrentDictionary<string, BgpSession> _sessions = new();
    private readonly CancellationTokenSource _cts = new();
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

        // Graceful Restart-aware shutdown (RFC 4724 §4): a NOTIFICATION termination bypasses GR, so
        // send a Cease only when GR is disabled — peers then tear down cleanly instead of waiting on
        // the hold timer. With GR enabled we deliberately just drop the TCP connection so peers
        // engage GR and retain our routes across the restart. Must run BEFORE _cts.Cancel() tears
        // the sessions down.
        if (!_config.Bgp.GracefulRestart)
        {
            var ceases = _sessions.Values
                .Where(s => s.IsEstablished)
                .Select(s => s.NotifyCeaseAsync())
                .ToArray();
            if (ceases.Length > 0)
                await Task.WhenAll(ceases);
        }

        _cts.Cancel();

        if (_listener is not null)
        {
            _listener.Close();
            _listener = null;
        }

        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();

        if (_acceptTask is not null)
        {
            try { await _acceptTask; } catch { }
        }

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
                var peerAddress = remoteEndpoint.Address.ToString();

                _logger.LogInformation("Incoming connection from {Address}", peerAddress);

                var peerConfig = new PeerConfig { Address = peerAddress };

                var session = new BgpSession(
                    socket, peerConfig, _config.Bgp, _routeTable,
                    _routeFilter, _metrics, _sessionLogger,
                    _onPeerIdentified,
                    _peerStore, _prefixService, _config, _prefixAggregator);

                // TryAdd first so a racing replacement from the same peer doesn't get clobbered
                // (an older session's finally still owns the key). If an existing session is present
                // it is the older one — ask it to gracefully terminate via NOTIFICATION/Cease so the
                // peer sees a clean close instead of a bare TCP RST, then swap. max-active is not
                // enforced at accept in this codebase, so the simple swap is safe.
                if (!_sessions.TryAdd(peerAddress, session))
                {
                    if (_sessions.TryGetValue(peerAddress, out var existing))
                    {
                        _logger.LogInformation("Replacing existing session for {Peer}", peerAddress);
                        // Fire-and-forget: send Cease so the peer sees a clean close. NotifyCeaseAsync
                        // latches _ceaseSentOnTeardown so the old session's RunAsync finally-block
                        // does not emit a second Cease (RFC 4271 §8.1). The old session still
                        // exits when the peer closes the socket or its hold timer fires; we do
                        // not cancel its CTS here to keep P1 scope minimal.
                        _ = existing.NotifyCeaseAsync();
                        _sessions[peerAddress] = session;
                    }
                    else
                    {
                        // Existing was concurrently removed — just install ours.
                        _sessions[peerAddress] = session;
                    }
                }

                _ = RunSessionAsync(peerAddress, session, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting connection");
            }
        }
    }

    private async Task RunSessionAsync(string peerAddress, BgpSession session, CancellationToken cancellationToken)
    {
        try
        {
            await session.RunAsync(cancellationToken);
        }
        finally
        {
            // Only remove if we are still the registered session. Without this guard, a racing
            // re-accept for the same peer would install a new session, and our finally would
            // then erase it from the dictionary.
            if (_sessions.TryGetValue(peerAddress, out var current) && ReferenceEquals(current, session))
            {
                _sessions.TryRemove(peerAddress, out _);
            }
            session.Dispose();
        }
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
        if (!_sessions.TryGetValue(peerIp, out var session))
        {
            _logger.LogWarning("RefreshPeer: no session for {Ip} (active: [{Peers}])",
                peerIp, string.Join(", ", _sessions.Keys));
            return;
        }

        if (!session.IsEstablished)
        {
            _logger.LogWarning("RefreshPeer: session for {Ip} not established (state={State})", peerIp, session.State);
            return;
        }

        await session.RefreshRoutesAsync();
    }

    public List<string> GetActivePeerIps() =>
        _sessions.Where(kvp => kvp.Value.IsEstablished).Select(kvp => kvp.Key).ToList();

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _listener?.Dispose();
        foreach (var session in _sessions.Values)
            session.Dispose();
    }
}
