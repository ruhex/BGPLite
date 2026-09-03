using System.Net;
using System.Text;
using BGPLite;
using BGPLite.Api;
using BGPLite.Configuration;
using BGPLite.Providers;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using BGPLite.Contracts;
using BGPLite.Protocol;

namespace BGPLite.Tests;

/// <summary>
/// Hot-reload coverage for the SOFT (non-session-disrupting) config fields (#136):
/// <list type="bullet">
/// <item><see cref="ManagementApi.ApplyConfig"/> atomically swaps TrustedProxies / CORS / rate &amp;
/// concurrency limiters (verified directly, without a FileSystemWatcher).</item>
/// <item><see cref="ConfigReloader.Reload"/> never crashes the service on a bad edit — malformed YAML
/// or a config failing <see cref="AppConfig.Validate"/> is logged and the previous config stays.</item>
/// </list>
/// The reload/apply logic is exercised directly; the FileSystemWatcher debounce is integration-only.
/// </summary>
public class ConfigHotReloadTests
{
    // --- ManagementApi.ApplyConfig: TrustedProxies --------------------------------------------

    [Fact]
    public void ApplyConfig_Swaps_TrustedProxies()
    {
        using var harness = NewApi(Config(trustedProxies: []));

        // Initially no trusted proxies: a request from 127.0.0.1 ignores X-Forwarded-For.
        Assert.Equal("127.0.0.1",
            harness.Api.ResolveClientIpLive(IPAddress.Parse("127.0.0.1"), "198.51.100.5", null));

        harness.Api.ApplyConfig(Config(trustedProxies: ["127.0.0.0/8"]));

        // After reload, 127.0.0.1 is a trusted proxy, so X-Forwarded-For is honored.
        Assert.Equal("198.51.100.5",
            harness.Api.ResolveClientIpLive(IPAddress.Parse("127.0.0.1"), "198.51.100.5", null));
    }

    [Fact]
    public void ApplyConfig_Removes_TrustedProxies()
    {
        using var harness = NewApi(Config(trustedProxies: ["127.0.0.0/8"]));

        Assert.Equal("198.51.100.5",
            harness.Api.ResolveClientIpLive(IPAddress.Parse("127.0.0.1"), "198.51.100.5", null));

        harness.Api.ApplyConfig(Config(trustedProxies: []));

        // Trusted set cleared again → forwarding headers ignored.
        Assert.Equal("127.0.0.1",
            harness.Api.ResolveClientIpLive(IPAddress.Parse("127.0.0.1"), "198.51.100.5", null));
    }

    // --- ManagementApi.ApplyConfig: X-Real-IP opt-in (#256) --------------------------------------

    [Fact]
    public void ApplyConfig_Swaps_TrustXRealIp()
    {
        using var harness = NewApi(Config(trustedProxies: ["127.0.0.0/8"]));

        // Secure default: a spoofed X-Real-IP is ignored even behind a trusted proxy.
        Assert.Equal("127.0.0.1",
            harness.Api.ResolveClientIpLive(IPAddress.Parse("127.0.0.1"), null, "198.51.100.5"));

        harness.Api.ApplyConfig(Config(trustedProxies: ["127.0.0.0/8"], trustXRealIp: true));

        // Reload flipped the opt-in on; no restart required.
        Assert.Equal("198.51.100.5",
            harness.Api.ResolveClientIpLive(IPAddress.Parse("127.0.0.1"), null, "198.51.100.5"));
    }

    // --- ManagementApi.ApplyConfig: body-size cap (#266 item 6) -----------------------------------

    [Fact]
    public void ApplyConfig_Swaps_MaxRequestBodyBytes()
    {
        using var harness = NewApi(Config()); // default 1 MiB

        Assert.Equal(1024 * 1024, harness.Api.MaxRequestBodyBytesLive);

        harness.Api.ApplyConfig(Config(maxBodyBytes: 4096));

        // ReadBodyAsync reads the same live field — the new cap applies without a restart.
        Assert.Equal(4096, harness.Api.MaxRequestBodyBytesLive);
    }

    // --- ManagementApi.ApplyConfig: rate limiter -----------------------------------------------

