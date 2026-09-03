using BGPLite;
using BGPLite.Api;
using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Providers;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly;
using BGPLite.Contracts;

var builder = Host.CreateApplicationBuilder(args);

// #263: validate the whole composition when the container is built, not when a service is first
// used. ValidateOnBuild walks every constructor-injected registration and throws — naming the
// service and the parameter — if a dependency is unregistered, so a missing registration is a
// startup failure instead of a feature that quietly does nothing.
// ValidateScopes stays off: EF Core's own AddDbContext plumbing resolves the scoped
// IDbContextOptionsConfiguration<BgpDbContext> while building the singleton DbContextOptions, which
// the root-scope check rejects. That is EF's internal wiring, not this composition — the app's own
// singletons already take IDbContextFactory rather than a scoped DbContext.
builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
{
    ValidateOnBuild = true
}));

// EF Core logs every SQL statement at Information by default; with no appsettings.json the host
// uses Information for everything → EF SQL is ~85% of log volume. Silence EF SQL (warnings/errors
// still surface) without muting startup/host logs. (appsettings.yml Logging:LogLevel is NOT read
// by the host — YAML loads only into AppConfig — so this filter is the reliable place.)
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

var baseDir = AppContext.BaseDirectory;
var dataDir = Environment.GetEnvironmentVariable("BGPLITE_DATA") ?? Path.Combine(baseDir, "data");

var configPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(baseDir, "appsettings.yml");
var config = ConfigLoader.Load(configPath);

// Fail loud on invalid YAML before the host is built / DB initialized / BGP listener started, so
// misconfiguration (bad ASN, RouterId=0.0.0.0, HoldTime=2, bad ApiPort, malformed peer address) is
// reported with a clear message at the earliest possible point instead of surfacing later at
// runtime (#89). Behavior change: invalid config that previously loaded silently now throws.
config.Validate();

var routeTable = new RouteTable();
var nextHop = BgpConstants.IPAddressToUint(config.Bgp.GetRouterIdAddress());

// SQLite peer store
var dbPath = Path.Combine(dataDir, "bgplite.db");
Console.WriteLine($"Peer database: {dbPath}");

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(config.Bgp);
builder.Services.AddSingleton(routeTable);

// SQLite resilience (#95): WAL (readers don't block writers), synchronous=NORMAL, and a 5s
// busy_timeout (engine-level lock retry) applied on every connection via a DbConnectionInterceptor,
// so both the factory-created and scoped contexts get the same settings.
var sqlitePragmas = new SqlitePragmasInterceptor();
// #260: the MultipleCollectionIncludeWarning suppression is gone. It was added by #138 on the
// premise that "for SQLite with small per-peer data (tens of rows), the Cartesian product is
// negligible" — measured false: a peer with 200 custom prefixes made LoadPeerRoutingView materialize
// 6,000 rows (31 ms per call, on the BGP send path), and one with 1,000 made it 150,000 (814 ms).
// Both peer reads now use AsSplitQuery, so the warning has nothing to fire on; leaving the
// suppression in would only hide the next reintroduction. PeerStoreSplitQueryTests asserts the
// emitted SQL directly, which also covers the projection shape the warning cannot see.
builder.Services.AddDbContextFactory<BgpDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}").AddInterceptors(sqlitePragmas));

builder.Services.AddDbContext<BgpDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}").AddInterceptors(sqlitePragmas), ServiceLifetime.Scoped);

builder.Services.AddSingleton<PeerStore>();
builder.Services.AddSingleton<IRouteFilter>(sp =>
{
    var store = sp.GetRequiredService<PeerStore>();
    // #262: the resolver is async — its DB read used to run synchronously on the session send path.
    return new PeerCommunityFilter(config.Bgp.Asn, async (ip, asn, ct) =>
        asn.HasValue ? await store.GetCommunitiesAsync(ip, asn.Value, ct) : await store.GetCommunitiesByIpAsync(ip, ct));
});
// Per-list community resolver: stamps a configured BGP community on prefixes by source
// (AsnList / Country / PrefixSource). ConfigCommunityResolver reads static config; Phase 2 will
// add a DB-backed resolver for named user lists behind the same ICommunityResolver interface.
builder.Services.AddSingleton<ICommunityResolver>(sp =>
    new ConfigCommunityResolver(
        sp.GetRequiredService<AppConfig>(),
        sp.GetRequiredService<BgpConfig>(),
        sp.GetService<ILogger<ConfigCommunityResolver>>()));
