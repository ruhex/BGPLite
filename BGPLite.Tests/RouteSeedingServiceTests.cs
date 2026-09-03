using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Protocol;
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

        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetPrefixesAsync(uint asn, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>>([]);
        public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default) => Task.FromResult(new List<(UInt128, byte, bool, uint)>());
        public Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default) => Task.FromResult(0);
        public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default) => Task.FromResult(new List<(UInt128, byte, bool, uint)>());
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>>([]);
        public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetUserSourcePrefixesAsync(string name, string url, string? community, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>>([]);
        public Task WarmUpAsync(CancellationToken ct = default) =>
            WarmUpCompleted = WarmUpGate.Task.WaitAsync(ct);
    }

    private sealed class FakeSourceService : IPrefixSourceService
    {
        private readonly IReadOnlyList<(PrefixSourceConfig Source, IReadOnlyList<IpPrefix> Prefixes)> _sources;

        public FakeSourceService(IReadOnlyList<(PrefixSourceConfig, IReadOnlyList<IpPrefix>)>? sources = null) =>
            _sources = sources ??
            [
                (new PrefixSourceConfig { Name = "nets", Kind = "file", Url = "nets.txt" },
                    new List<IpPrefix> { new(0xC0A80000u, 24), new(0x0A000000u, 8) })
            ];

        public Task<IReadOnlyList<(PrefixSourceConfig Source, IReadOnlyList<IpPrefix> Prefixes)>> LoadAllAsync(CancellationToken ct = default) =>
            Task.FromResult(_sources);
        public Task<IReadOnlyList<IpPrefix>> GetAsync(string name, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IpPrefix>>([]);
        public Task<IReadOnlyList<IpPrefix>> GetDefaultAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IpPrefix>>([]);
        public Task<(IReadOnlyList<IpPrefix> Prefixes, bool Changed)> LoadDefaultAsync(CancellationToken ct = default) => Task.FromResult<(IReadOnlyList<IpPrefix>, bool)>(([], false));
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
        public Task TerminatePeerAsync(string peerIp, uint asn, CancellationToken ct = default) => Task.CompletedTask;
        public void SetPeerMd5Key(string peerIp, string? password) { }
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
    public async Task BadSourceCommunity_DegradesToUntagged_DoesNotAbortSeeding()
    {
        // #328/#327: an out-of-range community VALUE used to be silently masked; now that the codec
        // rejects it, seeding must degrade that ONE source to untagged instead of aborting the loop
        // (later sources unseeded, warm-up and the final established-session push skipped).
        var prefix = new HangingPrefixService();
        var table = new RouteTable();
        var sources = new FakeSourceService(
        [
            (new PrefixSourceConfig { Name = "bad", Kind = "file", Url = "a.txt", Community = "65444:99999" },
                new List<IpPrefix> { new(0xAC100000u, 12) }),
            (new PrefixSourceConfig { Name = "good", Kind = "file", Url = "b.txt", Community = "65000:100" },
                new List<IpPrefix> { new(0xC0A80000u, 24) }),
        ]);
        using var service = NewService(prefix, sources, table, new RecordingSessionManager());

        await service.StartAsync(CancellationToken.None);

        for (var i = 0; i < 100 && table.Count < 2; i++)
            await Task.Delay(20);

        Assert.Equal(2, table.Count);
        var bad = table.Get(0xAC100000, 12);
        Assert.NotNull(bad);
        Assert.Empty(bad!.Communities);
        var good = table.Get(0xC0A80000, 24);
        Assert.NotNull(good);
        Assert.Equal([CommunityCodec.Parse("65000:100")], good!.Communities);
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
