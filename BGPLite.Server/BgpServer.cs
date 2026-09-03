using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BGPLite.Contracts;

namespace BGPLite.Server;

public sealed class BgpServer : IHostedService, ISessionManager, IDisposable
{
    private readonly AppConfig _config;
    private readonly RouteTable _routeTable;
    private readonly IRouteFilter _routeFilter;
    private readonly BgpMetrics _metrics;
    private readonly ILogger<BgpServer> _logger;
    // #263: the accept loop needs none of the session's dependencies for itself — it only forwarded
    // them, and every forwarded one was optional. The factory owns them and requires them.
    private readonly IBgpSessionFactory _sessionFactory;
    // Keyed by the accepted TCP connection (remote IP + remote source port), NOT by remote IP
    // alone: per RFC 4271 §8.2.1 there is one session per TCP connection, so several distinct peers
    // arriving from the same source IP (different ephemeral source ports) must coexist as separate
    // entries. Keying by IP only made them clobber each other (issue #18).
    private readonly ConcurrentDictionary<SessionKey, BgpSession> _sessions = new();
    private readonly CancellationTokenSource _cts = new();
    // Per-source-IP accept throttle (#115): bounds inbound-connect floods from a single IP. Disabled
    // (always-allow) when Bgp.MaxAcceptsPerIpPerMinute <= 0.
    private readonly IpAcceptThrottle _acceptThrottle;
    private int _acceptingConnections = 1;
    private Socket? _listener;
    // #36: per-peer TCP-MD5 (RFC 2385) shared keys, keyed by the peer's source IP (TCP keys the
    // connection by address, not by (IP, ASN) — peers sharing one source IP share the key).
    private readonly ConcurrentDictionary<IPAddress, byte[]> _md5Keys = new();
    private Task? _acceptTask;
    private PeriodicTimer? _statusTimer;
    private Task? _statusTask;
    // #428: pause before retrying after a failed accept — see the catch in AcceptLoopAsync.
    private static readonly TimeSpan AcceptFailureBackoff = TimeSpan.FromMilliseconds(500);

    public BgpMetrics Metrics => _metrics;
    public RouteTable Routes => _routeTable;