builder.Services.AddSingleton(new BgpMetrics());

// Prefix sources (file / HTTP / ...) resolved by Kind via a provider factory,
// with an in-memory TTL cache. Add a new loader by implementing IPrefixSourceProvider
// and registering it here.
builder.Services.AddHttpClient(HttpPrefixProvider.ClientName, ConfigureHttpSourceClient)
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    // SSRF defense (#144): validate DNS resolution at the socket level — no TOCTOU race (every
    // hop of any redirect re-enters ConnectCallback, so the target IP/port are re-checked).
    // Redirects are NOT followed (#321): SocketsHttpHandler's AllowAutoRedirect defaults to true
    // and re-sends all per-source headers except Authorization to the redirect target — an
    // X-API-Key on a source whose host has an open redirect would leak to the attacker's origin.
    // A 3xx is surfaced to HttpPrefixProvider and rejected there; operators list the final URL.
    ConnectCallback = PrefixSourceUrlValidator.CreateValidatedConnectionAsync,
    AllowAutoRedirect = false
})
// #107: resilience handler — retry transient HTTP failures (429/5xx/timeouts/network errors)
// with exponential backoff + jitter, plus a circuit breaker. A transient blip on a prefix-source
// fetch previously returned 0 prefixes for that source until the next refresh; now it is retried.
.AddResilienceHandler("http-prefix-source", ConfigureDefaultHttpResilience);

// #425: peer-supplied user-source URLs get their OWN named client — retry WITHOUT a circuit
// breaker. The shared breaker coupled peer-controlled failure rates to operator sources: a few
// blackholed user URLs opened the shared breaker and suppressed every operator source fetch for
// 30s windows. Handler, SSRF gate and body cap are identical to the operator client.
builder.Services.AddHttpClient(HttpPrefixProvider.UserSourceClientName, ConfigureHttpSourceClient)
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    ConnectCallback = PrefixSourceUrlValidator.CreateValidatedConnectionAsync,
    AllowAutoRedirect = false
})
.AddResilienceHandler("http-user-source", pipelineBuilder => pipelineBuilder.AddRetry(new HttpRetryStrategyOptions
{
    MaxRetryAttempts = 2,
    Delay = TimeSpan.FromMilliseconds(500),
    MaxDelay = TimeSpan.FromSeconds(2),
}));

// #425: the user-source provider instance wired with the retry-only client, keyed so the
// operator registrations above are untouched.
builder.Services.AddKeyedSingleton<IPrefixSourceProvider>(HttpPrefixProvider.UserSourceClientName,
    (sp, _) => new HttpPrefixProvider(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILogger<HttpPrefixProvider>>(),
        clientName: HttpPrefixProvider.UserSourceClientName));

static void ConfigureHttpSourceClient(HttpClient c)
{
    // HttpClient.Timeout MUST be InfiniteTimeSpan when a Polly resilience pipeline is attached —
    // otherwise the client's own timeout fires prematurely across retries and cancels the whole
    // pipeline (CodeRabbit #177). Per-attempt/per-source budgets are enforced downstream (#324).
    c.Timeout = Timeout.InfiniteTimeSpan;
    c.DefaultRequestHeaders.UserAgent.ParseAdd("BGPLite/1.0");
}

builder.Services.AddSingleton<HttpPrefixProvider>();
builder.Services.AddSingleton<FilePrefixProvider>();
builder.Services.AddSingleton<IPrefixSourceProvider>(sp => sp.GetRequiredService<HttpPrefixProvider>());
builder.Services.AddSingleton<IPrefixSourceProvider>(sp => sp.GetRequiredService<FilePrefixProvider>());
builder.Services.AddSingleton<PrefixSourceProviderFactory>();
// #214 convergence: when any load detects a content change (connect-path GetAsync OR auto-refresh
// RefreshAsync), push updated prefixes to all established peers. Resolved lazily inside the callback
// (ISessionManager is registered later, line ~164) — singletons are constructed on first use.
builder.Services.AddSingleton(sp => new PrefixSourceService(
    sp.GetRequiredService<AppConfig>(),
    sp.GetRequiredService<PrefixSourceProviderFactory>(),
    sp.GetRequiredService<ILogger<PrefixSourceService>>(),
    onSourceChanged: async name =>
    {
        // Push updated prefixes to all established peers. ISessionManager resolved lazily (registered
        // later) — singletons are constructed on first use.
        await sp.GetRequiredService<ISessionManager>().RefreshAllEstablishedAsync();
    }));
