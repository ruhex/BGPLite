using System.Net;
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
    private readonly AppConfig _config;
    private readonly BgpMetrics _metrics;
    private readonly IPrefixService? _prefixService;
    private readonly IPrefixSourceService? _prefixSources;
    private readonly ISessionManager? _sessionManager;
    private readonly ILogger<ManagementApi> _logger;
    private readonly int _port;
    private readonly IReadOnlyList<IPNetwork> _trustedProxyNetworks;
    private readonly PartitionedRateLimiter<string>? _rateLimiter;
    private HttpListener? _listener;
    private Task? _listenTask;
    private CancellationTokenSource _cts = new();

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
        _trustedProxyNetworks = ParseTrustedProxies(config.TrustedProxies);
        // Opt-in (#116): no rate limiting unless an ApiRateLimit section is configured, so the live
        // service's behavior is unchanged until the operator enables it.
        _rateLimiter = config.ApiRateLimit is { Enabled: true } cfg ? CreateRateLimiter(cfg) : null;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{_port}/");
        _listener.Start();

        _logger.LogInformation("Management API listening on http://+:{Port}/", _port);
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
        AddCorsHeaders(ctx);

        if (ctx.Request.HttpMethod == "OPTIONS")
        {
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return;
        }

        // Per-client-IP rate limit (#116) — 429 once the resolved client's token bucket is drained.
        if (_rateLimiter is not null)
        {
            var clientIp = GetClientIp(ctx);
            using var lease = await _rateLimiter.AcquireAsync(clientIp);
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

        try
        {
            var response = await RouteAsync(method, segments, ctx);
            await WriteResponse(ctx, response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "API error {Method} {Path}: {Message}",
                SanitizeForLog(method), SanitizeForLog(path), SanitizeForLog(ex.Message));
            await WriteResponse(ctx, ApiResponse.Error(ex.InnerException?.Message ?? ex.Message, 500));
        }
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

        var peerInfo = _store.GetPeerByIp(clientIp);
        if (peerInfo is null)
            return ApiResponse.Ok(new { ip = clientIp, peer = (object?)null });

        var peer = _store.GetDbPeerById(peerInfo.Id);
        if (peer is null)
            return ApiResponse.Ok(new { ip = clientIp, peer = (object?)null });

        var subscriptions = _store.GetSubscriptions(peer.Id);
        var customPrefixes = _store.GetCustomPrefixes(peer.Id);
        var customAsns = _store.GetCustomAsns(peer.Id);
        var communities = _store.GetCommunitiesByIp(clientIp);

        return ApiResponse.Ok(new
        {
            ip = clientIp,
            peer = new
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
            }
        });
    }

    #endregion

    #region /api/peers

    private async Task<ApiResponse> HandleCreatePeer(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var data = JsonSerializer.Deserialize<CreatePeerRequest>(body, _jsonOpts);

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
                var slash = cidr.IndexOf('/');
                if (slash < 0)
                    return ApiResponse.Error($"Invalid CIDR: {cidr}", 400);
                var prefix = cidr[..slash];
                var length = byte.Parse(cidr[(slash + 1)..]);
                customPrefixes.Add((prefix, length));
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
            communities = communities.Select(CommunityCodec.Format),
            allRoutes = communities.Count == 0
        });
    }

    private async Task<ApiResponse> HandleUpdatePeer(string peerId, HttpListenerContext ctx)
    {
        var peer = _store.GetDbPeerById(peerId);
        if (peer is null)
            return ApiResponse.Error("Peer not found", 404);

        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var data = JsonSerializer.Deserialize<UpdatePeerRequest>(body, _jsonOpts);

        if (data is null)
            return ApiResponse.Error("Invalid request body", 400);

        if (data.Description is not null)
            _store.SetDescription(peerId, data.Description);

        if (data.Lists is not null)
            _store.SetSubscriptions(peerId, data.Lists);

        var customPrefixes = data.CustomPrefixes ?? [];
        _logger.LogInformation("UpdatePeer {Id}: CustomPrefixes={Count}, CustomAsns={AsnCount}",
            SanitizeForLog(peerId), customPrefixes.Count, (data.CustomAsns ?? []).Count);
        var parsed = new List<(string, byte)>();
        foreach (var cidr in customPrefixes)
        {
            var slash = cidr.IndexOf('/');
            if (slash < 0) return ApiResponse.Error($"Invalid CIDR: {cidr}", 400);
            parsed.Add((cidr[..slash], byte.Parse(cidr[(slash + 1)..])));
        }
        _store.SetCustomPrefixes(peerId, parsed);

        _store.SetCustomAsns(peerId, data.CustomAsns ?? []);

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
            catch { }
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
            catch { }
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
            catch (Exception ex)
            {
                return ApiResponse.Error(ex.Message, 500);
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

    private string GetClientIp(HttpListenerContext ctx) =>
        ResolveClientIp(
            ctx.Request.RemoteEndPoint?.Address,
            ctx.Request.Headers["X-Forwarded-For"],
            ctx.Request.Headers["X-Real-IP"],
            _trustedProxyNetworks);

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

    private static async Task WriteResponse(HttpListenerContext ctx, ApiResponse response)
    {
        ctx.Response.StatusCode = response.StatusCode;
        ctx.Response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(response.Body);
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
        var origin = ResolveCorsOrigin(ctx.Request.Headers["Origin"], _config.CorsAllowedOrigins);
        if (origin is null) return;
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", origin);
        ctx.Response.Headers.Add("Vary", "Origin");
        ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
        ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
    }

    #endregion

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _listener?.Close();
    }

    private record CreatePeerRequest(string Ip, uint Asn, string? Description, [property: JsonPropertyName("lists")] List<string>? AsnLists, List<string>? CustomPrefixes, List<uint>? CustomAsns);
    private record UpdatePeerRequest(string? Description, [property: JsonPropertyName("lists")] List<string>? Lists, List<string>? CustomPrefixes, List<uint>? CustomAsns);

    private record ApiResponse(object? Body, int StatusCode = 200)
    {
        public static ApiResponse Ok(object data) => new(data);
        public static ApiResponse Error(string message, int code) => new(new { error = message }, code);
    }

    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };
}
