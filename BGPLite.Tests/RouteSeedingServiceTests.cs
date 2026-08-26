using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Providers;
using BGPLite.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BGPLite.Tests;

/// <summary>
/// #251: route seeding must not block listener startup. StartAsync returns immediately even while
/// the RIPEstat warm-up hangs; configured sources (the local nets.txt fallback) seed the table in
/// the background; and sessions established meanwhile receive the full set via
/// ISessionManager.RefreshAllEstablishedAsync once warm-up completes.
/// </summary>
public class RouteSeedingServiceTests
{
    private sealed class HangingPrefixService : IPrefixService
    {
        public TaskCompletionSource WarmUpGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Starts as a never-completing task so assertions cannot race the seeding task's entry
        // into WarmUpAsync (where it becomes WarmUpGate.Task.WaitAsync(ct)).
        public Task WarmUpCompleted { get; private set; } = new TaskCompletionSource().Task;

        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetPrefixesAsync(uint asn, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task<List<(uint Prefix, byte Length, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default) => Task.FromResult(new List<(uint, byte, uint)>());
        public Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default) => Task.FromResult(0);
        public Task<List<(uint Prefix, byte Length, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default) => Task.FromResult(new List<(uint, byte, uint)>());
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetUserSourcePrefixesAsync(string name, string url, string? community, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task WarmUpAsync(CancellationToken ct = default) =>
            WarmUpCompleted = WarmUpGate.Task.WaitAsync(ct);
    }

    private sealed class FakeSourceService : IPrefixSourceService
    {
        public Task<IReadOnlyList<(PrefixSourceConfig Source, IReadOnlyList<(uint Prefix, byte Length)> Prefixes)>> LoadAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(PrefixSourceConfig Source, IReadOnlyList<(uint Prefix, byte Length)> Prefixes)>>(new List<(PrefixSourceConfig, IReadOnlyList<(uint, byte)>)>
            {
                (new PrefixSourceConfig { Name = "nets", Kind = "file", Url = "nets.txt" },
                    new List<(uint, byte)> { (0xC0A80000u, 24), (0x0A000000u, 8) })
            });
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetAsync(string name, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetDefaultAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RefreshAsync(string sourceName, CancellationToken ct = default) => Task.FromResult(false);
        public bool SourceSupportsConditional(string sourceName) => false;
    }

    private sealed class RecordingSessionManager : ISessionManager
    {
        public int RefreshAllCalls;
        public Task RefreshPeerAsync(string peerIp, uint asn) => Task.CompletedTask;
        public List<string> GetActivePeerIps() => [];
        public int GetAdvertisedPrefixCount(string peerIp, uint asn) => 0;
        public Task RefreshAllEstablishedAsync() { RefreshAllCalls++; return Task.CompletedTask; }
    }

    private static RouteSeedingService NewService(
        HangingPrefixService prefix, FakeSourceService sources, RouteTable table, RecordingSessionManager sessions) =>
        new(prefix, sources, table,
            new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "10.0.0.1" } },
            sessions, NullLogger<RouteSeedingService>.Instance);

    [Fact]
    public async Task StartAsync_ReturnsImmediately_WhileWarmUpHangs()
    {
        var prefix = new HangingPrefixService();
        var table = new RouteTable();
        using var service = NewService(prefix, new FakeSourceService(), table, new RecordingSessionManager());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await service.StartAsync(CancellationToken.None);
        sw.Stop();

        // The listener-start path must not wait on network I/O — 5s is a very generous bound.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"StartAsync blocked for {sw.Elapsed}");
        Assert.False(prefix.WarmUpCompleted.IsCompleted); // warm-up is still hanging in the background
    }

    [Fact]
    public async Task Sources_SeedRouteTable_InBackground()
    {
        var prefix = new HangingPrefixService();
        var table = new RouteTable();
        using var service = NewService(prefix, new FakeSourceService(), table, new RecordingSessionManager());

        await service.StartAsync(CancellationToken.None);

        // Sources seed before the warm-up await — wait for the 2 prefixes without depending on warm-up.
        for (var i = 0; i < 100 && table.Count < 2; i++)
            await Task.Delay(20);
        Assert.Equal(2, table.Count);
    }

    [Fact]
    public async Task WarmUpCompletion_PushesRefreshToEstablishedSessions()
    {
        var prefix = new HangingPrefixService();
        var table = new RouteTable();
        var sessions = new RecordingSessionManager();
        using var service = NewService(prefix, new FakeSourceService(), table, sessions);

        await service.StartAsync(CancellationToken.None);
        for (var i = 0; i < 100 && table.Count < 2; i++)
            await Task.Delay(20);
        Assert.Equal(0, sessions.RefreshAllCalls); // not yet — warm-up still hanging

        prefix.WarmUpGate.SetResult();
        for (var i = 0; i < 100 && sessions.RefreshAllCalls == 0; i++)
            await Task.Delay(20);

        Assert.Equal(1, sessions.RefreshAllCalls);
    }

    [Fact]
    public async Task StopAsync_CancelsHangingWarmUp_AndCompletes()
    {
        var prefix = new HangingPrefixService();
        var table = new RouteTable();
        using var service = NewService(prefix, new FakeSourceService(), table, new RecordingSessionManager());

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(TimeSpan.FromSeconds(5).Equals(TimeSpan.Zero)
            ? CancellationToken.None
            : new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        // Shutdown does not throw and does not hang; seeding ended in the cancelled state.
        Assert.True(prefix.WarmUpCompleted.IsCompleted);
    }
}
