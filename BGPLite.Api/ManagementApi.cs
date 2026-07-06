using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.RateLimiting;
using System.Text.Json;
using System.Text.Json.Serialization;
using BGPLite.Api.Entities;
using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Providers;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BGPLite.Api;

public sealed class ManagementApi : IHostedService, IDisposable
{
    private readonly PeerStore _store;
    private readonly RouteTable _routeTable;
    // Hot-reloadable derived state (#136): these four fields are swapped atomically by ApplyConfig
    // via Interlocked.Exchange while the listener keeps running. Reads on the request path capture
    // them into locals (Volatile.Read) so a reload mid-request cannot observe a half-swapped set.
    private AppConfig _config;
    private IReadOnlyList<IPNetwork> _trustedProxyNetworks;
    private PartitionedRateLimiter<string>? _rateLimiter;
    private ConcurrencyLimiter? _concurrencyLimiter;
    private IReadOnlyList<string>? _corsAllowedOrigins;
    private readonly BgpMetrics _metrics;
    private readonly IPrefixService? _prefixService;
    private readonly IPrefixSourceService? _prefixSources;
    private readonly ISessionManager? _sessionManager;
    private readonly ILogger<ManagementApi> _logger;
    private readonly int _port;
    private readonly string _listenAddress;  // #90: bind address — loopback by default
    private HttpListener? _listener;
    private Task? _listenTask;
    private readonly CancellationTokenSource _cts = new();

    public ManagementApi(
        PeerStore store,
        RouteTable routeTable,
        AppConfig config,
        BgpMetrics metrics,
        ILogger<ManagementApi> logger,
        IPrefixService? prefixService = null,
        IPrefixSourceService? prefixSources = null,
        ISessionManager? sessionManager = null)
    {
        _store = store;
        _routeTable = routeTable;
        _config = config;
        _metrics = metrics;
        _prefixService = prefixService;
        _prefixSources = prefixSources;
        _sessionManager = sessionManager;
        _logger = logger;
        _port = config.ApiPort;
        // #90: secure-by-default — bind to loopback unless the operator explicitly sets ApiListen.
        // The previous "http://+:port" exposed the unauthenticated control plane on every interface.
        _listenAddress = string.IsNullOrWhiteSpace(config.ApiListen) ? "127.0.0.1" : config.ApiListen!;
        _trustedProxyNetworks = ParseTrustedProxies(config.TrustedProxies);
        // Opt-in (#116): no rate limiting unless an ApiRateLimit section is configured, so the live
        // service's behavior is unchanged until the operator enables it.
        _rateLimiter = config.ApiRateLimit is { Enabled: true } cfg ? CreateRateLimiter(cfg) : null;
        // Opt-in (#119): no global concurrency cap unless MaxConcurrentRequests > 0, so the live
        // service's behavior is unchanged until the operator sets a limit. Independent of the per-IP
        // rate: a burst passing the per-client token check still cannot run more than this many at once.
        _concurrencyLimiter = config.ApiRateLimit is { Enabled: true, MaxConcurrentRequests: > 0 } limitCfg
            ? CreateConcurrencyLimiter(limitCfg) : null;
        _corsAllowedOrigins = config.CorsAllowedOrigins;
    }

    /// <summary>
    /// Hot-reloads the SOFT (non-session-disrupting) part of the configuration (#136): the
    /// trusted-proxy CIDR list (client-IP resolution), the CORS origin allowlist (via <c>_config</c>),
    /// and the API rate / concurrency limiters. Each derived field is rebuilt from
    /// <paramref name="newConfig"/> and swapped atomically with <see cref="Interlocked.Exchange"/> so
    /// in-flight requests keep observing the previous state while subsequent requests pick up the new
    /// one. The OLD rate / concurrency limiters are disposed after the swap (they hold timers). All
    /// other fields (Bgp, Peers, ApiPort, PrefixSources, RipeStat, communities) are intentionally NOT
    /// applied here — they are baked into established sessions / the listener and require a restart;
    /// the caller logs those as "requires restart". This method never throws: the caller
    /// (<c>ConfigReloader</c>) validates first, and the rebuild steps here only reuse already-validated
    /// parsing helpers.
    /// </summary>
    internal void ApplyConfig(AppConfig newConfig)
    {
        var trusted = ParseTrustedProxies(newConfig.TrustedProxies);
        var rateLimiter = newConfig.ApiRateLimit is { Enabled: true } cfg ? CreateRateLimiter(cfg) : null;
        var concurrencyLimiter = newConfig.ApiRateLimit is { Enabled: true, MaxConcurrentRequests: > 0 } limitCfg
            ? CreateConcurrencyLimiter(limitCfg) : null;

        // Swap every reloadable field atomically. A request that has already captured the old
        // references into locals finishes against them; the next request reads the new ones.
        // _config is swapped last so CORS / client-IP and the limiters always move together.
        Interlocked.Exchange(ref _rateLimiter, rateLimiter);
        Interlocked.Exchange(ref _concurrencyLimiter, concurrencyLimiter);
        Interlocked.Exchange(ref _trustedProxyNetworks, trusted);
        Interlocked.Exchange(ref _corsAllowedOrigins, newConfig.CorsAllowedOrigins);

        // Old limiters are NOT disposed: a concurrent HandleAsync may have captured the old reference
        // and be mid-acquire. Let GC collect them (reload is rare, one retired object per reload).

        _logger.LogInformation(
            "Soft config reloaded: trustedProxies={TrustedProxyCount}, corsOrigins={CorsOriginCount}, rateLimit={RateLimitEnabled}, concurrencyLimit={ConcurrencyEnabled}",
            trusted.Count,
            newConfig.CorsAllowedOrigins?.Count ?? 0,
            rateLimiter is not null,
            concurrencyLimiter is not null);
    }

    /// <summary>
    /// Instance-level client-IP resolution that uses the CURRENT live trusted-proxy list (#136), for
    /// tests that need to observe the effect of <see cref="ApplyConfig"/> without an HttpListener.
    /// Mirrors <see cref="GetClientIp"/>'s forwarding-header logic.
    /// </summary>
    internal string ResolveClientIpLive(IPAddress? remote, string? xForwardedFor, string? xRealIp) =>
        ResolveClientIp(remote, xForwardedFor, xRealIp, Volatile.Read(ref _trustedProxyNetworks));

