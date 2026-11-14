using BGPLite;
using BGPLite.Api;
using BGPLite.Configuration;
using BGPLite.Providers;
using BGPLite.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BGPLite.Tests.Integration;

/// <summary>
/// The application-hosted services (the pieces only Program.cs composes): background route
/// seeding from prefix sources, the config hot-reloader (watcher → debounce → reload → apply),
/// the periodic auto-refresh timer, and the persisted TCP-MD5 bootstrap — each driven through its
/// real StartAsync against real files/stores.
/// </summary>
public sealed class HostedServicesE2eTests
{
    [Fact]
    public async Task RouteSeeding_LoadsSourcesIntoRouteTable_AndNotifiesSessions()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "bgplite-seed-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(sourcePath, "45.12.97.0/24\n91.219.236.0/24\n");
        try
        {
            var config = new AppConfig
            {
                Bgp = new BgpConfig { Asn = 65000, RouterId = "198.51.100.1" },
                DefaultPrefixSource = "seeded",
                PrefixSources = [new PrefixSourceConfig { Name = "seeded", Kind = "file", Path = sourcePath, Community = "65000:30" }],
            };
            var routeTable = new RouteTable();
            var sessions = new FakeSessionManager();
            var scripted = new ScriptedPrefixService();
            var sources = new PrefixSourceService(
                config,
                new PrefixSourceProviderFactory([new FilePrefixProvider(NullLogger<FilePrefixProvider>.Instance)]),
                NullLogger<PrefixSourceService>.Instance,
                cacheTtl: TimeSpan.Zero);
            using var service = new RouteSeedingService(
                scripted, sources, routeTable, config, sessions, NullLogger<RouteSeedingService>.Instance);

            await service.StartAsync(CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (routeTable.Count < 2 && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            Assert.Equal(2, routeTable.Count);
            Assert.All(routeTable.GetAll(), r => Assert.Equal(["65000:30"], r.Communities.Select(BGPLite.Protocol.CommunityCodec.Format)));
            // Seeding completion pushes the full set to sessions established meanwhile.
            Assert.True(sessions.RefreshAllCount >= 1, "seeding must notify established sessions");

            await service.StopAsync(CancellationToken.None);
        }
        finally { try { File.Delete(sourcePath); } catch { /* best-effort cleanup */ } }
    }

    [Fact]
    public async Task ConfigReloader_FileChange_AppliesRateLimitHot()
    {
        var yamlPath = Path.Combine(Path.GetTempPath(), "bgplite-reload-" + Guid.NewGuid().ToString("N") + ".yml");
        try
        {
            await using var server = await RouteServerFixture.StartAsync(
                rateLimitOverride: new ApiRateLimitConfig { Enabled = true, TokenLimit = 200, TokensPerPeriod = 200, PeriodSeconds = 60 });

            File.WriteAllText(yamlPath, ReloaderYaml(server.ApiPort, tokenLimit: 200));
            using var reloader = new ConfigReloader(yamlPath, server.Api, NullLogger<ConfigReloader>.Instance);
            await reloader.StartAsync(CancellationToken.None);

            // A "bad" first state is NOT what we test; shrink the bucket and wait for the watcher.
            File.WriteAllText(yamlPath, ReloaderYaml(server.ApiPort, tokenLimit: 2));
            var limited = await WaitUntilAsync(async () =>
            {
                var third = await server.SendJsonAsync("/api/server", "GET", new { });
                var second = await server.SendJsonAsync("/api/server", "GET", new { });
                var first = await server.SendJsonAsync("/api/server", "GET", new { });
                return first.Status == 429 || second.Status == 429 || third.Status == 429;
            }, TimeSpan.FromSeconds(15));
            Assert.True(limited, "hot reload did not apply the shrunken rate limit within the budget");

            await reloader.StopAsync(CancellationToken.None);
        }
        finally { try { File.Delete(yamlPath); } catch { /* best-effort cleanup */ } }
    }

    [Fact]
    public async Task ConfigReloader_BadEdit_KeepsPreviousConfigServing()
    {
        var yamlPath = Path.Combine(Path.GetTempPath(), "bgplite-reload-bad-" + Guid.NewGuid().ToString("N") + ".yml");
        try
        {
            await using var server = await RouteServerFixture.StartAsync();
            File.WriteAllText(yamlPath, ReloaderYaml(server.ApiPort, tokenLimit: 200));
            using var reloader = new ConfigReloader(yamlPath, server.Api, NullLogger<ConfigReloader>.Instance);
            await reloader.StartAsync(CancellationToken.None);

            File.WriteAllText(yamlPath, ":::: not-yaml [[[");
            await Task.Delay(3000); // debounce (500ms) + watcher latency — a bad edit must be swallowed

            // The previous config is still fully in effect and the service is unharmed.
            var (status, _) = await server.SendJsonAsync("/api/server", "GET", new { });
            Assert.Equal(200, status);

            await reloader.StopAsync(CancellationToken.None);
        }
        finally { try { File.Delete(yamlPath); } catch { /* best-effort cleanup */ } }
    }

    [Fact]
    public async Task AutoRefresh_ChangedSource_TriggersFleetRefresh()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sources = new FakePrefixSourceService { NextRefreshResult = true }; // "content changed"
        var sessions = new FakeSessionManager();
        var config = new AppConfig
        {
            Bgp = new BgpConfig { Asn = 65000, RouterId = "198.51.100.1" },
            AutoRefresh = new AutoRefreshConfig { Enabled = true, IntervalSeconds = 60, MaxJitterMs = 0 },
            PrefixSources = [new PrefixSourceConfig { Name = "s1", Kind = "file", Path = "/tmp/unused.txt" }],
        };
        using var service = new PrefixAutoRefreshService(
            sources, sessions, config, NullLogger<PrefixAutoRefreshService>.Instance, fakeTime, jitterRandom: new Random(1));

        await service.StartAsync(CancellationToken.None);
        fakeTime.Advance(TimeSpan.FromSeconds(61));

        var woke = await WaitUntilAsync(() => Task.FromResult(sources.RefreshCalls >= 1), TimeSpan.FromSeconds(5));
        Assert.True(woke, "the periodic timer did not poll the source after one interval");
        var pushed = await WaitUntilAsync(() => Task.FromResult(sessions.RefreshAllCount >= 1), TimeSpan.FromSeconds(5));
        Assert.True(pushed, "a detected source change must refresh all established sessions");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Md5Bootstrap_GroupsPersistedKeysPerSourceIp()
    {
        await using var server = await RouteServerFixture.StartAsync();
        // Two peers share one source IP (one grouped key) + one peer on its own IP.
        await server.Store.SavePeerConfigurationAsync("198.51.100.51", 65010, null, [], [], [], md5Password: "key-one");
        await server.Store.SavePeerConfigurationAsync("198.51.100.51", 65011, null, [], [], [], md5Password: "key-one");
        await server.Store.SavePeerConfigurationAsync("198.51.100.52", 65012, null, [], [], [], md5Password: "key-two");
        var sessions = new FakeSessionManager();
        var service = new PeerMd5BootstrapService(
            server.Store, sessions, NullLogger<PeerMd5BootstrapService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(2, sessions.Md5Keys.Count); // grouped per source IP
        Assert.All(sessions.Md5Keys, k => Assert.False(string.IsNullOrEmpty(k.Password)));
    }

    private static string ReloaderYaml(int apiPort, int tokenLimit) => $"""
        Bgp:
          Asn: 65000
          RouterId: 198.51.100.1
          HoldTime: 0
        ApiPort: {apiPort}
        ApiListen: "127.0.0.1"
        ApiRateLimit:
          Enabled: true
          TokenLimit: {tokenLimit}
          TokensPerPeriod: {tokenLimit}
          PeriodSeconds: 60
        """;

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> probe, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe()) return true;
            await Task.Delay(100);
        }
        return false;
    }
}