builder.Services.AddSingleton<IPrefixSourceService>(sp => sp.GetRequiredService<PrefixSourceService>());

// RIPE Stat provider — registered unconditionally so arbitrary ASNs (peer custom ASNs,
// API lookups) can be resolved on demand, regardless of preconfigured RipeStat.AsnLists.
// The ris-prefixes endpoint can take minutes to respond for large origin ASes (e.g. AS3356 /
// Lumen), so the timeout is configurable and defaults to a generous value. Fall back to the
// built-in defaults when the RipeStat section is absent.
// #104: resilience is now provided by Microsoft.Extensions.Http.Resilience (Polly v8) on the named
// client — the provider's hand-rolled retry loop + IsTransient classification were removed. The
// HttpRetryStrategyOptions.ShouldHandle defaults cover 429/5xx/timeouts/network failures.
var ripeStatConfig = config.RipeStat ?? new RipeStatConfig();
builder.Services.AddHttpClient(RipeStatProvider.ClientName, c =>
{
    // HttpClient.Timeout MUST be InfiniteTimeSpan when a Polly resilience pipeline is attached —
    // otherwise the client's own timeout (180s default) fires prematurely across retries and cancels
    // the whole pipeline (CodeRabbit #177). The per-attempt timeout is enforced by the pipeline's
    // AddTimeout (60s), and the ris-prefixes endpoint's long generation time is honored per attempt.
    c.Timeout = Timeout.InfiniteTimeSpan;
    c.DefaultRequestHeaders.UserAgent.ParseAdd("BGPLite/1.0");
})
.AddResilienceHandler("ripestat", pipelineBuilder => ConfigureRipeStatResilience(pipelineBuilder, ripeStatConfig));
builder.Services.AddSingleton(sp => new RipeStatProvider(
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<ILogger<RipeStatProvider>>(),
    ripeStatConfig));

// AS-originated prefix source (Kind: "asn") — fetches an AS's prefixes via RIPEstat through the
// provider factory, so `Kind: asn` entries under PrefixSources load like any other source.
// #267 item 5: the ONE per-ASN RIPEstat cache — shared by PrefixService (RipeStat.AsnLists,
// custom ASNs) and AsnPrefixProvider (Kind: asn sources), so an ASN configured in both
// mechanisms is fetched and cached once.
builder.Services.AddSingleton(sp => new RipeStatPrefixCache(
    sp.GetRequiredService<RipeStatProvider>(),
    sp.GetRequiredService<ILogger<RipeStatPrefixCache>>()));
builder.Services.AddSingleton<AsnPrefixProvider>(sp => new AsnPrefixProvider(
    sp.GetRequiredService<RipeStatPrefixCache>(),
    sp.GetRequiredService<ILogger<AsnPrefixProvider>>()));
builder.Services.AddSingleton<IPrefixSourceProvider>(sp => sp.GetRequiredService<AsnPrefixProvider>());

builder.Services.AddSingleton<IPrefixService>(sp =>
{
    var ripe = sp.GetRequiredService<RipeStatProvider>();
    var sources = sp.GetRequiredService<IPrefixSourceService>();
    return new PrefixService(
        config,
        sp.GetRequiredService<RipeStatPrefixCache>(),
        sources,
        sp.GetRequiredService<HttpPrefixProvider>(),
        logger: sp.GetRequiredService<ILogger<PrefixService>>(),
        // #416: the RU path now owns its convergence push — GetRuPrefixesAsync fires it AFTER
        // releasing _ruGate (the load itself is callback-free via LoadDefaultAsync), so a changed
        // default source can no longer deadlock the fleet refresh it triggers.
        onSourceChanged: async name =>
        {
            await sp.GetRequiredService<ISessionManager>().RefreshAllEstablishedAsync();
        },
        // #425: the peer-supplied user-source path uses the retry-only client so its failures
        // cannot open the circuit breaker gating operator sources.
        userSourceHttpProvider: sp.GetRequiredKeyedService<IPrefixSourceProvider>(HttpPrefixProvider.UserSourceClientName));
});