    /// <summary>
    /// Resolves the CORS origin against the CURRENT live <c>_config</c> (#136), for tests that need
    /// to observe the effect of reloading <c>CorsAllowedOrigins</c> without an HttpListener. Mirrors
    /// <see cref="AddCorsHeaders"/>'s resolution.
    /// </summary>
    internal string? ResolveCorsOriginLive(string? requestOrigin) =>
        ResolveCorsOrigin(requestOrigin, Volatile.Read(ref _corsAllowedOrigins));

    /// <summary>Whether a per-client rate limiter is currently active — exposed for hot-reload tests.</summary>
    internal bool IsRateLimitingEnabled => Volatile.Read(ref _rateLimiter) is not null;

    /// <summary>Whether a global concurrency limiter is currently active — exposed for hot-reload tests.</summary>
    internal bool IsConcurrencyLimitEnabled => Volatile.Read(ref _concurrencyLimiter) is not null;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new HttpListener();
        // IPv6 literals (e.g. "::1") must be bracketed in a URI: http://[::1]:5001/, not http://::1:5001/.
        // IPv4 and hostnames ("127.0.0.1", "localhost", "0.0.0.0") go through as-is (CodeRabbit #181).
        var host = _listenAddress.Contains(':') ? $"[{_listenAddress}]" : _listenAddress;
        _listener.Prefixes.Add($"http://{host}:{_port}/");
        _listener.Start();

