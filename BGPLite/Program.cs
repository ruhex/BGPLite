using BGPLite.Api;
using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Providers;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

var baseDir = AppContext.BaseDirectory;
var dataDir = Environment.GetEnvironmentVariable("BGPLITE_DATA") ?? Path.Combine(baseDir, "data");

var configPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(baseDir, "appsettings.yml");
var config = ConfigLoader.Load(configPath);

var routeTable = new RouteTable();
var nextHop = BgpConstants.IPAddressToUint(config.Bgp.GetRouterIdAddress());

// SQLite peer store
var dbPath = Path.Combine(dataDir, "bgplite.db");
Console.WriteLine($"Peer database: {dbPath}");

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(config.Bgp);
builder.Services.AddSingleton(routeTable);

builder.Services.AddDbContextFactory<BgpDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddDbContext<BgpDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"), ServiceLifetime.Scoped);

builder.Services.AddSingleton<PeerStore>();
builder.Services.AddSingleton<IRouteFilter>(sp =>
{
    var store = sp.GetRequiredService<PeerStore>();
    return new PeerCommunityFilter(ip => store.GetCommunitiesByIp(ip));
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
builder.Services.AddHostedService<ManagementApi>();

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
    BgpDbContext.Initialize(db);
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
