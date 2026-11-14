using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BGPLite.Api;
using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Providers;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests.Integration;

/// <summary>
/// Boots the REAL production stack on loopback — the same composition as Program.cs: PeerStore on
/// a temp SQLite file, PeerCommunityFilter, ConfigCommunityResolver, RouteAssembler,
/// ExactUnionPrefixAggregator, BgpSessionFactory, BgpServer (ephemeral listen port via the test
/// seam) and ManagementApi (ephemeral HTTP port). RIPEstat and HTTP providers are cut at the
/// IPrefixService seam (ScriptedPrefixService) so every route class resolves deterministically;
/// the file provider stays real. Mirrors Program.cs registrations — a composition drift here is
/// what this fixture exists to catch.
/// </summary>
public sealed class RouteServerFixture : IAsyncDisposable
{
    public const uint LocalAsn = 65000;

    private readonly Action<AppConfig>? _configure;
    private readonly bool _leaveFiles;

    public ScriptedPrefixService Prefixes { get; } = new();
    public PeerStore Store { get; }
    public AppConfig Config { get; private set; } = null!;
    public BgpServer Server { get; private set; } = null!;
    public ManagementApi Api { get; private set; } = null!;
    public int BgpPort { get; private set; }
    public int ApiPort { get; private set; }
    public string ApiBase => $"http://127.0.0.1:{ApiPort}";
    public HttpClient Http { get; } = new();
    public string DbPath { get; }
    public string ExtraSourcePath { get; }

    private readonly SqliteFileOptionsFactory _dbFactory;

    private RouteServerFixture(PeerStore store, string dbPath, string extraSourcePath, bool leaveFiles, Action<AppConfig>? configure)
    {
        Store = store;
        DbPath = dbPath;
        ExtraSourcePath = extraSourcePath;
        _leaveFiles = leaveFiles;
        _configure = configure;
        _dbFactory = new SqliteFileOptionsFactory(dbPath);
    }

    /// <summary>Opens a fresh DbContext against the fixture's SQLite file (for direct row seeding).</summary>
    public BgpDbContext CreateDbContext() => _dbFactory.CreateDbContext();

    /// <summary>
    /// Starts the stack. <paramref name="configure"/> mutates the template config's SETTABLE
    /// surface; init-only sections get explicit override parameters (Bgp / rate limit / CORS) that
    /// the template applies at construction. Ports are always chosen by the fixture.
    /// <paramref name="seed"/> populates the prefix catalog BEFORE the server accepts traffic.
    /// </summary>
    public static Task<RouteServerFixture> StartAsync(
        Action<AppConfig>? configure = null,
        Action<ScriptedPrefixService>? seed = null,
        string? dbPath = null,
        bool leaveFilesOnDispose = false,
        BgpConfig? bgpOverride = null,
        ApiRateLimitConfig? rateLimitOverride = null,
        List<string>? corsOriginsOverride = null)
    {
        var extraSource = Prepare(out var fixture, configure, seed, dbPath, leaveFilesOnDispose);
        return StartCoreAsync(fixture, () => AppConfigTemplate(
            extraSource, fixture.ApiPort, configure, bgpOverride, rateLimitOverride, corsOriginsOverride));
    }

    /// <summary>
    /// Boots the stack from REAL YAML through <see cref="ConfigLoader"/> — the app's actual config
    /// entry path (parse + Validate) exercised before any listener starts.
    /// <paramref name="yamlFactory"/> receives the chosen API port per bind attempt.
    /// </summary>
    public static Task<RouteServerFixture> StartFromYamlAsync(
        Func<int, string> yamlFactory,
        Action<AppConfig>? configure = null,
        Action<ScriptedPrefixService>? seed = null,
        string? dbPath = null)
    {
        var extraSource = Prepare(out var fixture, configure, seed, dbPath, leaveFilesOnDispose: false);
        var yamlPath = Path.Combine(Path.GetDirectoryName(fixture.DbPath)!, "appsettings.yml");
        return StartCoreAsync(fixture, () =>
        {
            File.WriteAllText(yamlPath, yamlFactory(fixture.ApiPort));
            return ConfigLoader.Load(yamlPath);   // strict parse + Validate — the production pipeline
        });
    }