    public BgpServer(
        AppConfig config,
        RouteTable routeTable,
        IRouteFilter routeFilter,
        BgpMetrics metrics,
        IBgpSessionFactory sessionFactory,
        ILogger<BgpServer> logger)
    {
        _config = config;
        _routeTable = routeTable;
        _routeFilter = routeFilter;
        _metrics = metrics;
        _sessionFactory = sessionFactory;
        _logger = logger;
        _acceptThrottle = new IpAcceptThrottle(config.Bgp.MaxAcceptsPerIpPerMinute);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // #14 phase 4: prefer the dual-mode IPv6 listener — it accepts both IPv6 peers and IPv4
        // peers (the kernel surfaces the latter as IPv4-mapped addresses, normalized in
        // AcceptLoopAsync). DualMode must be set before Bind. On hosts with IPv6 disabled we fall
        // back to the pre-phase-4 IPv4 listener instead of refusing to start: serving IPv4-only
        // beats not serving at all, and the capability difference is announced loudly.
        var useDualMode = Socket.OSSupportsIPv6;
        if (useDualMode)
        {
            _listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            _listener.DualMode = true;
            _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Bind(new IPEndPoint(IPAddress.IPv6Any, BgpConstants.BgpPort));
        }
        else
        {
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Bind(new IPEndPoint(IPAddress.Any, BgpConstants.BgpPort));
            _logger.LogWarning("IPv6 is not available on this host — serving IPv4 peers only");
        }
        // #344: after a restart every peer reconnects at once; backlog 16 dropped SYNs and pushed
        // peers into their own retry backoff, stretching reconvergence for no benefit. A few
        // hundred pending accepts costs nothing on a route-server host.
        _listener.Listen(512);

        _logger.LogInformation("BGP server listening on {Address}:{Port}", useDualMode ? "[::]" : "0.0.0.0", BgpConstants.BgpPort);
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

        // The host's shutdown token bounds how long each step blocks — a stuck peer (TCP receive
        // window full → WriteAsync blocks on the send buffer) must not pin StopAsync past the host's
        // grace (#161). WaitAsync propagates the cancellation as OperationCanceledException; on
        // cancel we abandon the pending step and move on to force-disposing the sessions below.
        if (_acceptTask is not null)
        {
            try { await _acceptTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { /* host grace elapsed — proceed to force teardown */ }
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
                .Select(s => s.NotifyCeaseAsync(cancellationToken))
                .ToArray();
            if (ceases.Length > 0)
            {
                try { await Task.WhenAll(ceases).WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { /* host grace elapsed — proceed to force teardown */ }
                catch { }
            }
        }

        _cts.Cancel();

        // Always dispose the sessions even if a Cease step was cancelled above — the socket close is
        // the ultimate signal to the peer and releases FDs/timers/tasks so the process can exit.
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();

        _statusTimer?.Dispose();
        if (_statusTask is not null)
        {
            try { await _statusTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { /* host grace elapsed */ }
            catch { }
        }
    }

    /// <inheritdoc cref="ISessionManager.SetPeerMd5Key" />
    public void SetPeerMd5Key(string peerIp, string? password)
    {
        // #36: opt-in per peer — a password enables RFC 2385 enforcement for the peer's source
        // IP; clearing it returns the peer to plain TCP. On unsupported platforms this is a
        // logged no-op (fail-visible, D: TCP-MD5 is Linux/macOS-only), never a crash.
        if (!IPAddress.TryParse(peerIp, out var address))
        {
            _logger.LogWarning("TCP-MD5: ignoring unparseable peer IP '{PeerIp}'", peerIp);
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            _md5Keys.TryRemove(address, out _);
            ApplyMd5(_listener, address, key: null);
            return;
        }

        if (!TcpMd5.IsValidPassword(password))
        {
            _logger.LogWarning("TCP-MD5: rejecting a password with an invalid length (must be 1..{Max} UTF-8 bytes)", TcpMd5.PasswordMaxBytes);
            return;
        }

        var key = TcpMd5.KeyBytes(password);
        _md5Keys[address] = key;
        ApplyMd5(_listener, address, key);
    }

    private void ApplyMd5(Socket? socket, IPAddress peer, byte[]? key)
    {
        if (socket is null)
            return;
        var wire = Md5WireAddress(socket.AddressFamily, peer);
        try
        {
            if (key is null)
                TcpMd5.Clear(socket, wire);
            else
                TcpMd5.Apply(socket, wire, key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "TCP-MD5: could not {Verb} the key for {Peer} on this platform — the peer keeps {State} (RFC 2385 support is Linux/macOS-only)",
                key is null ? "clear" : "set", peer, key is null ? "unprotected" : "unprotected until supported");
        }
    }

    /// <summary>
    /// Normalizes the remote address of an accepted connection for identity/lookup use — the
    /// session key, the PeerStore string address, the accept throttle and the MD5 key table all
    /// key on the plain form. A dual-mode listener surfaces IPv4 peers as IPv4-mapped IPv6
    /// (<c>::ffff:a.b.c.d</c>); configured peers are plain IPv4, so the mapped form would break
    /// every lookup. internal static for unit tests.
    /// </summary>
    internal static IPAddress NormalizeAcceptedAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>
    /// The address form a TCP-MD5 key must be stored under on <paramref name="socketFamily"/>.
    /// The kernel resolves the key through the socket's own parse path, which demands the
    /// SOCKET's family in the sockaddr: on Linux an IPv6 socket's
    /// <c>tcp_v6_parse_md5_keys</c> rejects an AF_INET entry (EINVAL) but accepts the
    /// IPv4-mapped form (<c>ipv6_addr_v4mapped</c>, prefixlen 32) — so an IPv4 peer maps to
    /// <c>::ffff:a.b.c.d</c> on a v6 (dual-mode) socket. A v4 socket and real v6 peers pass
    /// through unchanged. Pure; internal static for unit tests.
    /// </summary>
    internal static IPAddress Md5WireAddress(AddressFamily socketFamily, IPAddress peer) =>
        socketFamily == AddressFamily.InterNetworkV6 && peer.AddressFamily == AddressFamily.InterNetwork
            ? peer.MapToIPv6()
            : peer;

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var socket = await _listener!.AcceptAsync(cancellationToken);
                var remoteEndpoint = (IPEndPoint)socket.RemoteEndPoint!;
                // A dual-mode listener surfaces IPv4 peers as IPv4-mapped IPv6 addresses
                // (::ffff:a.b.c.d). Normalize BEFORE anything keys on the address: the session
                // key, the PeerStore string address, the accept throttle and the MD5 key table
                // all key on the plain IPv4 form, so configured peer "10.0.0.1" matches the
                // connection it configured. The RAW address stays the MD5 wire form — see
                // Md5WireAddress for why it must NOT be normalized there (#14 phase 4).
                var address = NormalizeAcceptedAddress(remoteEndpoint.Address);
                // Session identity = the accepted TCP connection (remote IP + remote source port),
                // so peers sharing a source IP but on different source ports get distinct slots and
                // coexist (RFC 4271 §8.2.1; issue #18). peerAddress stays the IP-only form for the
                // PeerStore, which is still keyed by IP.
                var key = new SessionKey(address, remoteEndpoint.Port);
                var peerAddress = address.ToString();

                // Per-source-IP accept throttle (#115): defend one-IP accept floods. If this IP has
                // already exceeded MaxAcceptsPerIpPerMinute within the rolling 60s window, close the
                // just-accepted socket immediately WITHOUT spawning a session — no FD/task/session
                // pinned. The rejected attempt is logged and the loop continues (continue, not break:
                // this is a per-IP limit, not a server-wide stop). Disabled when the limit is 0.
                if (!_acceptThrottle.TryAccept(peerAddress))
                {
                    _logger.LogWarning(
                        "Accept throttle: closing connection from {Peer} (over {Limit} accepts/min, #115)",
                        peerAddress, _config.Bgp.MaxAcceptsPerIpPerMinute);
                    socket.Dispose();
                    continue;
                }

                _logger.LogInformation("Incoming connection from {Peer} ({Key})", peerAddress, key);

                // #96: the transport seam — SocketBgpConnection owns the socket (and the 60s
                // SendTimeout backstop from #160), so BgpSession no longer touches Socket directly.
                var peerConfig = new PeerConfig { Address = peerAddress, Port = remoteEndpoint.Port };

                // #36: the accepted socket inherits the listener's TCP-MD5 key on Linux; re-apply
                // for the known peer so enforcement does not depend on inheritance semantics.
                // The lookup keys on the normalized address (the table is keyed by the configured
                // form); ApplyMd5 re-maps to the socket's wire form itself.
                if (_md5Keys.TryGetValue(address, out var md5Key))
                    ApplyMd5(socket, remoteEndpoint.Address, md5Key);

                var session = _sessionFactory.Create(new SocketBgpConnection(socket), peerConfig);
                // #265 item 1: the session's finally-block consults this before flipping the
                // peer row to inactive — "still the registered session for your slot?" A
                // replacement (TryUpdate below) removes this session from the registry, so its
                // slow unwind cannot clobber the replacement's Status=active.
                session.StillRegisteredProbe = s => _sessions.Values.Any(v => ReferenceEquals(v, s));

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
                // #428: a persistent accept failure (fd exhaustion/EMFILE is the classic) used to
                // spin this loop at full speed — log + immediate retry thousands of times a
                // second, exactly when the host is already out of resources. Bound the retry
                // rate; the shutdown token breaks the wait so shutdown latency is unaffected.
                try { await Task.Delay(AcceptFailureBackoff, cancellationToken); }
                catch (OperationCanceledException) { break; }
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

    public async Task RefreshPeerAsync(string peerIp, uint asn)
    {
        // #200: filter by BOTH IP and ASN so a refresh for one peer on a shared IP (NAT/VPN)
        // does not refresh sibling sessions with a different ASN.
        if (!IPAddress.TryParse(peerIp, out var ip))
        {
            _logger.LogWarning("RefreshPeer: invalid IP {Ip}", peerIp);
            return;
        }

        var sessions = _sessions
            .Where(kvp => kvp.Key.Address.Equals(ip) && kvp.Value.RemoteAsn == asn)
            .Select(kvp => kvp.Value)
            .ToList();

        if (sessions.Count == 0)
        {
            _logger.LogWarning("RefreshPeer: no session for {Ip} AS{Asn} (active: [{Peers}])",
                peerIp, asn, string.Join(", ", _sessions.Keys));
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

    public async Task TerminatePeerAsync(string peerIp, uint asn, CancellationToken ct = default)
    {
        // Same (ip, asn) matching as RefreshPeerAsync (#200): a NAT/shared-IP sibling session with
        // a different ASN must survive a peer deletion.
        if (!IPAddress.TryParse(peerIp, out var ip))
        {
            _logger.LogWarning("TerminatePeer: invalid IP {Ip}", peerIp);
            return;
        }

        var sessions = _sessions
            .Where(kvp => kvp.Key.Address.Equals(ip) && kvp.Value.RemoteAsn == asn)
            .Select(kvp => kvp.Value)
            .ToList();

        if (sessions.Count == 0)
            return;

        _logger.LogInformation("Terminating {Count} session(s) for {Ip} AS{Asn} (peer deleted)",
            sessions.Count, peerIp, asn);
        await TerminateSessionsAsync(sessions, ct);
    }

    /// <inheritdoc cref="ISessionManager.TerminatePeerByIpAsync" />
    public async Task TerminatePeerByIpAsync(string peerIp, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(peerIp, out var ip))
        {
            _logger.LogWarning("TerminatePeerByIp: invalid IP {Ip}", peerIp);
            return;
        }

        var sessions = _sessions
            .Where(kvp => kvp.Key.Address.Equals(ip))
            .Select(kvp => kvp.Value)
            .ToList();

        if (sessions.Count == 0)
            return;

        _logger.LogInformation("Terminating {Count} session(s) from {Ip} (IP-only match — the deleted peer row carries no ASN)",
            sessions.Count, peerIp);
        await TerminateSessionsAsync(sessions, ct);
    }

    /// <summary>
    /// The #323 teardown core, split out so tests can drive real sessions without a live BgpServer
    /// (sessions enter <see cref="_sessions"/> only through the accept loop, which binds port 179).
    /// Established sessions get exactly one Cease (Administrative Reset) — NotifyCeaseAsync
    /// CAS-latches the teardown reason, so the session's own finally-block cannot double-send —
    /// and then every session is disposed. The dictionary is left alone: RunSessionAsync's
    /// compare-and-remove unwinds each entry, exactly like session replacement. Unlike StopAsync,
    /// the Cease is sent even when Graceful Restart is enabled: a deleted peer is a permanent
    /// removal, not a restart we will return from, and a NOTIFICATION termination is what makes
    /// the peer flush our routes instead of retaining them (RFC 4724 §4).
    /// </summary>
    internal static async Task TerminateSessionsAsync(IReadOnlyList<BgpSession> sessions, CancellationToken ct)
    {
        foreach (var session in sessions.Where(s => s.IsEstablished))
            await session.NotifyCeaseAsync(ct);   // best-effort: swallows send/IO failures internally

        foreach (var session in sessions)
            session.Dispose();
    }

    public List<string> GetActivePeerIps() =>
        _sessions.Where(kvp => kvp.Value.IsEstablished)
                 .Select(kvp => kvp.Key.Address.ToString())
                 .Distinct()
                 .ToList();

    /// <summary>
    /// Returns the actual advertised prefix count (post-aggregation, post-dedup) for the given
    /// peer (Ip, Asn), or 0 if no session is established (#212).
    /// </summary>
    public int GetAdvertisedPrefixCount(string peerIp, uint asn)
    {
        if (!IPAddress.TryParse(peerIp, out var ip)) return 0;
        return _sessions
            .Where(kvp => kvp.Key.Address.Equals(ip) && kvp.Value.RemoteAsn == asn && kvp.Value.IsEstablished)
            .Select(kvp => kvp.Value.AdvertisedPrefixCount)
            .FirstOrDefault();
    }

    /// <summary>#214: Refresh ALL established sessions concurrently — used by the auto-refresh timer
    /// and the onSourceChanged convergence callback. Sessions refresh in parallel so a single slow
    /// peer (TCP receive window full → WriteAsync blocks) can't stall the rest.</summary>
    public async Task RefreshAllEstablishedAsync()
    {
        var established = _sessions.Values.Where(s => s.IsEstablished).ToList();
        if (established.Count == 0) return;

        _logger.LogInformation("Auto-refresh: refreshing {Count} established sessions", established.Count);
        var failures = 0;
        // Parallel: each session refreshes independently. Per-session try/catch keeps one failure
        // from faulting the aggregate — collected here for the summary log.
        var tasks = established.Select(async session =>
        {
            try { await session.RefreshRoutesAsync(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failures);
                _logger.LogWarning(ex, "Auto-refresh: failed to refresh session {Peer}", session.Peer);
            }
        }).ToArray();
        await Task.WhenAll(tasks);
        if (failures > 0)
            _logger.LogWarning("Auto-refresh: {Failed}/{Total} sessions failed to refresh", failures, established.Count);
    }

    public void Dispose()
    {
        Volatile.Write(ref _acceptingConnections, 0);
        _listener?.Close();
        _cts.Cancel();
        _cts.Dispose();  // #105: dispose the CTS (StopAsync's graceful path doesn't reach here)
        _statusTimer?.Dispose();  // #487: the abort path never runs StopAsync — don't leak the timer to GC
        foreach (var session in _sessions.Values)
            session.Dispose();
    }
}