        _logger.LogInformation("Management API listening on http://{Address}:{Port}/", _listenAddress, _port);
        // Warn if the operator explicitly exposed the API without a trusted-proxy gate (#90).
        // Both IPv4 and IPv6 loopback are recognized as secure.
        if (_listenAddress is not "127.0.0.1" and not "localhost" and not "::1")
        {
            _logger.LogWarning(
                "Management API is bound to {Address} (non-loopback) — ensure an authenticated reverse " +
                "proxy (Caddy/nginx with TLS + auth) is in front, or the unauthenticated control plane " +
                "is reachable from the network", _listenAddress);
        }
        _listenTask = ListenAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        _listener?.Stop();
        if (_listenTask is not null)
        {
            try { await _listenTask; } catch { }
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener!.GetContextAsync();
                if (ct.IsCancellationRequested) break;
                _ = HandleAsync(ctx);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Management API error");
            }
        }
    }

    #region Router

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        // Capture the current reloadable limiters into locals (#136): a hot reload can swap the
        // fields mid-request, so each request observes a single consistent limiter instance rather
        // than checking one instance and acquiring against a newer/different one.
        var rateLimiter = Volatile.Read(ref _rateLimiter);
        var concurrencyLimiter = Volatile.Read(ref _concurrencyLimiter);

        AddCorsHeaders(ctx);

        if (ctx.Request.HttpMethod == "OPTIONS")
        {
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return;
        }

        // Per-client-IP rate limit (#116) — 429 once the resolved client's token bucket is drained.
        if (rateLimiter is not null)
        {
            var clientIp = GetClientIp(ctx);
            using var lease = await rateLimiter.AcquireAsync(clientIp);
            if (!lease.IsAcquired)
            {
                _logger.LogWarning("API rate limit exceeded for {Ip}", clientIp);
                await WriteResponse(ctx, ApiResponse.Error("Too many requests", 429));
                return;
            }
        }

        var path = ctx.Request.Url!.AbsolutePath;
        var method = ctx.Request.HttpMethod;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Global concurrency cap (#119): hold the lease for the whole request so total in-flight work
        // (RIPEstat fetches / DB ops) is bounded regardless of source. QueueLimit = 0 means acquisition
        // is immediate — either granted or denied with 503 Server busy when at capacity.
        RateLimitLease? concurrencyLease = null;
        if (concurrencyLimiter is not null)
        {
            concurrencyLease = await concurrencyLimiter.AcquireAsync();
            if (!concurrencyLease.IsAcquired)
            {
                concurrencyLease.Dispose();
                _logger.LogWarning("API concurrency limit reached, request rejected");
                await WriteResponse(ctx, ApiResponse.Error("Server busy", 503));
                return;
            }
        }

        try
        {
            var response = await RouteAsync(method, segments, ctx);
            await WriteResponse(ctx, response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Log the full exception detail server-side (sanitized), then map to a stable client
            // response. Raw exception text (EF Core / SQLite / JSON internals — table names,
            // constraint text, file paths) must NOT reach the client: it is reconnaissance surface
            // for an attacker and is misleading (JsonException surfacing as 500 instead of 400, a
            // unique-constraint race as 500 instead of 409) — #157.
            // Cancellation (client disconnect / shutdown) is NOT an error — let it propagate so the
            // host's cancellation handling unwinds cleanly instead of surfacing as a 500.
            _logger.LogError(ex, "API error {Method} {Path}: {Message}",
                SanitizeForLog(method), SanitizeForLog(path), SanitizeForLog(ex.Message));
            var (message, status) = MapExceptionToResponse(ex);
            await WriteResponse(ctx, ApiResponse.Error(message, status));
        }
        finally
        {
            // Release the slot back to the global pool (#119) — also covers the success path so the
            // lease is held exactly for the request duration.
            concurrencyLease?.Dispose();
        }
    }

    /// <summary>
    /// Maps an unhandled exception to a stable, non-revealing client response (#157):
    /// <list type="bullet">
    /// <item><c>JsonException</c> → 400 (malformed JSON body is the client's fault, not a server error).</item>
    /// <item>EF Core unique-constraint violation → 409 (peer already exists — a concurrent duplicate
    /// CreatePeer/UpsertPeer race, not a 500).</item>
    /// <item>Everything else → 500 with a generic message (full detail stays in the server log).</item>
    /// </list>
    /// Extracted as a pure function so the mapping is unit-testable without a live listener.
    /// </summary>
    internal static (string Message, int Status) MapExceptionToResponse(Exception ex)
    {
        // Malformed JSON body — the client's fault.
        if (ex is JsonException)
            return ("Malformed JSON body", 400);

        // EF Core unique-constraint violation (concurrent duplicate insert). SQLite's message
        // contains "UNIQUE constraint failed"; EF wraps it in DbUpdateException. Treat as 409.
        if (ex is Microsoft.EntityFrameworkCore.DbUpdateException)
            return ("The resource already exists or conflicts with the current state", 409);

        // Anything else: generic message, full detail logged server-side.
        return ("Internal server error", 500);
    }

    private async Task<ApiResponse> RouteAsync(string method, string[] segments, HttpListenerContext ctx)
    {
        // /api/server
        if (IsGet(method, segments, "api", "server"))
            return HandleGetServer();

        // /api/me
        if (IsGet(method, segments, "api", "me"))
            return HandleGetMe(ctx);

        // /api/peers
        if (IsPost(method, segments, "api", "peers"))
            return await HandleCreatePeer(ctx);

        // /api/peers/{id}
        if (segments.Length == 3 && segments[0] == "api" && segments[1] == "peers" && method == "GET")
            return HandleGetPeer(segments[2]);
        if (segments.Length == 3 && segments[0] == "api" && segments[1] == "peers" && method == "PUT")
            return await HandleUpdatePeer(segments[2], ctx);
        if (segments.Length == 3 && segments[0] == "api" && segments[1] == "peers" && method == "DELETE")
            return HandleDeletePeer(segments[2]);

        // /api/peers/{id}/prefixes
        if (segments.Length == 4 && segments[0] == "api" && segments[1] == "peers" && segments[3] == "prefixes" && method == "GET")
            return await HandleExportPrefixes(segments[2], ctx);

        // /api/peers/{id}/sources — GET (list), POST (add) (#143)
        if (segments.Length == 4 && segments[0] == "api" && segments[1] == "peers" && segments[3] == "sources")
        {
            if (method == "GET")
                return HandleGetSources(segments[2]);
            if (method == "POST")
                return await HandleAddSource(segments[2], ctx);
        }

        // /api/peers/{id}/sources/{sourceId} — DELETE / PATCH (#143)
        if (segments.Length == 5 && segments[0] == "api" && segments[1] == "peers" && segments[3] == "sources")
        {
            if (method == "DELETE")
                return HandleDeleteSource(segments[2], segments[4]);
            if (method == "PATCH")
                return await HandlePatchSource(segments[2], segments[4], ctx);
        }

        // /api/asn-lists
        if (IsGet(method, segments, "api", "asn-lists"))
            return await HandleGetAsnListsAsync();

        // /api/community-scheme
        if (IsGet(method, segments, "api", "community-scheme"))
            return HandleGetCommunityScheme();

        // /api/sessions
        if (IsGet(method, segments, "api", "sessions"))
            return HandleGetSessions();

        // /api/routes
        if (IsGet(method, segments, "api", "routes"))
            return HandleGetRoutes();

        // /api/as/{asn}/prefixes
        if (segments.Length == 4 && segments[0] == "api" && segments[1] == "as" && segments[3] == "prefixes" && method == "GET")
            return await HandleGetAsnPrefixes(segments[2], ctx);

        return ApiResponse.Error("Not found", 404);
    }

    private static bool IsGet(string method, string[] segments, string s0, string s1)
        => method == "GET" && segments.Length == 2 && segments[0] == s0 && segments[1] == s1;

    private static bool IsPost(string method, string[] segments, string s0, string s1)
        => method == "POST" && segments.Length == 2 && segments[0] == s0 && segments[1] == s1;

    #endregion

    #region Request body reader

    /// <summary>
    /// Reads the request body with a hard size cap (#156): rejects bodies larger than
    /// <see cref="AppConfig.MaxRequestBodyBytes"/> with <c>413 Payload Too Large</c> BEFORE
    /// deserialization. <c>HttpListener</c> has no default body limit, so without this a single
    /// client could stream gigabytes into the process. The cap also covers chunked-transfer bodies
    /// (no Content-Length) via the read-loop's running byte count.
    /// </summary>
    private async Task<(string? Body, ApiResponse? Error)> ReadBodyAsync(HttpListenerContext ctx)
    {
        var maxBytes = _config.MaxRequestBodyBytes;

        // Fast path: Content-Length present and already over the cap → reject without reading.
        if (ctx.Request.ContentLength64 > maxBytes)
            return (null, ApiResponse.Error(
                $"Request body too large ({ctx.Request.ContentLength64} bytes, max {maxBytes}).", 413));

        return await ReadBoundedBodyAsync(ctx.Request.InputStream, maxBytes);
    }

    /// <summary>
    /// Pure body reader with a hard byte cap — extracted for unit testing (#156). Returns
    /// <c>(null, 413-error)</c> when the stream yields more than <paramref name="maxBytes"/> bytes,
    /// otherwise the full body decoded as UTF-8. Covers both sized and chunked/streaming bodies.
    /// </summary>
    internal static async Task<(string? Body, ApiResponse? Error)> ReadBoundedBodyAsync(Stream input, long maxBytes)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > maxBytes)
                return (null, ApiResponse.Error(
                    $"Request body too large (over {maxBytes} bytes).", 413));
        }

        return (Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length), null);
    }

    #endregion

    #region GET /api/server

    private ApiResponse HandleGetServer()
    {
        var bgp = _config.Bgp;
        var ip = bgp.RouterId;
        return ApiResponse.Ok(new
        {
            asn = bgp.Asn,
            routerId = ip,
            bgpPort = 179,
            apiPort = _port,
            holdTime = bgp.HoldTime,
            keepalive = bgp.KeepAlive,
            setup = new[]
            {
                $"router bgp {bgp.Asn}",
                $" neighbor <YOUR_IP> remote-as {bgp.Asn}",
                $" neighbor <YOUR_IP> ebgp-multihop 2",
                $" neighbor <YOUR_IP> update-source <YOUR_INTERFACE>",
                $" neighbor <YOUR_IP> soft-reconfiguration inbound",
                $"!",
                $"address-family ipv4 unicast",
                $" neighbor <YOUR_IP> activate",
                $" neighbor <YOUR_IP> route-map BGPLite-IN in",
                $" neighbor <YOUR_IP> route-map BGPLite-OUT out",
                $"exit-address-family"
            },
            bird = new[]
            {
                $"# ---------- FILTER ----------",
                $"filter bgplite_in {{",
                $"  gw = <YOUR_GATEWAY>;",
                $"  accept;",
                $"}}",
                $"",
                $"# ---------- eBGP ----------",
                $"protocol bgp bgplite {{",
                $"  local as <YOUR_ASN>;",
                $"  neighbor {ip} as {bgp.Asn};",
                $"  source address <YOUR_IP>;",
                $"  multihop;",
                $"  hold time {bgp.HoldTime};",
                $"  ipv4 {{",
                $"    import filter bgplite_in;",
                $"    export none;",
                $"    graceful restart on;",
                $"  }};",
                $"}}"
            },
            mikrotik = new[]
            {
                $"# Apply all lines as-is — full paths => one paste. v7 ties a connection to a BGP instance; output.filter-chain=discard announces nothing back.",
                $"/routing/bgp/instance/add name=bgplite as=<YOUR_ASN> router-id=<YOUR_ROUTER_ID>",
                $"/routing/filter/rule/add chain=discard rule=\"reject;\"",
                $"/routing/filter/rule/add chain=bgplite-in rule=\"set gw <YOUR_GW>; accept;\"",
                $"/routing/bgp/connection/add name=bgplite instance=bgplite afi=ip remote.address={ip}/32 remote.as={bgp.Asn} local.role=ebgp hold-time={bgp.HoldTime}s output.filter-chain=discard input.filter=bgplite-in"
            }
        });
    }

    #endregion

    #region GET /api/me

    private ApiResponse HandleGetMe(HttpListenerContext ctx)
    {
        var clientIp = GetClientIp(ctx);

        // #23: /api/me always returns a `peers` array. When several peers share one source IP
        // (NAT/VPN), each is a distinct record (composite (Ip, Asn) key, #19).
        //
        // - ?asn=64512 → resolve that specific peer via GetPeer(ip, asn). Malformed → 400.
        // - No ?asn= → return ALL peers at this IP.
        // - Always `peers: [...]` (array), even for a single peer.

        var asnQuery = ctx.Request.QueryString["asn"];
        List<PeerInfo> peerInfos;
        if (asnQuery is not null)
        {
            if (!uint.TryParse(asnQuery, out var asn))
                return ApiResponse.Error($"Invalid 'asn' query parameter: '{asnQuery}'. Must be a non-negative integer.", 400);
            var single = _store.GetPeer(clientIp, asn);
            peerInfos = single is null ? [] : [single];
        }
        else
        {
            peerInfos = _store.GetPeersByIp(clientIp);
        }

        var details = peerInfos.Select(p => BuildPeerDetail(p.Id)).Where(d => d is not null).ToList()!;
        return ApiResponse.Ok(new { ip = clientIp, peers = details });
    }

    /// <summary>Builds the peer-detail anonymous object for /api/me. Returns null if the peer vanished.</summary>
    private object? BuildPeerDetail(string peerId)
    {
        var peer = _store.GetDbPeerById(peerId);
        if (peer is null) return null;

        var subscriptions = _store.GetSubscriptions(peer.Id);
        var customPrefixes = _store.GetCustomPrefixes(peer.Id);
        var customAsns = _store.GetCustomAsns(peer.Id);
        // #23: communities are per-peer (keyed by (Ip, Asn)), not per-IP.
        var communities = peer.Asn.HasValue
            ? _store.GetCommunities(peer.Ip, peer.Asn.Value)
            : _store.GetCommunitiesByIp(peer.Ip);

        return new
        {
            id = peer.Id,
            ip = peer.Ip,
            asn = peer.Asn,
            description = peer.Description,
            status = peer.Status,
            createdAt = peer.CreatedAt,
            lastSessionAt = peer.LastSessionAt,
            lists = subscriptions,
            customPrefixes,
            customAsns,
            communities = communities.Select(CommunityCodec.Format),
            allRoutes = communities.Count == 0
        };
    }

    #endregion

    #region /api/peers

    private async Task<ApiResponse> HandleCreatePeer(HttpListenerContext ctx)
    {
        var (body, bodyError) = await ReadBodyAsync(ctx);
        if (bodyError is not null) return bodyError;
        var data = JsonSerializer.Deserialize<CreatePeerRequest>(body!, _jsonOpts);

        if (data is null)
            return ApiResponse.Error("Invalid request body", 400);

        var asnLists = data.AsnLists ?? [];
        var customPrefixes = new List<(string Prefix, byte Length)>();

        _logger.LogInformation("CreatePeer deserialized: AsnLists={Lists}, CustomPrefixes={Prefixes}, CustomAsns={Asns}",
            string.Join(",", asnLists), string.Join(",", data.CustomPrefixes ?? []),
            string.Join(",", data.CustomAsns ?? []));

        if (data.CustomPrefixes is not null)
        {
            foreach (var cidr in data.CustomPrefixes)
            {
                var parsed = ParseCustomPrefix(cidr);
                if (parsed is null)
                    return ApiResponse.Error($"Invalid CIDR: {cidr}", 400);
                customPrefixes.Add(parsed.Value);
            }
        }

        var id = _store.CreatePeer(data.Ip, data.Asn, data.Description);
        _store.SetSubscriptions(id, asnLists);
        _store.SetCustomPrefixes(id, customPrefixes);
        _store.SetCustomAsns(id, data.CustomAsns ?? []);

        var peer = _store.GetDbPeerById(id);

        _logger.LogInformation("Created peer {Ip} AS{Asn} ({Id}): {Subs} lists, {Prefixes} custom prefixes, {Asns} custom AS",
            data.Ip, data.Asn, id, asnLists.Count, customPrefixes.Count, data.CustomAsns?.Count ?? 0);

        if (_sessionManager is not null)
            _ = _sessionManager.RefreshPeerAsync(data.Ip);

        return ApiResponse.Ok(new
        {
            id,
            ip = data.Ip,
            asn = data.Asn,
            description = data.Description,
            status = peer?.Status ?? "inactive",
            createdAt = peer?.CreatedAt,
            lists = asnLists,
            customPrefixes = data.CustomPrefixes ?? [],
            customAsns = data.CustomAsns ?? []
        });
    }

    private ApiResponse HandleGetPeer(string peerId)
    {
        var peer = _store.GetDbPeerById(peerId);
        if (peer is null)
            return ApiResponse.Error("Peer not found", 404);

        var subscriptions = _store.GetSubscriptions(peer.Id);
        var customPrefixes = _store.GetCustomPrefixes(peer.Id);
        var customAsns = _store.GetCustomAsns(peer.Id);
        var customSources = _store.GetCustomSources(peer.Id);
        var communities = _store.GetCommunitiesByIp(peer.Ip);

        return ApiResponse.Ok(new
        {
            id = peer.Id,
            ip = peer.Ip,
            asn = peer.Asn,
            description = peer.Description,
            status = peer.Status,
            createdAt = peer.CreatedAt,
            lastSessionAt = peer.LastSessionAt,
            lists = subscriptions,
            customPrefixes,
            customAsns,
            customSources = customSources.Select(s => new { id = s.Id, name = s.Name, url = s.Url, community = s.Community, active = s.Active }),
            communities = communities.Select(CommunityCodec.Format),
            allRoutes = communities.Count == 0
        });
    }

    private async Task<ApiResponse> HandleUpdatePeer(string peerId, HttpListenerContext ctx)
    {
        var peer = _store.GetDbPeerById(peerId);
        if (peer is null)
            return ApiResponse.Error("Peer not found", 404);

        var (body, bodyError) = await ReadBodyAsync(ctx);
        if (bodyError is not null) return bodyError;
        var data = JsonSerializer.Deserialize<UpdatePeerRequest>(body!, _jsonOpts);

        if (data is null)
            return ApiResponse.Error("Invalid request body", 400);

        // Validate ALL custom prefixes BEFORE any mutation so a bad prefix rejects the whole
        // request with a 400 without partial mutation (#100). parsedPrefixes stays null when the
        // field is omitted so existing prefixes are preserved (partial-update semantics: omitting a
        // field must not wipe it — same as Description/Lists above and CustomAsns below).
        List<(string Prefix, byte Length)>? parsedPrefixes = null;
        if (data.CustomPrefixes is not null)
        {
            parsedPrefixes = [];
            foreach (var cidr in data.CustomPrefixes)
            {
                var parsed = ParseCustomPrefix(cidr);
                if (parsed is null)
                    return ApiResponse.Error($"Invalid CIDR: {cidr}", 400);
                parsedPrefixes.Add(parsed.Value);
            }
        }

        _logger.LogInformation("UpdatePeer {Id}: CustomPrefixes={Count}, CustomAsns={AsnCount}",
            SanitizeForLog(peerId), parsedPrefixes?.Count ?? 0, data.CustomAsns?.Count ?? 0);

        if (data.Description is not null)
            _store.SetDescription(peerId, data.Description);

        if (data.Lists is not null)
            _store.SetSubscriptions(peerId, data.Lists);

        if (parsedPrefixes is not null)
            _store.SetCustomPrefixes(peerId, parsedPrefixes);
        if (data.CustomAsns is not null)
            _store.SetCustomAsns(peerId, data.CustomAsns);

        _logger.LogInformation("Updated peer {Id}", SanitizeForLog(peerId));

        if (_sessionManager is not null)
            _ = _sessionManager.RefreshPeerAsync(peer.Ip);

        return HandleGetPeer(peerId);
    }

    private ApiResponse HandleDeletePeer(string peerId)
    {
        var peer = _store.GetDbPeerById(peerId);
        if (peer is null)
            return ApiResponse.Error("Peer not found", 404);

        _store.DeletePeer(peerId);
        _logger.LogInformation("Deleted peer {Id} ({Ip})", SanitizeForLog(peerId), peer.Ip);
        return ApiResponse.Ok(new { id = peerId, deleted = true });
    }

    #endregion

    #region /api/peers/{id}/sources (#143)

    private ApiResponse HandleGetSources(string peerId)
    {
        if (_store.GetDbPeerById(peerId) is null)
            return ApiResponse.Error("Peer not found", 404);

        var sources = _store.GetCustomSources(peerId);
        return ApiResponse.Ok(sources.Select(s => new { id = s.Id, name = s.Name, url = s.Url, community = s.Community, active = s.Active }));
    }

    private async Task<ApiResponse> HandleAddSource(string peerId, HttpListenerContext ctx)
    {
        if (_store.GetDbPeerById(peerId) is null)
            return ApiResponse.Error("Peer not found", 404);

        var (body, bodyError) = await ReadBodyAsync(ctx);
        if (bodyError is not null) return bodyError;
        var data = JsonSerializer.Deserialize<AddSourceRequest>(body!, _jsonOpts);

        if (data is null || string.IsNullOrWhiteSpace(data.Name) || string.IsNullOrWhiteSpace(data.Url))
            return ApiResponse.Error("Name and Url are required", 400);

        if (!Uri.TryCreate(data.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return ApiResponse.Error($"Invalid URL: {data.Url}", 400);

        var source = _store.AddCustomSource(peerId, data.Name, data.Url, data.Community);

        _logger.LogInformation("Added source '{Name}' ({Url}) to peer {PeerId}",
            SanitizeForLog(data.Name), SanitizeForLog(data.Url), SanitizeForLog(peerId));
        return ApiResponse.Ok(new { id = source.Id, name = source.Name, url = source.Url, community = source.Community, active = source.Active });
    }

    private ApiResponse HandleDeleteSource(string peerId, string sourceId)
    {
        if (_store.GetDbPeerById(peerId) is null)
            return ApiResponse.Error("Peer not found", 404);

        if (!_store.DeleteCustomSource(peerId, sourceId))
            return ApiResponse.Error($"Source '{sourceId}' not found", 404);

        _logger.LogInformation("Deleted source {SourceId} from peer {PeerId}", SanitizeForLog(sourceId), SanitizeForLog(peerId));
        return ApiResponse.Ok(new { id = sourceId, deleted = true });
    }

    private async Task<ApiResponse> HandlePatchSource(string peerId, string sourceId, HttpListenerContext ctx)
    {
        if (_store.GetDbPeerById(peerId) is null)
            return ApiResponse.Error("Peer not found", 404);

        var (body, bodyError) = await ReadBodyAsync(ctx);
        if (bodyError is not null) return bodyError;
        var data = JsonSerializer.Deserialize<PatchSourceRequest>(body!, _jsonOpts);

        if (data is null || data.Active is null)
            return ApiResponse.Error("PATCH body must contain { \"active\": true/false }", 400);

        if (!_store.SetSourceActive(peerId, sourceId, data.Active.Value))
            return ApiResponse.Error($"Source '{sourceId}' not found", 404);

        _logger.LogInformation("Source {SourceId} active={Active}", SanitizeForLog(sourceId), data.Active.Value);
        return ApiResponse.Ok(new { id = sourceId, active = data.Active.Value });
    }

    #endregion

    #region /api/peers/{id}/prefixes

    private async Task<ApiResponse> HandleExportPrefixes(string peerId, HttpListenerContext ctx)
    {
        var peer = _store.GetDbPeerById(peerId);
        if (peer is null)
            return ApiResponse.Error("Peer not found", 404);

        var prefixes = await CollectPeerPrefixes(peerId);

        var format = ctx.Request.QueryString["format"] ?? "txt";
        if (format == "json")
            return ApiResponse.Ok(prefixes);

        ctx.Response.ContentType = "text/plain";
        return ApiResponse.Ok(string.Join("\n", prefixes));
    }

    private async Task<List<string>> CollectPeerPrefixes(string peerId)
    {
        var prefixes = new List<string>();

        // Custom prefixes
        prefixes.AddRange(_store.GetCustomPrefixes(peerId));

        if (_prefixService is null)
            return prefixes.Distinct().OrderBy(p => p).ToList();

        var subscriptions = _store.GetSubscriptions(peerId);
        var subscribedLists = _config.RipeStat?.AsnLists
            .Where(l => subscriptions.Contains(l.Name))
            .ToList() ?? [];

        // ASN-based lists
        var asns = subscribedLists.Where(l => l.Asns.Count > 0).SelectMany(l => l.Asns).ToList();
        if (asns.Count > 0)
        {
            try
            {
                var fetched = await _prefixService.GetPrefixesForAsns(asns);
                foreach (var (prefix, length, _) in fetched)
                    prefixes.Add($"{BgpConstants.UintToIPAddress(prefix)}/{length}");
            }
            catch (Exception ex) { _logger.LogWarning(ex, "CollectPeerPrefixes: ASN fetch failed"); }
        }

        // Country-based lists
        if (subscribedLists.Any(l => l.Asns.Count == 0 && l.Country is not null))
        {
            try
            {
                var ruPrefixes = await _prefixService.GetRuPrefixesAsync();
                foreach (var (prefix, length, _) in ruPrefixes)
                    prefixes.Add($"{BgpConstants.UintToIPAddress(prefix)}/{length}");
            }
            catch (Exception ex) { _logger.LogWarning(ex, "CollectPeerPrefixes: RU prefix fetch failed"); }
        }

        return prefixes.Distinct().OrderBy(p => p).ToList();
    }

    #endregion

    #region /api/peers/{id}/communities

    #endregion

    #region GET /api/asn-lists

    private async Task<ApiResponse> HandleGetAsnListsAsync()
    {
        var lists = _config.RipeStat?.AsnLists ?? [];
        var result = new List<object>();

        foreach (var l in lists)
        {
            int prefixCount = 0;
            if (_prefixService is not null)
            {
                if (l.Asns.Count > 0)
                {
                    foreach (var asn in l.Asns)
                    {
                        try { prefixCount += await _prefixService.GetPrefixCountAsync(asn); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Failed to get prefix count for AS{Asn}", asn); }
                    }
                }
                else if (l.Country is not null)
                {
                    try { prefixCount = (await _prefixService.GetRuPrefixesAsync()).Count; }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to get RU prefix count"); }
                }
            }

            result.Add(new
            {
                id = l.Name,
                l.Name,
                l.Description,
                l.Country,
                Community = l.Community,
                prefixCount,
                type = l.Country is not null ? "country" : "asn"
            });
        }

        // Append configured PrefixSources (file/http) alongside the legacy RipeStat ASN-lists,
        // reusing the same response shape. "Kind" is intentionally not exposed.
        if (_prefixSources is not null)
        {
            var seen = lists.Select(l => l.Name).ToHashSet();
            foreach (var (source, prefixes) in await _prefixSources.LoadAllAsync())
            {
                if (!seen.Add(source.Name)) continue; // skip names already present (e.g. shared "ru")
                result.Add(new
                {
                    id = source.Name,
                    Name = source.Name,
                    Description = source.Description,
                    Country = (string?)null,
                    Community = source.Community,
                    prefixCount = prefixes.Count,
                    type = source.Kind == "asn" ? "asn" : "list"
                });
            }
        }

        return ApiResponse.Ok(result);
    }

    #endregion

    #region GET /api/community-scheme

    // Static community scheme for the per-peer custom categories, so the UI can show the community
    // a peer's custom prefixes / custom-AS prefixes will carry before they are advertised.
    // Config overrides win; otherwise the hardcoded defaults "<Asn>:100" / "<Asn>:200".
    private ApiResponse HandleGetCommunityScheme()
    {
        var asn = _config.Bgp.Asn;
        return ApiResponse.Ok(new
        {
            asn,
            customPrefixes = _config.CustomPrefixCommunity ?? $"{asn}:100",
            customAsns = _config.CustomAsnCommunity ?? $"{asn}:200"
        });
    }

    #endregion

    #region GET /api/sessions

    private ApiResponse HandleGetSessions()
    {
        return ApiResponse.Ok(new
        {
            active = _metrics.ActiveSessions
        });
    }

    #endregion

    #region GET /api/routes

    private ApiResponse HandleGetRoutes()
    {
        var routes = _routeTable.GetAll();
        var byCommunity = routes
            .SelectMany(r => r.Communities.Length == 0
                ? [(community: 0u, route: r)]
                : r.Communities.Select(c => (community: c, route: r)))
            .GroupBy(x => x.community)
            .ToDictionary(g => g.Key == 0 ? "default" : CommunityCodec.Format(g.Key), g => g.Count());

        return ApiResponse.Ok(new { total = routes.Count, byCommunity });
    }

    #endregion

    #region GET /api/as/{asn}/prefixes

    private async Task<ApiResponse> HandleGetAsnPrefixes(string asnStr, HttpListenerContext ctx)
    {
        if (!uint.TryParse(asnStr, out var asn))
            return ApiResponse.Error("Invalid ASN", 400);

        var countOnly = ctx.Request.QueryString["count"] == "true";

        if (countOnly)
        {
            if (_prefixService is null)
                return ApiResponse.Error("Prefix service not available", 503);

            try
            {
                var count = await _prefixService.GetPrefixCountAsync(asn);
                return ApiResponse.Ok(new { asn, prefixCount = count });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // #157: log the real detail server-side; return a generic message so RIPEstat /
                // provider internals do not reach the client. Cancellation (client disconnect /
                // shutdown) is NOT an error — let it propagate instead of surfacing as a 500.
                _logger.LogWarning(ex, "GetAsnPrefixes failed for AS{Asn}", asn);
                var (message, status) = MapExceptionToResponse(ex);
                return ApiResponse.Error(message, status);
            }
        }

        return ApiResponse.Ok(new { asn, message = "Use ?count=true for prefix count" });
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Strips CR/LF and other control characters (and truncates) from a user-controlled string so it
    /// cannot forge log lines when rendered by a log sink (CodeQL cs/log-forging). Structured logging
    /// (<c>{...}</c> placeholders) is the primary mitigation; this is defense-in-depth on the value.
    /// </summary>
    internal static string SanitizeForLog(string? value, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(char.IsControl(ch) ? ' ' : ch);
            if (sb.Length >= maxLength) { sb.Append('…'); break; }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parses a single user-supplied custom prefix CIDR ("<prefix>/<length>") into a validated
    /// (prefix, mask length) tuple. Splitting is done on the first '/', requiring exactly two
    /// parts. The prefix is validated with <see cref="IPAddress.TryParse"/> (IPv4 only — IPv6 is
    /// rejected); the length must parse as a byte in 0..32. Returns null on any failure so callers
    /// can reject the whole request with a 400 before touching the store (no partial mutation).
    /// Extracted as a pure helper for unit tests (#100).
    /// </summary>
    internal static (string Prefix, byte Length)? ParseCustomPrefix(string? cidr)
    {
        if (string.IsNullOrWhiteSpace(cidr))
            return null;

        var parts = cidr.Split('/');
        if (parts.Length != 2)
            return null;

        var prefix = parts[0];
        var lengthStr = parts[1];

        // IPv4 only: reject IPv6 (and any non-IP string).
        if (!IPAddress.TryParse(prefix, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork)
            return null;

        // Length must be a byte in the valid IPv4 range 0..32 (rejects 33..255, negatives, non-numeric).
        if (!byte.TryParse(lengthStr, out byte length) || length > 32)
            return null;

        return (prefix, length);
    }

    private string GetClientIp(HttpListenerContext ctx) =>
        ResolveClientIp(
            ctx.Request.RemoteEndPoint?.Address,
            ctx.Request.Headers["X-Forwarded-For"],
            ctx.Request.Headers["X-Real-IP"],
            Volatile.Read(ref _trustedProxyNetworks));

    /// <summary>
    /// Builds the per-client-IP token-bucket rate limiter for the management API (#116). Each distinct
    /// resolved client IP (see <see cref="GetClientIp"/>) gets its own token bucket; a request is
    /// rejected with 429 once its bucket is exhausted. Tunable via <see cref="ApiRateLimitConfig"/>; the
    /// limiter is only created when the operator opts in (ApiRateLimit section present + Enabled).
    /// Extracted as a pure factory for unit tests.
    /// </summary>
    internal static PartitionedRateLimiter<string> CreateRateLimiter(ApiRateLimitConfig cfg)
    {
        var options = new TokenBucketRateLimiterOptions
        {
            TokenLimit = Math.Max(1, cfg.TokenLimit),
            TokensPerPeriod = Math.Max(1, cfg.TokensPerPeriod),
            ReplenishmentPeriod = TimeSpan.FromSeconds(Math.Max(1, cfg.PeriodSeconds)),
            QueueLimit = 0,         // deny immediately (429) when no tokens — never queue
            AutoReplenishment = true
        };
        return PartitionedRateLimiter.Create<string, string>(
            ip => RateLimitPartition.GetTokenBucketLimiter(ip, _ => options));
    }

    /// <summary>
    /// Builds the GLOBAL concurrency limiter for the management API (#119). A single non-partitioned
    /// <see cref="ConcurrencyLimiter"/> with <see cref="ConcurrencyLimiterOptions.PermitLimit"/> =
    /// <see cref="ApiRateLimitConfig.MaxConcurrentRequests"/> and
    /// <see cref="ConcurrencyLimiterOptions.QueueLimit"/> = 0, so at most PermitLimit requests run at
    /// once across ALL clients; the next is denied immediately (503) rather than queued. Only created
    /// when the operator opts in (MaxConcurrentRequests &gt; 0 and ApiRateLimit enabled). Extracted as a
    /// pure factory for unit tests.
    /// </summary>
    internal static ConcurrencyLimiter CreateConcurrencyLimiter(ApiRateLimitConfig cfg)
    {
        var options = new ConcurrencyLimiterOptions
        {
            PermitLimit = Math.Max(1, cfg.MaxConcurrentRequests),
            QueueLimit = 0,         // deny immediately (503) when at capacity — never queue
        };
        return new ConcurrencyLimiter(options);
    }

    /// <summary>
    /// Resolves the real client IP from the connection's remote endpoint and forwarding headers.
    /// Forwarding headers are honored ONLY when the immediate peer (<paramref name="remote"/>) is a
    /// configured trusted proxy (#91) — a direct client cannot inject <c>X-Forwarded-For</c> /
    /// <c>X-Real-IP</c>. <c>X-Forwarded-For</c> is walked right-to-left and the first hop that is not
    /// itself a trusted proxy is returned, defeating injection through the proxy. Extracted as a pure
    /// function so the security logic is unit-testable without an HttpListener.
    /// </summary>
    internal static string ResolveClientIp(IPAddress? remote, string? xForwardedFor, string? xRealIp, IReadOnlyList<IPNetwork> trustedProxies)
    {
        if (remote is null) return "unknown";

        // HttpListener on a dual-stack (http://+) listener reports IPv4 peers as IPv4-mapped IPv6
        // (::ffff:x.x.x.x); normalize so IPv4 trusted-proxy CIDRs match and the returned string is clean.
        IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
        remote = Normalize(remote);

        bool IsTrusted(IPAddress ip) => trustedProxies.Count > 0 && trustedProxies.Any(n => n.Contains(ip));

        // Direct (non-proxy) client: never trust client-supplied forwarding headers.
        if (!IsTrusted(remote))
            return remote.ToString();

        // Trusted proxy: recover the original client from X-Forwarded-For, walking right-to-left past
        // trusted hops (the proxy appends the real client on the right; attacker-controlled entries
        // land on the left and are skipped).
        if (!string.IsNullOrWhiteSpace(xForwardedFor))
        {
            foreach (var raw in xForwardedFor.Split(',').Reverse())
            {
                var hop = raw.Trim();
                if (hop.Length == 0) continue;
                if (IPAddress.TryParse(hop, out var ip))
                {
                    var normalized = Normalize(ip);
                    if (!IsTrusted(normalized)) return normalized.ToString();
                }
            }
        }

        // Single-hop proxies commonly set X-Real-IP instead of (or alongside) X-Forwarded-For.
        // Validate + normalize so a malformed header can't surface garbage (e.g. newlines for log
        // forging) — fall back to the proxy address if it isn't a parseable IP.
        if (!string.IsNullOrWhiteSpace(xRealIp) && IPAddress.TryParse(xRealIp.Trim(), out var realAddr))
            return Normalize(realAddr).ToString();

        return remote.ToString();
    }

    /// <summary>Parses configured TrustedProxies CIDRs (or bare IPs → /32 or /128) into IPNetworks.
    /// Unparseable entries are logged and skipped (that CIDR simply won't be trusted).</summary>
    private IReadOnlyList<IPNetwork> ParseTrustedProxies(List<string>? cidrs)
    {
        var nets = new List<IPNetwork>();
        if (cidrs is null || cidrs.Count == 0) return nets;

        foreach (var entry in cidrs)
        {
            var s = entry.Trim();
            if (s.Length == 0) continue;

            if (IPNetwork.TryParse(s, out var net)) { nets.Add(net); continue; }

            if (IPAddress.TryParse(s, out var ip))
            {
                nets.Add(new IPNetwork(ip, ip.GetAddressBytes().Length * 8)); // /32 (IPv4) or /128 (IPv6)
                continue;
            }

            _logger.LogWarning("Ignoring unparseable TrustedProxies entry '{Entry}'.", s);
        }
        return nets;
    }

    private async Task WriteResponse(HttpListenerContext ctx, ApiResponse response)
    {
        ctx.Response.StatusCode = response.StatusCode;
        ctx.Response.ContentType = "application/json";
        // Pass the cached JsonSerializerOptions (#105 aot/perf) — without it, Serialize falls back to
        // default per-call options (reflection + no caching), a perf regression on every response.
        var json = JsonSerializer.Serialize(response.Body, _jsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    /// <summary>
    /// Resolves the CORS <c>Access-Control-Allow-Origin</c> value for a request (#99). Returns the
    /// request's own <paramref name="requestOrigin"/> when it is non-empty AND exactly matches an
    /// entry in <paramref name="allowed"/> (case-insensitive) — never <c>"*"</c> and never a
    /// non-allowlisted origin, so an untrusted client cannot trick the API into reflecting an
    /// arbitrary origin. Returns <c>null</c> for an absent/empty origin or an empty/absent
    /// allowlist, which <see cref="AddCorsHeaders"/> maps to "no CORS headers emitted" (CORS
    /// disabled — the secure default). Extracted as a pure function for unit tests.
    /// </summary>
    internal static string? ResolveCorsOrigin(string? requestOrigin, IReadOnlyList<string>? allowed)
    {
        if (string.IsNullOrEmpty(requestOrigin)) return null;
        if (allowed is null || allowed.Count == 0) return null;
        foreach (var entry in allowed)
        {
            if (string.Equals(entry, requestOrigin, StringComparison.OrdinalIgnoreCase))
                return requestOrigin;
        }
        return null;
    }

    private void AddCorsHeaders(HttpListenerContext ctx)
    {
        // #99: gate CORS on an explicit origin allowlist. ResolveCorsOrigin returns the request's
        // own Origin only when allowlisted, else null. Null => emit NO Access-Control-Allow-Origin
        // (CORS disabled — secure default); matched => reflect the origin with Vary: Origin so caches
        // key by origin. Allow-Methods/Allow-Headers are emitted only alongside a real ACAO.
        // The allowlist is read off the live _config (#136) so a hot reload of CorsAllowedOrigins
        // takes effect on the next request without a restart.
        var origin = ResolveCorsOrigin(ctx.Request.Headers["Origin"], Volatile.Read(ref _corsAllowedOrigins));
        if (origin is null) return;
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", origin);
        ctx.Response.Headers.Add("Vary", "Origin");
        ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
        ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
    }

    #endregion

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();
        // At shutdown no requests are in-flight, so disposing the current limiters is safe
        // (unlike ApplyConfig mid-flight where we leave old ones for GC per #137's CodeRabbit fix).
        Volatile.Read(ref _rateLimiter)?.Dispose();
        Volatile.Read(ref _concurrencyLimiter)?.Dispose();
        _listener?.Close();
    }

    private record CreatePeerRequest(string Ip, uint Asn, string? Description, [property: JsonPropertyName("lists")] List<string>? AsnLists, List<string>? CustomPrefixes, List<uint>? CustomAsns);
    private record UpdatePeerRequest(string? Description, [property: JsonPropertyName("lists")] List<string>? Lists, List<string>? CustomPrefixes, List<uint>? CustomAsns);
    private record AddSourceRequest(string Name, string Url, string? Community);
    private record PatchSourceRequest([property: JsonPropertyName("active")] bool? Active);

    internal record ApiResponse(object? Body, int StatusCode = 200)
    {
        public static ApiResponse Ok(object data) => new(data);
        public static ApiResponse Error(string message, int code) => new(new { error = message }, code);
    }

    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };
}