    /// <summary>Shared preparation: temp directory, store, schema, fixture instance, seed, source file.</summary>
    private static string Prepare(
        out RouteServerFixture fixture,
        Action<AppConfig>? configure,
        Action<ScriptedPrefixService>? seed,
        string? dbPath,
        bool leaveFilesOnDispose)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bgplite-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = dbPath ?? Path.Combine(directory, "bgplite.db");
        var extraSource = Path.Combine(directory, "extra-nets.txt");

        var store = new PeerStore(new SqliteFileOptionsFactory(database));
        using (var boot = new SqliteFileOptionsFactory(database).CreateDbContext())
            BgpDbContext.Initialize(boot);

        fixture = new RouteServerFixture(store, database, extraSource, leaveFilesOnDispose || dbPath is not null, configure);
        seed?.Invoke(fixture.Prefixes);
        // The "extra" source is a REAL file provider — materialize the scripted catalog into the
        // actual file so the parse path runs end-to-end.
        File.WriteAllLines(extraSource, fixture.Prefixes.SourcePrefixes.GetValueOrDefault("extra", []));
        return extraSource;
    }

    /// <summary>The bind-retry loop: per attempt it asks <paramref name="configFactory"/> for a fresh config (ApiPort is init-only), wires the production composition, and starts both listeners.</summary>
    private static async Task<RouteServerFixture> StartCoreAsync(RouteServerFixture fixture, Func<AppConfig> configFactory)
    {
        var routeTable = new RouteTable();
        var metrics = new BgpMetrics();
        var store = fixture.Store;
        var filter = new PeerCommunityFilter(LocalAsn, (ip, asn, ct) =>
            asn.HasValue ? store.GetCommunitiesAsync(ip, asn.Value, ct) : store.GetCommunitiesByIpAsync(ip, ct));
        var aggregator = new ExactUnionPrefixAggregator();

        // Bind with retry: the free-port probe is TOCTOU-racy, same mitigation as the existing suites.
        for (var attempt = 0; ; attempt++)
        {
            fixture.BgpPort = FreeTcpPort();
            fixture.ApiPort = FreeTcpPort();
            var config = configFactory();
            fixture.Config = config;

            var resolver = new ConfigCommunityResolver(config, config.Bgp, NullLogger<ConfigCommunityResolver>.Instance);
            var assembler = new RouteAssembler(
                fixture.Prefixes, store, resolver, filter, config, config.Bgp, NullLogger<RouteAssembler>.Instance);
            var sourceService = fixture.CreateSourceService(config);
            fixture.Server = new BgpServer(
                config, routeTable, filter, metrics,
                new BgpSessionFactory(config.Bgp, routeTable, filter, metrics,
                    NullLogger<BgpSession>.Instance, store, aggregator, assembler, TimeProvider.System),
                NullLogger<BgpServer>.Instance)
            { ListenPort = fixture.BgpPort };
            fixture.Api = new ManagementApi(
                store, routeTable, config, metrics, NullLogger<ManagementApi>.Instance,
                fixture.Prefixes, sourceService, fixture.Server,
                new PrefixExportService(assembler, aggregator));
            try
            {
                await fixture.Server.StartAsync(CancellationToken.None);
                await fixture.Api.StartAsync(CancellationToken.None);
                break;
            }
            catch (Exception ex) when ((ex is SocketException or HttpListenerException) && attempt < 2)
            {
                fixture.Server.Dispose();
                fixture.Api.Dispose();
            }
        }
        return fixture;
    }

    /// <summary>Restarts the stack (new server + API instances) over the SAME SQLite file — the process-restart scenario.</summary>
    public async Task RestartAsync()
    {
        Server.Dispose();
        await Api.StopAsync(CancellationToken.None);
        Api.Dispose();

        var routeTable = new RouteTable();
        var metrics = new BgpMetrics();
        var filter = new PeerCommunityFilter(LocalAsn, (ip, asn, ct) =>
            asn.HasValue ? Store.GetCommunitiesAsync(ip, asn.Value, ct) : Store.GetCommunitiesByIpAsync(ip, ct));
        var aggregator = new ExactUnionPrefixAggregator();

        for (var attempt = 0; ; attempt++)
        {
            BgpPort = FreeTcpPort();
            ApiPort = FreeTcpPort();
            var config = AppConfigTemplate(ExtraSourcePath, ApiPort, _configure);
            Config = config;

            var resolver = new ConfigCommunityResolver(config, config.Bgp, NullLogger<ConfigCommunityResolver>.Instance);
            var assembler = new RouteAssembler(
                Prefixes, Store, resolver, filter, config, config.Bgp, NullLogger<RouteAssembler>.Instance);
            var sourceService = CreateSourceService(config);
            Server = new BgpServer(
                config, routeTable, filter, metrics,
                new BgpSessionFactory(config.Bgp, routeTable, filter, metrics,
                    NullLogger<BgpSession>.Instance, Store, aggregator, assembler, TimeProvider.System),
                NullLogger<BgpServer>.Instance)
            { ListenPort = BgpPort };
            Api = new ManagementApi(
                Store, routeTable, config, metrics, NullLogger<ManagementApi>.Instance,
                Prefixes, sourceService, Server, new PrefixExportService(assembler, aggregator));
            try
            {
                await Server.StartAsync(CancellationToken.None);
                await Api.StartAsync(CancellationToken.None);
                break;
            }
            catch (Exception ex) when ((ex is SocketException or HttpListenerException) && attempt < 2)
            {
                Server.Dispose();
                Api.Dispose();
            }
        }
    }

    private PrefixSourceService CreateSourceService(AppConfig config) => new(
        config,
        new PrefixSourceProviderFactory([new FilePrefixProvider(NullLogger<FilePrefixProvider>.Instance)]),
        NullLogger<PrefixSourceService>.Instance,
        onSourceChanged: _ => Server!.RefreshAllEstablishedAsync());

    /// <summary>The default scenario config: timers off, accept throttle off, two RIPEstat list kinds + one file source.</summary>
    private static AppConfig AppConfigTemplate(
        string extraSourcePath,
        int apiPort,
        Action<AppConfig>? configure,
        BgpConfig? bgpOverride = null,
        ApiRateLimitConfig? rateLimitOverride = null,
        List<string>? corsOriginsOverride = null)
    {
        var config = new AppConfig
        {
            Bgp = bgpOverride ?? new BgpConfig
            {
                Asn = LocalAsn,
                RouterId = "198.51.100.1",
                HoldTime = 0,                     // no timers — no keepalive traffic to race against
                MaxAcceptsPerIpPerMinute = 0,     // throttle off — tests reconnect from one IP
            },
            ApiRateLimit = rateLimitOverride,
            CorsAllowedOrigins = corsOriginsOverride,
            ApiListen = "127.0.0.1",
            ApiPort = apiPort,
            RipeStat = new RipeStatConfig
            {
                TimeoutSeconds = 10,
                RetryAttempts = 0,
                RetryDelaySeconds = 0,
                AsnLists =
                [
                    new AsnList { Name = "apple", Description = "AS714 Apple Inc.", Asns = [714], Community = "65000:20" },
                    new AsnList { Name = "ru", Description = "Russia", Country = "ru", Community = "65000:10" },
                ],
            },
            PrefixSources =
            [
                new PrefixSourceConfig { Name = "extra", Kind = "file", Path = extraSourcePath, Community = "65000:30", Description = "E2E file source" },
            ],
        };
        configure?.Invoke(config);
        return config;
    }

    public async Task<JsonElement> GetJsonAsync(string path)
    {
        using var response = await Http.GetAsync(ApiBase + path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET {path} -> {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    /// <summary>Sends a JSON request and returns the raw (status, body) pair — for scenarios asserting non-2xx too.</summary>
    public async Task<(int Status, string Body)> SendJsonAsync(string path, string method, object payload)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), ApiBase + path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        using var response = await Http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public async ValueTask DisposeAsync()
    {
        try { Server?.Dispose(); } catch { /* best-effort teardown */ }
        try { if (Api is not null) { await Api.StopAsync(CancellationToken.None); Api.Dispose(); } } catch { /* best-effort teardown */ }
        Http.Dispose();
        if (_leaveFiles) return;
        try { Directory.Delete(Path.GetDirectoryName(DbPath)!, recursive: true); } catch { /* best-effort teardown */ }
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    /// <summary>Per-call SQLite options against the fixture's file — mirrors Program.cs factory wiring.</summary>
    private sealed class SqliteFileOptionsFactory(string path) : IDbContextFactory<BgpDbContext>
    {
        public BgpDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<BgpDbContext>()
                .UseSqlite($"Data Source={path}")
                .AddInterceptors(new SqlitePragmasInterceptor())
                .Options;
            return new BgpDbContext(options);
        }
    }
}

/// <summary>Test convenience: a fully configured peer on 127.0.0.1 exercising every route class.</summary>
public static class FixtureScenario
{
    public const uint FullPeerAsn = 65010;
    public const string AppleCommunity = "65000:20";
    public const string RuCommunity = "65000:10";
    public const string ExtraCommunity = "65000:30";
    public const string CustomPrefixCommunity = "65000:100";
    public const string CustomAsnCommunity = "65000:200";

    public static readonly string[] ApplePrefixes = ["17.253.14.0/24", "17.250.16.0/20"];
    public static readonly string[] RuPrefixes = ["5.101.220.0/22", "212.192.0.0/16"];
    public static readonly string[] ExtraPrefixes = ["45.12.97.0/24"];
    public static readonly string[] CustomAsnCatalogPrefixes = ["203.0.114.0/24"];
    public const string CustomPrefix = "103.10.0.0/16";
    public static readonly string[] UserSourcePrefixes = ["91.219.236.0/24"];

    /// <summary>Seeds every scripted catalog the full-config scenario needs (non-overlapping prefixes).</summary>
    public static void SeedAll(ScriptedPrefixService prefixes)
    {
        prefixes.AsnPrefixes[714] = [.. ApplePrefixes];
        prefixes.AsnPrefixes[65020] = [.. CustomAsnCatalogPrefixes];
        prefixes.RuPrefixes.AddRange(RuPrefixes);
        prefixes.SourcePrefixes["extra"] = [.. ExtraPrefixes];
        prefixes.UserSourcePrefixes["peer-feed"] = [.. UserSourcePrefixes];
    }

    /// <summary>Expected export/dump set for the full peer: every route class, deduplicated and Ordinal-sorted.</summary>
    public static string[] ExpectedFullPeerPrefixes =>
        [.. ApplePrefixes.Concat(RuPrefixes).Concat(ExtraPrefixes).Concat(CustomAsnCatalogPrefixes)
            .Append(CustomPrefix).Concat(UserSourcePrefixes)
            .Distinct().OrderBy(p => p, StringComparer.Ordinal)];

    /// <summary>Parses "a.b.c.d/len" into the store's (bare network, length) pair — the Prefix column holds the network without "/len".</summary>
    public static (string Prefix, byte Length) SplitCidr(string cidr)
    {
        if (!BGPLite.Protocol.PrefixCidr.TryParse(cidr, out var prefix))
            throw new FormatException($"Invalid test CIDR: {cidr}");
        var rendered = prefix.ToString(); // "a.b.c.d/len" — the store wants the network part only
        return (rendered[..rendered.IndexOf('/')], prefix.Length);
    }

    /// <summary>
    /// Creates the full-config peer through the STORE (the API boundary rejects loopback peer IPs —
    /// a deliberate production policy; locally the wire client can only source from 127.0.0.1).
    /// Includes subscriptions + custom AS + custom prefix + an ACTIVE user URL source.
    /// </summary>
    public static async Task<(RouteServerFixture Server, string PeerId)> StartWithFullPeerAsync(
        Action<AppConfig>? configure = null,
        Action<ScriptedPrefixService>? extraSeed = null)
    {
        var server = await RouteServerFixture.StartAsync(
            configure,
            prefixes =>
            {
                SeedAll(prefixes);
                extraSeed?.Invoke(prefixes);
            });
        var saved = await server.Store.SavePeerConfigurationAsync(
            "127.0.0.1", FullPeerAsn, "e2e-full",
            ["apple", "ru", "extra"],
            [SplitCidr(CustomPrefix)],
            [65020u]);
        var source = await server.Store.AddCustomSourceAsync(saved.Id, "peer-feed", "https://example.com/peer-feed.txt", null);
        await server.Store.SetSourceActiveAsync(saved.Id, source.Id, true);
        return (server, saved.Id);
    }
}
