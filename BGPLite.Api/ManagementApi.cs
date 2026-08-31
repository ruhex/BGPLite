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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BGPLite.Contracts;

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
    // #330: limiters swapped out by ApplyConfig, disposed in StopAsync/Dispose after in-flight
    // requests have drained — a retired TokenBucketRateLimiter with AutoReplenishment roots a
    // replenish Timer that GC never collects, so "let GC handle it" leaked one timer per reload.
    private readonly List<IDisposable> _retiredLimiters = [];
    private IReadOnlyList<string>? _corsAllowedOrigins;
    private readonly BgpMetrics _metrics;
    // #263: required, not optional. Each of these was a silent feature switch: without
    // _sessionManager a peer edited in the UI was persisted but never pushed to its live session,
    // and without _prefixService the prefix views reported zero instead of failing.
    private readonly IPrefixService _prefixService;
    private readonly IPrefixSourceService _prefixSources;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<ManagementApi> _logger;
    private readonly int _port;
    private readonly string _listenAddress;  // #90: bind address — loopback by default

    /// <summary>
    /// #238: default in-flight cap for accepted-but-not-yet-completed requests. The #119
    /// concurrency limiter is opt-in; without this bound a connection burst spawns unbounded
    /// fire-and-forget tasks, each potentially doing DB work or RIPEstat fetches. Backpressure
    /// is applied in the accept loop: at capacity it waits for a slot instead of spawning.
    /// </summary>
    private const int DefaultInflightCap = 64;
    private readonly SemaphoreSlim _inflightCap = new(DefaultInflightCap);
    // #258: in-flight tracking is a COUNT plus an idle Task (completed whenever the count is 0),
    // not a List<Task> — the #248 design appended every handler and never removed it, so each
    // completed request leaked its Task (and its exception, if faulted) for the process lifetime
    // and the drain snapshot grew without bound. StopAsync drains by awaiting the idle task.
    private readonly object _inflightSync = new();
    private int _inflightCount;
    private TaskCompletionSource? _inflightActive;
    private Task _inflightIdle = Task.CompletedTask;

    /// <summary>Currently in-flight request handlers (#258) — bounded by the cap, zero when idle.</summary>
    internal int InflightRequestCount { get { lock (_inflightSync) return _inflightCount; } }
    private HttpListener? _listener;
    private Task? _listenTask;
    private readonly CancellationTokenSource _cts = new();
    // #326: cancelled at the top of StopAsync so in-flight handlers stop provider work (RIPEstat
    // fetches) instead of pinning the drain — the host stops services in reverse registration
    // order, and BgpServer.StopAsync (the Cease + socket teardown) waits behind this drain.
    // Deliberately never disposed: handlers abandoned by the bounded drain may still hold its
    // token, and a disposed source's Token getter would throw ODE at them.
    private readonly CancellationTokenSource _shutdownCts = new();

    public ManagementApi(
        PeerStore store,
        RouteTable routeTable,
        AppConfig config,
        BgpMetrics metrics,
        ILogger<ManagementApi> logger,
        IPrefixService prefixService,
        IPrefixSourceService prefixSources,
        ISessionManager sessionManager)
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
        var oldRateLimiter = Interlocked.Exchange(ref _rateLimiter, rateLimiter);
        var oldConcurrencyLimiter = Interlocked.Exchange(ref _concurrencyLimiter, concurrencyLimiter);
        Interlocked.Exchange(ref _trustedProxyNetworks, trusted);
        Interlocked.Exchange(ref _corsAllowedOrigins, newConfig.CorsAllowedOrigins);

        // Old limiters cannot be disposed here — a concurrent HandleAsync may still be mid-acquire
        // on them (#137). Park them for StopAsync/Dispose, which run after the in-flight drain.
        lock (_retiredLimiters)
        {
            if (oldRateLimiter is not null) _retiredLimiters.Add(oldRateLimiter);
            if (oldConcurrencyLimiter is not null) _retiredLimiters.Add(oldConcurrencyLimiter);
        }

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
        // HttpListener prefix normalization:
        // - "0.0.0.0" must be mapped to "+" — HttpListener on Linux does NOT accept 0.0.0.0
        //   (throws HttpListenerException "The request is not supported"), only the "+" wildcard (#195).
        // - IPv6 literals (e.g. "::1") must be bracketed: http://[::1]:5001/ (CodeRabbit #181).
        // - IPv4 ("127.0.0.1") and hostnames ("localhost") go through as-is.
        string host;
        if (_listenAddress is "0.0.0.0" or "::")
            host = "+";
        else if (_listenAddress.Contains(':'))
            host = $"[{_listenAddress}]";
        else
            host = _listenAddress;
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
        // #326: cancel in-flight handlers FIRST — they observe this token on provider calls, so a
        // cold-cache RIPEstat fetch (per-attempt timeout 180 s × retries, /api/asn-lists sequential
        // per ASN) unwinds immediately instead of pinning the drain. The host stops services in
        // reverse registration order, so BgpServer.StopAsync (the Cease teardown) waits behind this
        // method; an unbounded drain got the process SIGKILLed in Docker (default 10 s stop grace)
        // — peers saw a TCP RST instead of the promised Cease.
        _shutdownCts.Cancel();
        _cts.Cancel();
        _listener?.Stop();
        if (_listenTask is not null)
        {
            try { await _listenTask; } catch { }
        }

        // Drain in-flight handlers, but bounded: 10 s after cancellation is enough for a response
        // write or an orderly OCE unwind, and short of the host's 30 s shutdown grace. The drain
        // awaits the in-flight idle task (#258) — it completes when the last handler's finally
        // runs, so no per-request bookkeeping outlives the request.
        Task idle;
        lock (_inflightSync) idle = _inflightIdle;
        if (!idle.IsCompleted)
        {
            try { await Task.WhenAny(idle, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken)); }
            catch { /* host grace elapsed — proceed */ }
        }

        // In-flight requests have drained (boundedly) — limiters retired by ApplyConfig can go.
        DisposeRetiredLimiters();
    }

    /// <summary>
    /// #330: dispose limiters retired by <see cref="ApplyConfig"/>. Safe after the in-flight drain
    /// in StopAsync; Dispose calls it as best-effort teardown — a request still mid-acquire on a
    /// retired limiter there (direct Dispose without StopAsync, embedded use) surfaces as an
    /// ObjectDisposedException in its fault log. Idempotent.
    /// </summary>
    private void DisposeRetiredLimiters()
    {
        lock (_retiredLimiters)
        {
            foreach (var limiter in _retiredLimiters)
                try { limiter.Dispose(); } catch { /* best-effort teardown */ }
            _retiredLimiters.Clear();
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext? ctx = null;
            try
            {
                ctx = await _listener!.GetContextAsync();
                if (ct.IsCancellationRequested) break;
                // #238: acquire an in-flight slot before spawning so the default posture is
                // bounded even when the operator has not enabled the #119 concurrency limiter.
                await _inflightCap.WaitAsync(ct);
                // Register BEFORE spawning: the handler's finally decrements, and a handler that
                // completes before this thread runs would otherwise drive the count negative.
                lock (_inflightSync)
                {
                    _inflightCount++;
                    if (_inflightCount == 1)
                    {
                        _inflightActive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        _inflightIdle = _inflightActive.Task;
                    }
                }
                var accepted = ctx;
                ctx = null; // ownership transferred to the handler task
                _ = HandleWithInflightReleaseAsync(accepted);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Management API error");
            }
            finally
            {
                // An accepted context that never reached its handler task (shutdown cancelled
                // the permit acquisition, or the ct-check raced the accept) must not leak an
                // open response (#248 review).
                try { ctx?.Response.Close(); } catch { /* best-effort on shutdown */ }
            }
        }
    }

    /// <summary>
    /// Wraps <see cref="HandleAsync"/> so the <see cref="_inflightCap"/> slot acquired by the
    /// accept loop is released on every exit path (HandleAsync has early returns before its own
    /// try/finally). Faults are logged here rather than escaping into an unobserved task.
    /// </summary>
    private async Task HandleWithInflightReleaseAsync(HttpListenerContext ctx)
    {
        try
        {
            await HandleAsync(ctx);
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            // #326/#114: shutdown cancelled an in-flight provider fetch — a normal unwind, not a
            // fault. The response is closed by the listener teardown; the drain observes this task.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Management API request handling faulted");
        }
        finally
        {
            // #330: fault paths that never reach WriteResponse (429/503 early returns, a throwing
            // WriteResponse, the shutdown unwind above) must not leave the response to the
            // finalizer — Close is idempotent after a successful write.
            try { ctx.Response.Close(); } catch { /* best-effort teardown */ }
            // Tolerate teardown racing the StopAsync drain (e.g. direct Dispose without StopAsync).
            try { _inflightCap.Release(); }
            catch (ObjectDisposedException) { /* cap already disposed */ }
            // #258: a handler leaving the in-flight set completes the idle task when it was the
            // last one — that is what StopAsync's bounded drain awaits.
            lock (_inflightSync)
            {
                _inflightCount--;
                if (_inflightCount == 0)
                {
                    _inflightActive?.TrySetResult();
                    _inflightActive = null;
                    _inflightIdle = Task.CompletedTask;
                }
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
            return await HandleDeletePeer(segments[2]);

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
    /// <para>
    /// #257: HttpListener also exposes no client-disconnect token, so a slow-drip body (a byte
    /// every few seconds) otherwise parks the handler — and its in-flight slot — forever; 64 such
    /// connections starve the whole API. Each read is therefore bounded by
    /// <paramref name="readTimeout"/>; a breach surfaces as <c>408 Request Timeout</c>. The
    /// abandoned read parks on the (dead) socket holding this call's buffer — bounded by the
    /// in-flight cap (64 × 8 KB), never reused, collected with the socket.
    /// </para>
    /// </summary>
    private async Task<(string? Body, ApiResponse? Error)> ReadBodyAsync(HttpListenerContext ctx)
    {
        var maxBytes = _config.MaxRequestBodyBytes;

        // Fast path: Content-Length present and already over the cap → reject without reading.
        if (ctx.Request.ContentLength64 > maxBytes)
            return (null, ApiResponse.Error(
                $"Request body too large ({ctx.Request.ContentLength64} bytes, max {maxBytes}).", 413));

        return await ReadBoundedBodyAsync(ctx.Request.InputStream, maxBytes, BodyReadTimeout);
    }

    /// <summary>#257: per-read deadline for request bodies — the time dimension of the #156 size cap.</summary>
    private static readonly TimeSpan BodyReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Pure body reader with a hard byte cap — extracted for unit testing (#156). Returns
    /// <c>(null, 413-error)</c> when the stream yields more than <paramref name="maxBytes"/> bytes,
    /// <c>(null, 408-error)</c> when the body misses <paramref name="readTimeout"/> (#257) — an
    /// ABSOLUTE deadline for the whole body, not a per-read window: a per-read WaitAsync restarts
    /// on every byte, and a client trickling one byte per window held its slot indefinitely
    /// (#358 review). Otherwise the full body decoded as UTF-8. Covers sized and chunked bodies.
    /// </summary>
    internal static async Task<(string? Body, ApiResponse? Error)> ReadBoundedBodyAsync(
        Stream input, long maxBytes, TimeSpan? readTimeout = null)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        var deadline = readTimeout is { } timeout ? DateTime.UtcNow + timeout : (DateTime?)null;
        while (true)
        {
            int read;
            var readTask = input.ReadAsync(buffer, 0, buffer.Length);
            try
            {
                if (deadline is { } byThen)
                {
                    // #257/#358: no client-disconnect token exists on HttpListener streams — the
                    // deadline is the only bound on a slow-drip body, and it is TOTAL: each read
                    // gets only the remaining budget, so trickling cannot reset the clock. The
                    // abandoned read parks on the socket with this call's buffer (bounded by the
                    // in-flight cap, never reused); the caller unwinds, answers 408, frees its slot.
                    var remaining = byThen - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                        throw new TimeoutException();
                    read = await readTask.WaitAsync(remaining);
                }
                else
                {
                    read = await readTask;
                }
            }
            catch (TimeoutException)
            {
                return (null, ApiResponse.Error("Request body read timed out.", 408));
            }
            if (read <= 0)
                break;

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
        return ApiResponse.Ok(new
        {
            asn = bgp.Asn,
            routerId = bgp.RouterId,
            bgpPort = 179,
            apiPort = _port,
            holdTime = bgp.HoldTime,
            keepalive = bgp.KeepAlive,
            setup = BuildCiscoSetup(bgp.Asn),
            bird = BuildBirdSetup(bgp.RouterId, bgp.Asn, bgp.HoldTime),
            mikrotik = BuildMikrotikSetup(bgp.RouterId, bgp.Asn, bgp.HoldTime)
        });
    }

    /// <summary>
    /// Cisco IOS peering snippet. Pure (no instance state) so it is directly unit-testable (#218).
    /// </summary>
    internal static string[] BuildCiscoSetup(uint asn) =>
    [
        $"router bgp {asn}",
        $" neighbor <YOUR_IP> remote-as {asn}",
        $" neighbor <YOUR_IP> ebgp-multihop 2",
        $" neighbor <YOUR_IP> update-source <YOUR_INTERFACE>",
        $" neighbor <YOUR_IP> soft-reconfiguration inbound",
        $"!",
        $"address-family ipv4 unicast",
        $" neighbor <YOUR_IP> activate",
        $" neighbor <YOUR_IP> route-map BGPLite-IN in",
        $" neighbor <YOUR_IP> route-map BGPLite-OUT out",
        $"exit-address-family"
    ];

    /// <summary>
    /// BIRD peering snippet. Pure so it is directly unit-testable (#218).
    /// </summary>
    internal static string[] BuildBirdSetup(string routerId, uint asn, int holdTime) =>
    [
        $"# ---------- FILTER ----------",
        $"filter bgplite_in {{",
        $"  gw = <YOUR_GATEWAY>;",
        $"  accept;",
        $"}}",
        $"",
        $"# ---------- eBGP ----------",
        $"protocol bgp bgplite {{",
        $"  local as <YOUR_ASN>;",
        $"  neighbor {routerId} as {asn};",
        $"  source address <YOUR_IP>;",
        $"  multihop;",
        $"  hold time {holdTime};",
        $"  ipv4 {{",
        $"    import filter bgplite_in;",
        $"    export none;",
        $"    graceful restart on;",
        $"  }};",
        $"}}"
    ];

    /// <summary>
    /// MikroTik RouterOS v7 peering snippet. Pure so it is directly unit-testable (#218).
    /// <para>
    /// multihop=yes is mandatory for the common case where the client is NOT directly connected to
    /// the server (behind NAT / via upstream transit): with the default multihop=no, RouterOS v7
    /// drops the inbound TCP to port 179 before sending OPEN, so the session never establishes and
    /// no prefixes are advertised. Cisco (ebgp-multihop 2) and BIRD (multihop) already set this.
    /// </para>
    /// </summary>
    internal static string[] BuildMikrotikSetup(string routerId, uint asn, int holdTime) =>
    [
        $"# Apply all lines as-is — full paths => one paste. v7 ties a connection to a BGP instance; output.filter-chain=discard announces nothing back.",
        $"/routing/bgp/instance/add name=bgplite as=<YOUR_ASN> router-id=<YOUR_ROUTER_ID>",
        $"/routing/filter/rule/add chain=discard rule=\"reject;\"",
        $"/routing/filter/rule/add chain=bgplite-in rule=\"set gw <YOUR_GW>; accept;\"",
        $"/routing/bgp/connection/add name=bgplite instance=bgplite afi=ip remote.address={routerId}/32 remote.as={asn} local.role=ebgp multihop=yes hold-time={holdTime}s output.filter-chain=discard input.filter=bgplite-in"
    ];

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
        // #228: single DbContext roundtrip via PeerStore.GetPeerDetail (was 5 separate DbContexts:
        // GetDbPeerById + GetSubscriptions + GetCustomPrefixes + GetCustomAsns + GetCommunities).
        var peer = _store.GetPeerDetail(peerId);
        if (peer is null) return null;

        // #212: actual advertised count from the live session (post-aggregation, post-dedup).
        var advertisedCount = peer.Asn.HasValue
            ? _sessionManager.GetAdvertisedPrefixCount(peer.Ip, peer.Asn.Value)
            : 0;

        return new
        {
            id = peer.Id,
            ip = peer.Ip,
            asn = peer.Asn,
            description = peer.Description,
            status = peer.Status,
            createdAt = peer.CreatedAt,
            lastSessionAt = peer.LastSessionAt,
            lists = peer.Subscriptions,
            customPrefixes = peer.CustomPrefixes,
            customAsns = peer.CustomAsns,
            communities = peer.Communities.Select(c => CommunityCodec.Format((uint)c)),
            allRoutes = peer.Communities.Count == 0,
            // #212: the actual number of prefixes on the wire (after aggregation + duplicate NLRI
            // merge). 0 = session not established or no routes sent yet.
            advertisedPrefixCount = advertisedCount
        };
    }

    #endregion

    #region /api/peers

    /// <summary>
    /// <c>POST /api/peers</c> — creates a peer from the request body. Every field is validated and
    /// the address canonicalized (#255) BEFORE the store is touched, so a rejected request leaves no
    /// row behind and an accepted one is stored in the form <c>BgpServer</c> keys sessions by.
    /// </summary>
    private async Task<ApiResponse> HandleCreatePeer(HttpListenerContext ctx)
    {
        var (body, bodyError) = await ReadBodyAsync(ctx);
        if (bodyError is not null) return bodyError;
        var data = JsonSerializer.Deserialize<CreatePeerRequest>(body!, _jsonOpts);

        if (data is null)
            return ApiResponse.Error("Invalid request body", 400);

        // #255: validate everything BEFORE the store is touched. The peer row and its collections
        // now commit together (#259), so a rejection here leaves nothing behind at all.
        var normalizedIp = NormalizePeerIp(data.Ip);
        if (normalizedIp is null)
            return ApiResponse.Error($"Invalid peer IP: {SanitizeForLog(data.Ip ?? "(missing)")}", 400);
        if (!IsConfigurablePeerAsn(data.Asn))
            return ApiResponse.Error($"Invalid peer AS number: {data.Asn}", 400);
        if (ValidatePeerFields(data.Description, data.CustomAsns) is { } createError)
            return createError;

        var asnLists = data.AsnLists ?? [];
        var customPrefixes = new List<(string Prefix, byte Length)>();

        _logger.LogInformation("CreatePeer deserialized: AsnLists={Lists}, CustomPrefixes={Prefixes}, CustomAsns={Asns}",
            SanitizeForLog(string.Join(",", asnLists)), SanitizeForLog(string.Join(",", data.CustomPrefixes ?? [])),
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

        // #259: one transaction for the whole create. The previous CreatePeer + three Set* calls
        // each committed separately, so a duplicate CIDR — which violates the
        // (PeerId, Prefix, PrefixLength) key — returned 500 over an already-committed peer row and
        // left the user with a half-configured peer. Duplicates are now deduplicated inside the
        // store: a set of prefixes means the same thing whether a value appears once or twice.
        var id = _store.SavePeerConfiguration(
            normalizedIp, data.Asn, data.Description, asnLists, customPrefixes, data.CustomAsns ?? []);

        var peer = _store.GetDbPeerById(id);

        _logger.LogInformation("Created peer {Ip} AS{Asn} ({Id}): {Subs} lists, {Prefixes} custom prefixes, {Asns} custom AS",
            normalizedIp, data.Asn, id, asnLists.Count, customPrefixes.Count, data.CustomAsns?.Count ?? 0);

        _ = _sessionManager.RefreshPeerAsync(normalizedIp, data.Asn);

        return ApiResponse.Ok(new
        {
            id,
            ip = normalizedIp,
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
        // #228: single DbContext roundtrip via PeerStore.GetPeerDetail (was 6 separate DbContexts).
        var peer = _store.GetPeerDetail(peerId);
        if (peer is null)
            return ApiResponse.Error("Peer not found", 404);

        return ApiResponse.Ok(new
        {
            id = peer.Id,
            ip = peer.Ip,
            asn = peer.Asn,
            description = peer.Description,
            status = peer.Status,
            createdAt = peer.CreatedAt,
            lastSessionAt = peer.LastSessionAt,
            lists = peer.Subscriptions,
            customPrefixes = peer.CustomPrefixes,
            customAsns = peer.CustomAsns,
            customSources = peer.CustomSources.Select(s => new { id = s.Id, name = s.Name, url = s.Url, community = s.Community, active = s.Active }),
            communities = peer.Communities.Select(c => CommunityCodec.Format((uint)c)),
            allRoutes = peer.Communities.Count == 0
        });
    }

    /// <summary>
    /// <c>PATCH /api/peers/{id}</c> — updates the supplied fields of an existing peer; an omitted
    /// collection means "leave it alone", an empty one means "clear it". Validation runs before the
    /// store is touched, matching <see cref="HandleCreatePeer"/> (#255).
    /// </summary>
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

        // #255: the address and the peer's own ASN are not updatable through this endpoint, so only
        // the shared fields need checking — but they need it before anything is written.
        if (ValidatePeerFields(data.Description, data.CustomAsns) is { } updateError)
            return updateError;

        // #259: one transaction for the whole update, same reasoning as the create path. A null
        // argument means "leave this alone" — the PATCH semantics this endpoint already had, now
        // expressed once in the store instead of as four conditional calls that each committed.
        _store.UpdatePeerConfiguration(peerId, data.Description, data.Lists, parsedPrefixes, data.CustomAsns);

        _logger.LogInformation("Updated peer {Id}", SanitizeForLog(peerId));

        _ = _sessionManager.RefreshPeerAsync(peer.Ip, peer.Asn ?? 0);

        return HandleGetPeer(peerId);
    }

    internal async Task<ApiResponse> HandleDeletePeer(string peerId)
    {
        var peer = _store.GetDbPeerById(peerId);
        if (peer is null)
            return ApiResponse.Error("Peer not found", 404);

        // #323: terminate the peer's live session(s) BEFORE the row is deleted. Terminating first
        // stops the advertisement immediately; RouteAssembler's unknown-peer branch refuses to
        // auto-register once the session token is cancelled (Dispose runs before the row goes
        // away), so a refresh that straddles the teardown cannot resurrect the deleted peer. The
        // 10 s bound keeps a slow peer (full TCP receive window — the Cease send can block on the
        // send-timeout backstop) from pinning the DELETE request; the session is disposed even
        // when the Cease send is cancelled.
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _sessionManager.TerminatePeerAsync(peer.Ip, peer.Asn ?? 0, bound.Token);

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
        var peer = _store.GetDbPeerById(peerId);
        if (peer is null)
            return ApiResponse.Error("Peer not found", 404);

        var (body, bodyError) = await ReadBodyAsync(ctx);
        if (bodyError is not null) return bodyError;
        var data = JsonSerializer.Deserialize<AddSourceRequest>(body!, _jsonOpts);

        if (data is null || string.IsNullOrWhiteSpace(data.Name) || string.IsNullOrWhiteSpace(data.Url))
            return ApiResponse.Error("Name and Url are required", 400);

        // #232: full SSRF validation at save time (defence-in-depth on top of the fetch-time
        // ConnectCallback). Reject a URL that is malformed, uses a non-http(s) scheme, resolves to
        // a private/loopback/link-local address, or uses a non-80/443 port — before persisting it,
        // so the caller gets a clear 400 instead of a silently-saved source that fails forever at
        // fetch time. The fetch-time ConnectCallback MUST stay regardless: it is the authoritative
        // layer covering every fetch path and survives any future change to the HTTP handler.
        //
        // The validator's raw error text (blocked-IP address, DNS exception message) is logged here
        // but NOT returned to the client — that would leak internal DNS/address details and bypass
        // the non-revealing-error policy (#157). The client sees a stable generic message; the
        // operator sees the cause in the server log.
        //
        // The DNS resolution step is bounded by a 5s timeout so a hanging resolver cannot pin the
        // handler indefinitely — the fetch path is already bounded by Polly, this closes the
        // save-path gap. HttpListener does NOT expose a client-disconnect CancellationToken
        // (unlike ASP.NET Core's RequestAborted), so the timeout is purely time-bounded.
        using var validationCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool isValid;
        string? validationError;
        try
        {
            (isValid, validationError) = await PrefixSourceUrlValidator.ValidateUrlAsync(data.Url, ct: validationCts.Token);
        }
        catch (OperationCanceledException) when (validationCts.IsCancellationRequested)
        {
            // DNS-resolution timeout (5s).
            _logger.LogWarning("Save-time URL validation timed out for '{Url}'", SanitizeForLog(data.Url));
            return ApiResponse.Error("URL validation timed out (DNS resolution took too long)", 400);
        }
        if (!isValid)
        {
            _logger.LogWarning("Save-time URL validation rejected '{Url}': {Error}", SanitizeForLog(data.Url), validationError);
            return ApiResponse.Error($"Invalid URL: the host could not be reached or is not allowed", 400);
        }

        var source = _store.AddCustomSource(peerId, data.Name, data.Url, data.Community);

        // Trigger refresh so the peer receives the new source's prefixes immediately —
        // same pattern as CreatePeer/UpdatePeer. Pass ASN so shared-IP peers aren't refreshed (#200).
        _ = _sessionManager.RefreshPeerAsync(peer.Ip, peer.Asn ?? 0);

        _logger.LogInformation("Added source '{Name}' ({Url}) to peer {PeerId}",
            SanitizeForLog(data.Name), SanitizeForLog(data.Url), SanitizeForLog(peerId));
        return ApiResponse.Ok(new { id = source.Id, name = source.Name, url = source.Url, community = source.Community, active = source.Active });
    }

    private ApiResponse HandleDeleteSource(string peerId, string sourceId)
    {
        var peer = _store.GetDbPeerById(peerId);
        if (peer is null)
            return ApiResponse.Error("Peer not found", 404);

        if (!_store.DeleteCustomSource(peerId, sourceId))
            return ApiResponse.Error($"Source '{sourceId}' not found", 404);

        // Trigger refresh so the source's prefixes are withdrawn immediately (#200: ASN-scoped).
        _ = _sessionManager.RefreshPeerAsync(peer.Ip, peer.Asn ?? 0);

        _logger.LogInformation("Deleted source {SourceId} from peer {PeerId}", SanitizeForLog(sourceId), SanitizeForLog(peerId));
        return ApiResponse.Ok(new { id = sourceId, deleted = true });
    }

    private async Task<ApiResponse> HandlePatchSource(string peerId, string sourceId, HttpListenerContext ctx)
    {
        var peer = _store.GetDbPeerById(peerId);
        if (peer is null)
            return ApiResponse.Error("Peer not found", 404);

        var (body, bodyError) = await ReadBodyAsync(ctx);
        if (bodyError is not null) return bodyError;
        var data = JsonSerializer.Deserialize<PatchSourceRequest>(body!, _jsonOpts);

        if (data is null || data.Active is null)
            return ApiResponse.Error("PATCH body must contain { \"active\": true/false }", 400);

        if (!_store.SetSourceActive(peerId, sourceId, data.Active.Value))
            return ApiResponse.Error($"Source '{sourceId}' not found", 404);

        // Trigger refresh so toggling active/inactive takes effect immediately (#200: ASN-scoped).
        _ = _sessionManager.RefreshPeerAsync(peer.Ip, peer.Asn ?? 0);

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

        var prefixes = await CollectPeerPrefixes(peerId, _shutdownCts.Token);

        var format = ctx.Request.QueryString["format"] ?? "txt";
        if (format == "json")
            return ApiResponse.Ok(prefixes);

        ctx.Response.ContentType = "text/plain";
        return ApiResponse.Ok(string.Join("\n", prefixes));
    }

    private async Task<List<string>> CollectPeerPrefixes(string peerId, CancellationToken ct)
    {
        var prefixes = new List<string>();

        // Custom prefixes
        prefixes.AddRange(_store.GetCustomPrefixes(peerId));

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
                var fetched = await _prefixService.GetPrefixesForAsns(asns, ct);
                foreach (var (prefix, length, _) in fetched)
                    prefixes.Add($"{BgpConstants.UintToIPAddress(prefix)}/{length}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }  // #114/#337: only shutdown cancellation — a provider-timeout OCE (live token) is a fetch failure below
            catch (Exception ex) { _logger.LogWarning(ex, "CollectPeerPrefixes: ASN fetch failed"); }
        }

        // Country-based lists
        if (subscribedLists.Any(l => l.Asns.Count == 0 && l.Country is not null))
        {
            try
            {
                var ruPrefixes = await _prefixService.GetRuPrefixesAsync(ct);
                foreach (var (prefix, length, _) in ruPrefixes)
                    prefixes.Add($"{BgpConstants.UintToIPAddress(prefix)}/{length}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }  // #114/#337: only shutdown cancellation — a provider-timeout OCE (live token) is a fetch failure below
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
        var ct = _shutdownCts.Token;
        var lists = _config.RipeStat?.AsnLists ?? [];
        var result = new List<object>();

        foreach (var l in lists)
        {
            int prefixCount = 0;
            if (l.Asns.Count > 0)
            {
                foreach (var asn in l.Asns)
                {
                    try { prefixCount += await _prefixService.GetPrefixCountAsync(asn, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }  // #114/#337: only shutdown cancellation — a provider-timeout OCE (live token) is a fetch failure below
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to get prefix count for AS{Asn}", asn); }
                }
            }
            else if (l.Country is not null)
            {
                try { prefixCount = (await _prefixService.GetRuPrefixesAsync(ct)).Count; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }  // #114/#337: only shutdown cancellation — a provider-timeout OCE (live token) is a fetch failure below
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to get RU prefix count"); }
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
        var seen = lists.Select(l => l.Name).ToHashSet();
        foreach (var (source, prefixes) in await _prefixSources.LoadAllAsync(ct))
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

    // #330: the peer-owned rows are live, but the unowned seed rows are a STARTUP snapshot —
    // auto-refresh pushes updated source content to peers per-session; the seed rows themselves
    // are only rebuilt on restart. Operators reading this endpoint should treat seed counts
    // accordingly.
    private ApiResponse HandleGetRoutes()
    {
        var routes = _routeTable.GetAll();
        var byCommunity = routes
            .SelectMany(r => r.Communities.Count == 0
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
            try
            {
                var count = await _prefixService.GetPrefixCountAsync(asn, _shutdownCts.Token);
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
    /// (prefix, mask length) tuple, delegating to the canonical <see cref="PrefixCidr"/> parser
    /// (#236). Host bits are masked to the network address (so <c>10.0.0.5/24</c> normalizes to
    /// <c>10.0.0.0/24</c> and dedups against the same network submitted via a file source); <c>/0</c>
    /// (the default route) is rejected — a route server must not originate a default from the API.
    /// Returns null on any failure so callers can reject the whole request with a 400 before touching
    /// the store (no partial mutation). Extracted as a pure helper for unit tests (#100).
    /// </summary>
    internal static (string Prefix, byte Length)? ParseCustomPrefix(string? cidr)
    {
        if (!PrefixCidr.TryParse(cidr, out var prefix, out var length))
            return null;

        // Return the masked network address in dotted-quad form (matches how it is stored in the DB
        // and re-parsed by the BGP send path, so the round-trip is byte-identical).
        return (BgpConstants.UintToIPAddress(prefix).ToString(), length);
    }

    /// <summary>
    /// Validates a peer address and returns it in canonical dotted-quad form, or <c>null</c> if it is
    /// not a usable IPv4 address (#255).
    /// <para>
    /// Canonicalizing is the point, not a nicety. <c>BgpServer</c> keys an accepted session by
    /// <c>remoteEndpoint.Address.ToString()</c>, so a peer row storing any other spelling of the same
    /// address never binds to its session — the peer is configured, visible in the UI, and silently
    /// never comes up. <see cref="IPAddress.TryParse"/> accepts several such spellings and rewrites
    /// them: <c>01.02.03.04</c> and <c>0x1.2.3.4</c> both become <c>1.2.3.4</c>, and the three-part
    /// form <c>1.2.3</c> becomes <c>1.2.0.3</c> — a different host than the one typed. Storing
    /// <c>ToString()</c> collapses all of them onto the form the BGP path will look for.
    /// </para>
    /// <para>
    /// IPv6 is rejected rather than mapped: BGPLite is IPv4-unicast only (#14 tracks the rest), so
    /// <c>::ffff:1.2.3.4</c> would produce a peer no session can match.
    /// </para>
    /// </summary>
    internal static string? NormalizePeerIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        if (!IPAddress.TryParse(ip, out var address)) return null;
        if (address.AddressFamily != AddressFamily.InterNetwork) return null;
        return address.ToString();
    }

    /// <summary>
    /// Whether an AS number may be configured for a peer (#255). Rejects exactly four values:
    /// <list type="bullet">
    /// <item><c>0</c> — RFC 7607 §2 requires an OPEN carrying peer AS 0 to be rejected with Bad Peer
    /// AS, which BGPLite now does (#300). Accepting it here produces a peer that is stored, shown in
    /// the UI, and can never establish a session.</item>
    /// <item><c>23456</c> — AS_TRANS is the RFC 6793 placeholder a 4-octet speaker puts in My AS; its
    /// real AS arrives in the capability, so no peer ever *is* AS_TRANS.</item>
    /// <item><c>65535</c> and <c>4294967295</c> — the Last ASNs reserved by RFC 7300.</item>
    /// </list>
    /// <para>
    /// The private ranges are deliberately NOT rejected. 64512–65534 (RFC 6996) and 4200000000–
    /// 4294967294 are exactly what a user of a route server peers with; #255 suggested excluding
    /// "4200000000+", which is the private 32-bit range and would lock out real peers. RFC 7300
    /// reserves only the two endpoints above.
    /// </para>
    /// </summary>
    internal static bool IsConfigurablePeerAsn(uint asn) =>
        asn is not (0 or BgpConstants.AsPath.AsTrans or 65535 or uint.MaxValue);

    /// <summary>Maximum stored length of a peer description — bounds what a client can persist per peer.</summary>
    internal const int MaxDescriptionLength = 512;

    /// <summary>
    /// Validates the peer fields shared by create and update, returning the error response to send or
    /// <c>null</c> when everything checks out. Runs BEFORE anything is persisted, so a rejected
    /// request leaves no trace (#255, and #259 for why "before" matters).
    /// </summary>
    private static ApiResponse? ValidatePeerFields(string? description, IReadOnlyList<uint>? customAsns)
    {
        if (description is { Length: > MaxDescriptionLength })
            return ApiResponse.Error($"Description exceeds {MaxDescriptionLength} characters", 400);

        if (customAsns is not null)
        {
            foreach (var asn in customAsns)
            {
                // Custom ASNs are handed to RIPEstat to resolve the prefixes that AS originates, so
                // an unusable value becomes a lookup that can never return anything.
                if (!IsConfigurablePeerAsn(asn))
                    return ApiResponse.Error($"Invalid custom AS number: {asn}", 400);
            }
        }

        return null;
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

        // #337 review: direct Dispose (embedded use, tests) skips StopAsync — cancel the shutdown
        // token here too so in-flight provider work observes shutdown. Still never disposed:
        // abandoned handlers may hold its token (a disposed source's Token getter throws ODE).
        _shutdownCts.Cancel();
        _cts.Cancel();
        _cts.Dispose();
        DisposeRetiredLimiters();
        // At shutdown no requests are in-flight, so disposing the current limiters is safe
        // (unlike ApplyConfig mid-flight, where old ones are parked for later disposal — #137/#330).
        Volatile.Read(ref _rateLimiter)?.Dispose();
        Volatile.Read(ref _concurrencyLimiter)?.Dispose();
        _inflightCap.Dispose();
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