// #263: the BGP send path's dependencies are registered explicitly and resolved with
// GetRequiredService, so an incomplete composition throws at startup naming the missing service.
// They used to be optional constructor arguments threaded BgpServer -> BgpSession -> RouteAssembler,
// where dropping one produced no error at all — just peers receiving the seeded shared table
// instead of the prefixes their operator selected.
builder.Services.AddSingleton<IPeerStore>(sp => sp.GetRequiredService<PeerStore>());
builder.Services.AddSingleton(TimeProvider.System);
// Summarization policy for what goes on the wire. Was hard-coded as a `?? new ExactUnion...()`
// fallback in two constructors and never passed by this file; naming it here is what makes it
// swappable at all.
builder.Services.AddSingleton<IPrefixAggregator, ExactUnionPrefixAggregator>();
builder.Services.AddSingleton<IRouteAssembler>(sp => new RouteAssembler(
    sp.GetRequiredService<IPrefixService>(),
    sp.GetRequiredService<IPeerStore>(),
    sp.GetRequiredService<ICommunityResolver>(),
    sp.GetRequiredService<IRouteFilter>(),
    sp.GetRequiredService<AppConfig>(),
    sp.GetRequiredService<BgpConfig>(),
    sp.GetRequiredService<ILogger<RouteAssembler>>()));
builder.Services.AddSingleton<IBgpSessionFactory, BgpSessionFactory>();

// BgpServer is registered as a singleton FIRST (same pattern as ManagementApi below): the old
// wiring created it inside the AddHostedService factory while writing a captured local that the
// ISessionManager factory read back with `!` — resolution-order-dependent and NRE-prone, since
// ISessionManager consumers (PrefixAutoRefreshService, PrefixSourceService) can resolve it
// before the hosted service starts. Now the container owns exactly one instance and every
// consumer resolves it regardless of order (#231).
builder.Services.AddSingleton(sp => new BgpServer(
    sp.GetRequiredService<AppConfig>(),
    sp.GetRequiredService<RouteTable>(),
    sp.GetRequiredService<IRouteFilter>(),
    sp.GetRequiredService<BgpMetrics>(),
    sp.GetRequiredService<IBgpSessionFactory>(),
    sp.GetRequiredService<ILogger<BgpServer>>()));
// #251: route seeding (sources + RIPEstat warm-up) runs as a BACKGROUND task — the listeners
// start immediately, the local nets.txt fallback seeds in milliseconds, and established sessions
// get the full set pushed once warm-up completes. Registered before the BGP server so seeding
// begins before any session can be accepted (hosted services start in registration order).
builder.Services.AddHostedService(sp => new RouteSeedingService(
    sp.GetRequiredService<IPrefixService>(),
    sp.GetRequiredService<IPrefixSourceService>(),
    routeTable,
    config,
    sp.GetRequiredService<ISessionManager>(),
    sp.GetRequiredService<ILogger<RouteSeedingService>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<BgpServer>());
builder.Services.AddSingleton<ISessionManager>(sp => sp.GetRequiredService<BgpServer>());

// ManagementApi is registered as a singleton FIRST so the ConfigReloader below can resolve the SAME
// running instance (AddHostedService<T> creates a separate instance owned by the host, which the
// reloader could not reach). AddHostedService(sp => sp.GetRequiredService<ManagementApi>()) tells the
// host to start/stop the singleton as a hosted service without making a second copy (#136).
builder.Services.AddSingleton<ManagementApi>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ManagementApi>());

// Hot-reload (#136): watch appsettings.yml and apply the soft (non-session-disrupting) fields
// (TrustedProxies / CORS / rate & concurrency limits) without restarting the BGP service. BGP,
// peers, port, sources and community changes still require a restart.
builder.Services.AddHostedService(sp => new ConfigReloader(
    configPath,
    sp.GetRequiredService<ManagementApi>(),
    sp.GetRequiredService<ILogger<ConfigReloader>>()));

// #214: periodic auto-refresh — checks prefix sources for changes via conditional requests (304).
// Only enabled when AutoRefresh.Enabled = true in config.
builder.Services.AddHostedService(sp => new PrefixAutoRefreshService(
    sp.GetRequiredService<IPrefixSourceService>(),
    sp.GetRequiredService<ISessionManager>(),
    config,
    sp.GetRequiredService<ILogger<PrefixAutoRefreshService>>()));