    [Fact]
    public void ApplyConfig_Enables_Rate_Limiter()
    {
        using var harness = NewApi(Config()); // no ApiRateLimit → no limiter
        Assert.False(harness.Api.IsRateLimitingEnabled);

        harness.Api.ApplyConfig(Config(rateLimit: new ApiRateLimitConfig
        {
            TokenLimit = 1,
            TokensPerPeriod = 1,
            PeriodSeconds = 60
        }));

        Assert.True(harness.Api.IsRateLimitingEnabled);
    }

    [Fact]
    public void ApplyConfig_Disables_Rate_Limiter()
    {
        using var harness = NewApi(Config(rateLimit: new ApiRateLimitConfig
        {
            TokenLimit = 1,
            TokensPerPeriod = 1,
            PeriodSeconds = 60
        }));
        Assert.True(harness.Api.IsRateLimitingEnabled);

        // Enabled:false (or the section removed) → limiter rebuilt to null and the old one disposed.
        harness.Api.ApplyConfig(Config(rateLimit: new ApiRateLimitConfig { Enabled = false }));

        Assert.False(harness.Api.IsRateLimitingEnabled);
    }

    // --- ManagementApi.ApplyConfig: concurrency limiter ----------------------------------------

    [Fact]
    public void ApplyConfig_Enables_Concurrency_Limiter()
    {
        using var harness = NewApi(Config()); // no concurrency cap
        Assert.False(harness.Api.IsConcurrencyLimitEnabled);

        harness.Api.ApplyConfig(Config(rateLimit: new ApiRateLimitConfig
        {
            Enabled = true,
            MaxConcurrentRequests = 4
        }));

        Assert.True(harness.Api.IsConcurrencyLimitEnabled);
    }

    // --- ManagementApi.ApplyConfig: CORS allowlist ---------------------------------------------

    [Fact]
    public void ApplyConfig_Swaps_Cors_Allowlist()
    {
        using var harness = NewApi(Config(corsOrigins: null));
        Assert.Null(harness.Api.ResolveCorsOriginLive("https://op.example.com"));

        harness.Api.ApplyConfig(Config(corsOrigins: ["https://op.example.com"]));

        // The live config now reflects the new CORS allowlist (AddCorsHeaders reads the same field).
        Assert.Equal("https://op.example.com",
            harness.Api.ResolveCorsOriginLive("https://op.example.com"));
        // And a non-allowlisted origin is still rejected.
        Assert.Null(harness.Api.ResolveCorsOriginLive("https://evil.example.org"));
    }

    // --- ConfigReloader.Reload: valid reload applies -------------------------------------------

