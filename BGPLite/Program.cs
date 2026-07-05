using BGPLite;
using BGPLite.Api;
using BGPLite.Configuration;
using Microsoft.EntityFrameworkCore.Diagnostics;
using BGPLite.Protocol;
using BGPLite.Providers;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

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
// Suppress EF Core warning 20504 (MultipleCollectionIncludeWarning): LoadPeerRoutingView includes 3
// collection navigations in a SingleQuery. For SQLite (local, embedded) with small per-peer data
// (tens of rows), the Cartesian product is negligible and SingleQuery keeps the read atomic (#138).
builder.Services.AddDbContextFactory<BgpDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}").AddInterceptors(sqlitePragmas)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.MultipleCollectionIncludeWarning)));

builder.Services.AddDbContext<BgpDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}").AddInterceptors(sqlitePragmas)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.MultipleCollectionIncludeWarning)), ServiceLifetime.Scoped);

builder.Services.AddSingleton<PeerStore>();
builder.Services.AddSingleton<IRouteFilter>(sp =>
{
    var store = sp.GetRequiredService<PeerStore>();
    return new PeerCommunityFilter(config.Bgp.Asn, (ip, asn) =>
        asn.HasValue ? store.GetCommunities(ip, asn.Value) : store.GetCommunitiesByIp(ip));
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
builder.Services.AddHttpClient(HttpPrefixProvider.ClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("BGPLite/1.0");
});
builder.Services.AddSingleton<HttpPrefixProvider>();
builder.Services.AddSingleton<FilePrefixProvider>();
builder.Services.AddSingleton<IPrefixSourceProvider>(sp => sp.GetRequiredService<HttpPrefixProvider>());
builder.Services.AddSingleton<IPrefixSourceProvider>(sp => sp.GetRequiredService<FilePrefixProvider>());
builder.Services.AddSingleton<PrefixSourceProviderFactory>();
builder.Services.AddSingleton<PrefixSourceService>();
builder.Services.AddSingleton<IPrefixSourceService>(sp => sp.GetRequiredService<PrefixSourceService>());

// RIPE Stat provider — registered unconditionally so arbitrary ASNs (peer custom ASNs,
// API lookups) can be resolved on demand, regardless of preconfigured RipeStat.AsnLists.
// The ris-prefixes endpoint can take minutes to respond for large origin ASes (e.g. AS3356 /
// Lumen), so the timeout is configurable and defaults to a generous value. Fall back to the
// built-in defaults when the RipeStat section is absent — the provider still serves ad-hoc
// lookups, and its retry handles transient failures (timeouts, 429/5xx).
var ripeStatConfig = config.RipeStat ?? new RipeStatConfig();
builder.Services.AddHttpClient(RipeStatProvider.ClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(ripeStatConfig.TimeoutSeconds);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("BGPLite/1.0");
});
builder.Services.AddSingleton(sp => new RipeStatProvider(
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<ILogger<RipeStatProvider>>(),
    ripeStatConfig));

// AS-originated prefix source (Kind: "asn") — fetches an AS's prefixes via RIPEstat through the
// provider factory, so `Kind: asn` entries under PrefixSources load like any other source.
builder.Services.AddSingleton<AsnPrefixProvider>();
builder.Services.AddSingleton<IPrefixSourceProvider>(sp => sp.GetRequiredService<AsnPrefixProvider>());

builder.Services.AddSingleton<IPrefixService>(sp =>
{
    var ripe = sp.GetRequiredService<RipeStatProvider>();
    var sources = sp.GetRequiredService<IPrefixSourceService>();
    return new PrefixService(config, ripe, sources);
});

BgpServer? bgpServer = null;

builder.Services.AddHostedService(sp =>
{
    var store = sp.GetRequiredService<PeerStore>();
    var prefixService = sp.GetRequiredService<IPrefixService>();

    bgpServer = new BgpServer(
        sp.GetRequiredService<AppConfig>(),
        sp.GetRequiredService<RouteTable>(),
        sp.GetRequiredService<IRouteFilter>(),
        sp.GetRequiredService<BgpMetrics>(),
        sp.GetRequiredService<ILogger<BgpSession>>(),
        sp.GetRequiredService<ILogger<BgpServer>>(),
        (ip, asn) => store.UpsertPeer(ip, asn),
        store,
        prefixService,
        communityResolver: sp.GetRequiredService<ICommunityResolver>());
    return bgpServer;
});
builder.Services.AddSingleton<ISessionManager>(sp => bgpServer!);

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

// Pre-warm prefix cache before accepting BGP connections (RIPE + all configured sources)
Console.WriteLine("Warming up prefix cache...");
var prefixService = host.Services.GetRequiredService<IPrefixService>();
await prefixService.WarmUpAsync();
Console.WriteLine("Prefix cache ready");

// Seed the route table from configured prefix sources, attaching each source's community.
var sourceSvc = host.Services.GetRequiredService<IPrefixSourceService>();
foreach (var (source, prefixes) in await sourceSvc.LoadAllAsync())
{
    var communities = string.IsNullOrEmpty(source.Community)
        ? Array.Empty<uint>()
        : new[] { CommunityCodec.Parse(source.Community!) };

    foreach (var (prefix, length) in prefixes)
    {
        routeTable.AddOrUpdate(new Route
        {
            Prefix = prefix,
            PrefixLength = length,
            NextHop = nextHop,
            Communities = communities
        });
    }

    Console.WriteLine($"  Source '{source.Name}': {prefixes.Count} prefixes" +
                      (source.Community is null ? "" : $" community={source.Community}"));
}

logger.LogInformation("Loaded routes: {RouteCount}", routeTable.Count);

await host.RunAsync();
return;