if (config.RipeStat is { AsnLists.Count: > 0 })
{
    foreach (var list in config.RipeStat.AsnLists)
        Console.WriteLine($"  {list.Name}: {list.Description} ({list.Asns.Count} ASNs)");
}

var host = builder.Build();

// Initialize DB
var dir = Path.GetDirectoryName(dbPath);
if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
    Directory.CreateDirectory(dir);

using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BgpDbContext>();
    try
    {
        BgpDbContext.Initialize(db);
    }
    catch (Exception ex)
    {
        // Fail loud at startup with a human-readable cause (read-only FS, disk full, locked file)
        // rather than surfacing later as a per-request 'database is locked' 500 (#95).
        Console.Error.WriteLine($"FATAL: peer database at '{dbPath}' is not writable or could not be initialized: {ex.Message}");
        throw;
    }
    var peerCount = db.Peers.Count();
    Console.WriteLine(peerCount == 0
        ? "Created new database"
        : $"Database loaded: {peerCount} peer(s)");
}

var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("BGPLite starting — ASN={Asn}, RouterId={RouterId}", config.Bgp.Asn, config.Bgp.RouterId);


// #104 / #107: resilience pipelines for the http and ripestat named clients. Uses
// Microsoft.Extensions.Http.Resilience (Polly v8 integrated) — the .NET 8+ standard — instead of
// hand-rolled retry loops. The HttpRetryStrategyOptions.ShouldHandle defaults cover 429/5xx/timeouts
// and network failures, so no bespoke IsTransient classification is needed.
void ConfigureDefaultHttpResilience(ResiliencePipelineBuilder<HttpResponseMessage> pipelineBuilder) =>
    pipelineBuilder
        .AddRetry(new HttpRetryStrategyOptions
        {
            // Sensible defaults for a CIDR-list fetch: 3 attempts, exponential backoff with jitter
            // (the options' default BackoffType is Exponential, jitter is built-in), max 2s delay.
            MaxRetryAttempts = 2,
            Delay = TimeSpan.FromMilliseconds(500),
            MaxDelay = TimeSpan.FromSeconds(2),
        })
        .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            // Open after 10 consecutive failures, stay open 30s — stops a flapping source from
            // generating a retry storm. The next caller after the break gets a BrokenCircuitException
            // (caught upstream by PrefixSourceService's stale-on-failure / negative cache).
            SamplingDuration = TimeSpan.FromSeconds(10),
            FailureRatio = 0.5,
            MinimumThroughput = 10,
            BreakDuration = TimeSpan.FromSeconds(30),
        });
// #324/#267-item-2: no pipeline-level AddTimeout. It wrapped SendAsync only up to the
// response headers (ResponseHeadersRead) and silently CLIPPED any configured source
// Timeout above it, while the body loop ran outside the pipeline unbounded. The per-source
// budget in HttpPrefixProvider.LoadAsync is now always armed (configured Timeout or the
// 30s default) on a linked token that flows through the pipeline and covers headers AND
// body — one deadline, never clipped, still bounding every attempt and retry.

void ConfigureRipeStatResilience(ResiliencePipelineBuilder<HttpResponseMessage> pipelineBuilder, RipeStatConfig cfg) =>
    pipelineBuilder
        .AddRetry(new HttpRetryStrategyOptions
        {
            // RIPEstat's ris-prefixes endpoint is slow + rate-limited — map the operator config to
            // the Polly retry options. Backoff base matches the prior hand-rolled loop, with jitter.
            MaxRetryAttempts = Math.Max(0, cfg.RetryAttempts),
            Delay = TimeSpan.FromSeconds(Math.Max(0, cfg.RetryDelaySeconds)),
            MaxDelay = TimeSpan.FromSeconds(60),
            UseJitter = true,
        })
        .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            SamplingDuration = TimeSpan.FromSeconds(30),
            FailureRatio = 0.5,
            MinimumThroughput = 8,
            BreakDuration = TimeSpan.FromSeconds(30),
        })
        // Per-attempt timeout: the ris-prefixes endpoint can take minutes for large origin ASes
        // (e.g. AS3356). TimeoutSeconds maps here (default 180s) — HttpClient.Timeout is now
        // InfiniteTimeSpan so it does not fire across retries (CodeRabbit #177).
        .AddTimeout(TimeSpan.FromSeconds(Math.Max(10, cfg.TimeoutSeconds)));

await host.RunAsync();
return;