    [Fact]
    public void Reload_Valid_Config_Applies_TrustedProxies()
    {
        var path = TempConfigPath();
        try
        {
            File.WriteAllText(path, Yaml(trustedProxies: ["127.0.0.0/8"]));
            using var harness = NewApi(Config(trustedProxies: []));
            var reloader = new ConfigReloader(path, harness.Api, new CapturingLogger<ConfigReloader>());

            reloader.Reload();

            Assert.Equal("198.51.100.5",
                harness.Api.ResolveClientIpLive(IPAddress.Parse("127.0.0.1"), "198.51.100.5", null));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Reload_Valid_Config_Applies_TrustXRealIp()
    {
        var path = TempConfigPath();
        try
        {
            File.WriteAllText(path, Yaml(trustedProxies: ["127.0.0.0/8"], trustXRealIp: true));
            using var harness = NewApi(Config(trustedProxies: ["127.0.0.0/8"]));
            var reloader = new ConfigReloader(path, harness.Api, new CapturingLogger<ConfigReloader>());

            // Initial state: opt-in off, the spoofed X-Real-IP is ignored.
            Assert.Equal("127.0.0.1",
                harness.Api.ResolveClientIpLive(IPAddress.Parse("127.0.0.1"), null, "198.51.100.5"));

            reloader.Reload();

            Assert.Equal("198.51.100.5",
                harness.Api.ResolveClientIpLive(IPAddress.Parse("127.0.0.1"), null, "198.51.100.5"));
        }
        finally { TryDelete(path); }
    }

    // --- ConfigReloader.Reload: invalid edits never crash, keep previous -----------------------

    [Fact]
    public void Reload_Malformed_Yaml_Logs_Error_And_Does_Not_Throw()
    {
        var path = TempConfigPath();
        try
        {
            // Unterminated flow mapping → a YAML syntax error at parse time (strict loader fails).
            File.WriteAllText(path, "Bgp: { Asn: 65001, RouterId: 10.0.0.1\n");
            using var harness = NewApi(Config(trustedProxies: ["127.0.0.0/8"]));
            var logger = new CapturingLogger<ConfigReloader>();
            var reloader = new ConfigReloader(path, harness.Api, logger);

            var ex = Record.Exception(() => reloader.Reload());

            Assert.Null(ex); // never crash the service
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
            // Previous config stays: 127.0.0.1 is still trusted.
            Assert.Equal("198.51.100.5",
                harness.Api.ResolveClientIpLive(IPAddress.Parse("127.0.0.1"), "198.51.100.5", null));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Reload_Config_Failing_Validate_Logs_Error_And_Does_Not_Throw()
    {
        var path = TempConfigPath();
        try
        {
            // Valid YAML but invalid config: ApiPort 99999 is out of range → Validate() throws (#89).
            File.WriteAllText(path, Yaml(apiPort: 99999));
            using var harness = NewApi(Config(trustedProxies: ["127.0.0.0/8"]));
            var logger = new CapturingLogger<ConfigReloader>();
            var reloader = new ConfigReloader(path, harness.Api, logger);

            var ex = Record.Exception(() => reloader.Reload());

            Assert.Null(ex);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
            // Previous config stays.
            Assert.Equal("198.51.100.5",
                harness.Api.ResolveClientIpLive(IPAddress.Parse("127.0.0.1"), "198.51.100.5", null));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Reload_Missing_File_Logs_Error_And_Does_Not_Throw()
    {
        // A path that does not exist: File.ReadAllText throws → caught → logged → no crash.
        var path = Path.Combine(Path.GetTempPath(), $"bgplite-missing-{Guid.NewGuid():N}.yml");
        using var harness = NewApi(Config());
        var logger = new CapturingLogger<ConfigReloader>();
        var reloader = new ConfigReloader(path, harness.Api, logger);

        var ex = Record.Exception(() => reloader.Reload());

        Assert.Null(ex);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    // --- helpers -------------------------------------------------------------------------------

    private static AppConfig Config(
        List<string>? trustedProxies = null,
        ApiRateLimitConfig? rateLimit = null,
        List<string>? corsOrigins = null,
        bool trustXRealIp = false,
        long maxBodyBytes = 1024 * 1024) => new()
        {
            Bgp = new BgpConfig { Asn = 65001, RouterId = "10.0.0.1", HoldTime = 180, KeepAlive = 60 },
            ApiPort = 5001,
            TrustedProxies = trustedProxies ?? [],
            ApiRateLimit = rateLimit,
            CorsAllowedOrigins = corsOrigins,
            TrustXRealIp = trustXRealIp,
            MaxRequestBodyBytes = maxBodyBytes
        };

    /// <summary>Renders a VALID <c>appsettings.yml</c>-style document with the given soft fields.</summary>
    private static string Yaml(
        List<string>? trustedProxies = null,
        List<string>? corsOrigins = null,
        int apiPort = 5001,
        bool trustXRealIp = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Bgp:");
        sb.AppendLine("  Asn: 65001");
        sb.AppendLine("  RouterId: 10.0.0.1");
        sb.AppendLine("  HoldTime: 180");
        sb.AppendLine("  KeepAlive: 60");
        sb.AppendLine($"ApiPort: {apiPort}");
        if (trustedProxies is { Count: > 0 })
        {
            sb.AppendLine("TrustedProxies:");
            foreach (var p in trustedProxies) sb.AppendLine($"  - \"{p}\"");
        }
        if (trustXRealIp) sb.AppendLine("TrustXRealIp: true");
        if (corsOrigins is { Count: > 0 })
        {
            sb.AppendLine("CorsAllowedOrigins:");
            foreach (var c in corsOrigins) sb.AppendLine($"  - \"{c}\"");
        }
        return sb.ToString();
    }

    private static string TempConfigPath() =>
        Path.Combine(Path.GetTempPath(), $"bgplite-reload-{Guid.NewGuid():N}.yml");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// Builds a real <see cref="ManagementApi"/> over a private in-memory SQLite DB (so the constructor
    /// and any incidental reads work), returning it together with the underlying connection that must
    /// stay alive for the DB to remain usable. Dispose the harness when done.
    /// </summary>
    private static ApiHarness NewApi(AppConfig config)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(connection).Options;
        using (var boot = new BgpDbContext(options))
            BgpDbContext.Initialize(boot);

        var store = new PeerStore(new StaticOptionsFactory(options));
        var api = new ManagementApi(
            store,
            new RouteTable(),
            config,
            new BgpMetrics(),
            new CapturingLogger<ManagementApi>(),
            // #263: these are required now. This fixture exercises only the hot-reload path
            // (trusted proxies / CORS / rate limits), so inert stubs are supplied rather than
            // omitted — the API can no longer be built in a half-wired state by accident.
            new InertPrefixService(),
            new InertPrefixSourceService(),
            new InertSessionManager());
        return new ApiHarness(api, connection);
    }

    /// <summary>Answers every prefix query with an empty set; the hot-reload path never calls it.</summary>
    private sealed class InertPrefixService : IPrefixService
    {
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetPrefixesAsync(uint asn, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>>([]);
        public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default)
            => Task.FromResult(new List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>());
        public Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default) => Task.FromResult(0);
        public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default)
            => Task.FromResult(new List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>());
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>>([]);
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetUserSourcePrefixesAsync(string name, string url, string? community, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>>([]);
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Reports no configured prefix sources; the hot-reload path never calls it.</summary>
    private sealed class InertPrefixSourceService : IPrefixSourceService
    {
        public Task<IReadOnlyList<(PrefixSourceConfig Source, IReadOnlyList<IpPrefix> Prefixes)>> LoadAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(PrefixSourceConfig, IReadOnlyList<IpPrefix>)>>([]);
        public Task<IReadOnlyList<IpPrefix>> GetAsync(string name, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IpPrefix>>([]);
        public Task<IReadOnlyList<IpPrefix>> GetDefaultAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IpPrefix>>([]);
        public Task<(IReadOnlyList<IpPrefix> Prefixes, bool Changed)> LoadDefaultAsync(CancellationToken ct = default)
            => Task.FromResult<(IReadOnlyList<IpPrefix>, bool)>(([], false));
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RefreshAsync(string sourceName, CancellationToken ct = default) => Task.FromResult(false);
        public bool SourceSupportsConditional(string sourceName) => false;
    }

    /// <summary>Holds no sessions; the hot-reload path never calls it.</summary>
    private sealed class InertSessionManager : ISessionManager
    {
        public Task RefreshPeerAsync(string peerIp, uint asn) => Task.CompletedTask;
        public List<string> GetActivePeerIps() => [];
        public int GetAdvertisedPrefixCount(string peerIp, uint asn) => 0;
        public Task RefreshAllEstablishedAsync() => Task.CompletedTask;
        public Task TerminatePeerAsync(string peerIp, uint asn, CancellationToken ct = default) => Task.CompletedTask;
        public void SetPeerMd5Key(string peerIp, string? password) { }
    }

    private sealed class ApiHarness : IDisposable
    {
        public ManagementApi Api { get; }
        private readonly SqliteConnection _connection;
        public ApiHarness(ManagementApi api, SqliteConnection connection) { Api = api; _connection = connection; }
        public void Dispose() { Api.Dispose(); _connection.Dispose(); }
    }

    private sealed class StaticOptionsFactory : IDbContextFactory<BgpDbContext>
    {
        private readonly DbContextOptions<BgpDbContext> _options;
        public StaticOptionsFactory(DbContextOptions<BgpDbContext> options) => _options = options;
        public BgpDbContext CreateDbContext() => new(_options);
    }

    /// <summary>A minimal <see cref="ILogger{TCategoryName}"/> that records entries for assertions.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
