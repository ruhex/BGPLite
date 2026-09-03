using BGPLite.Configuration;
using BGPLite.Providers;
using BGPLite.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using BGPLite.Contracts;
using BGPLite.Protocol;

namespace BGPLite.Tests;

/// <summary>
/// Tests for <see cref="PrefixAutoRefreshService"/> (#214): per-source poll interval and jitter.
/// Uses <see cref="FakeTimeProvider"/> to advance the clock instantly (no real-second waits) and a
/// counting fake <see cref="IPrefixSourceService"/> to assert which sources were polled when.
/// </summary>
public class PrefixAutoRefreshServiceTests
{
    /// <summary>
    /// A fake IPrefixSourceService that counts RefreshAsync calls per source name and reports a
    /// configurable change outcome. Used to assert poll frequency per source. Set ReportChanged=true
    /// to make RefreshAsync return true (triggers a peer refresh).
    /// </summary>
    private class CountingPrefixSourceService : IPrefixSourceService
    {
        public Dictionary<string, int> RefreshCalls { get; } = new();
        public Dictionary<string, bool> Conditional { get; } = new();
        public bool ReportChanged { get; set; }

        public Task<bool> RefreshAsync(string sourceName, CancellationToken ct = default)
        {
            RefreshCalls[sourceName] = RefreshCalls.TryGetValue(sourceName, out var c) ? c + 1 : 1;
            return Task.FromResult(ReportChanged);
        }

        public bool SourceSupportsConditional(string sourceName)
            => Conditional.TryGetValue(sourceName, out var v) ? v : true;

        // Unused by the auto-refresh service (it reads source names from AppConfig), but required by the interface.
        public Task<IReadOnlyList<(PrefixSourceConfig Source, IReadOnlyList<IpPrefix> Prefixes)>> LoadAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(PrefixSourceConfig, IReadOnlyList<IpPrefix>)>>([]);
        public Task<IReadOnlyList<IpPrefix>> GetAsync(string name, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IpPrefix>>([]);
        public Task<(IReadOnlyList<IpPrefix> Prefixes, bool Changed)> LoadDefaultAsync(CancellationToken ct = default) => Task.FromResult<(IReadOnlyList<IpPrefix>, bool)>(([], false));
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A no-op ISessionManager that records RefreshAllEstablishedAsync invocations.</summary>
    private sealed class RecordingSessionManager : ISessionManager
    {
        public int RefreshAllCalls;
        public Task RefreshPeerAsync(string peerIp, uint asn) => Task.CompletedTask;
        public List<string> GetActivePeerIps() => [];
        public int GetAdvertisedPrefixCount(string peerIp, uint asn) => 0;
        public Task RefreshAllEstablishedAsync() { RefreshAllCalls++; return Task.CompletedTask; }
        public Task TerminatePeerAsync(string peerIp, uint asn, CancellationToken ct = default) => Task.CompletedTask;
        public Task TerminatePeerByIpAsync(string peerIp, CancellationToken ct = default) => Task.CompletedTask;
        public void SetPeerMd5Key(string peerIp, string? password) { }
    }

    private static AppConfig ConfigWith(AutoRefreshConfig autoRefresh, params (string Name, string Kind)[] sources)
    {
        var yaml = "Bgp:\n  Asn: 65444\n  RouterId: 10.0.0.1\nPrefixSources:\n";
        foreach (var (name, kind) in sources)
            yaml += $"  - Name: {name}\n    Kind: {kind}\n";
        yaml += $"AutoRefresh:\n  Enabled: {autoRefresh.Enabled.ToString().ToLower()}" +
                $"\n  IntervalSeconds: {autoRefresh.IntervalSeconds}" +
                $"\n  NoEtagIntervalSeconds: {autoRefresh.NoEtagIntervalSeconds}" +
                $"\n  MaxJitterMs: {autoRefresh.MaxJitterMs}\n";
        return ConfigLoader.LoadFromText(yaml);
    }

    /// <summary>
    /// #214: a source supporting conditional requests is polled at IntervalSeconds; a source without
    /// ETag support (asn) is polled at the longer NoEtagIntervalSeconds. On a timer tick that falls
    /// between the two intervals, only the conditional source is re-polled.
    /// </summary>
    [Fact]
    public async Task PollsConditionalSourceMoreOftenThanNoEtagSource()
    {
        var time = new FakeTimeProvider();
        var svc = new CountingPrefixSourceService
        {
            Conditional = { ["http-src"] = true, ["asn-src"] = false }
        };
        var sessions = new RecordingSessionManager();
        var config = ConfigWith(
            new AutoRefreshConfig { Enabled = true, IntervalSeconds = 60, NoEtagIntervalSeconds = 600, MaxJitterMs = 0 },
            ("http-src", "http"), ("asn-src", "asn"));
        // tick = min(60, 600) = 60s
        using var service = new PrefixAutoRefreshService(svc, sessions, config,
            NullLogger<PrefixAutoRefreshService>.Instance, time, new Random(0));

        await service.StartAsync(CancellationToken.None);

        // Tick 1 (t=60s): both sources due (first check) → 1 call each.
        time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(50); // let the loop observe the tick
        Assert.Equal(1, svc.RefreshCalls.GetValueOrDefault("http-src"));
        Assert.Equal(1, svc.RefreshCalls.GetValueOrDefault("asn-src"));

        // Tick 2 (t=120s): http-src due again (interval=60s), asn-src NOT due (next at 60+600=660s).
        time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(50);
        Assert.Equal(2, svc.RefreshCalls.GetValueOrDefault("http-src"));
        Assert.Equal(1, svc.RefreshCalls.GetValueOrDefault("asn-src"));

        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// #214: jitter applies a delay between source checks within a tick. With MaxJitterMs=100 and two
    /// sources, the second source's RefreshAsync is NOT called until the clock has advanced past the
    /// jitter window. Verified by checking the call happens only AFTER advancing FakeTimeProvider.
    /// </summary>
    [Fact]
    public async Task JitterDelaysBetweenSourceChecks()
    {
        var time = new FakeTimeProvider();
        var svc = new CountingPrefixSourceService
        {
            Conditional = { ["a"] = true, ["b"] = true }
        };
        var sessions = new RecordingSessionManager();
        var config = ConfigWith(
            new AutoRefreshConfig { Enabled = true, IntervalSeconds = 60, NoEtagIntervalSeconds = 60, MaxJitterMs = 5000 },
            ("a", "http"), ("b", "http"));
        using var service = new PrefixAutoRefreshService(svc, sessions, config,
            NullLogger<PrefixAutoRefreshService>.Instance, time, new Random(0));

        await service.StartAsync(CancellationToken.None);

        // Tick 1 (t=60s): first source checked immediately (no jitter on the very first check), then
        // the second is delayed by jitter (0..5000ms). Advance the clock to release the jittered delay.
        time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(50); // let the loop start the tick
        // Only 'a' (first source, no jitter) has been polled so far; 'b' is waiting on the jitter delay.
        Assert.Equal(1, svc.RefreshCalls.GetValueOrDefault("a"));
        Assert.Equal(0, svc.RefreshCalls.GetValueOrDefault("b"));

        // Advance past the jitter window (Random(0).Next(0, 5001) is deterministic = some value ≤5000ms).
        time.Advance(TimeSpan.FromMilliseconds(5000));
        await Task.Delay(50);
        // Now 'b' has been polled too.
        Assert.Equal(1, svc.RefreshCalls.GetValueOrDefault("b"));

        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>#214: disabled by default — StartAsync is a no-op, no timer/loop is created.</summary>
    [Fact]
    public async Task DisabledByDefault_NoTimerCreated()
    {
        var time = new FakeTimeProvider();
        var svc = new CountingPrefixSourceService();
        var sessions = new RecordingSessionManager();
        var config = ConfigWith(new AutoRefreshConfig { Enabled = false }, ("a", "http"));
        using var service = new PrefixAutoRefreshService(svc, sessions, config,
            NullLogger<PrefixAutoRefreshService>.Instance, time);

        await service.StartAsync(CancellationToken.None);

        // Advancing the clock must NOT trigger any refresh (service disabled).
        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Empty(svc.RefreshCalls);
        Assert.Equal(0, sessions.RefreshAllCalls);

        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>#214: when a source reports changed, all established peers get a refresh push.</summary>
    [Fact]
    public async Task ChangedSource_TriggersPeerRefresh()
    {
        var time = new FakeTimeProvider();
        var svc = new CountingPrefixSourceService { Conditional = { ["a"] = true }, ReportChanged = true };
        var sessions = new RecordingSessionManager();
        var config = ConfigWith(
            new AutoRefreshConfig { Enabled = true, IntervalSeconds = 60, NoEtagIntervalSeconds = 60, MaxJitterMs = 0 },
            ("a", "http"));
        using var service = new PrefixAutoRefreshService(svc, sessions, config,
            NullLogger<PrefixAutoRefreshService>.Instance, time, new Random(0));

        await service.StartAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(50);

        Assert.Equal(1, sessions.RefreshAllCalls);

        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// #214 regression: multiple changed sources in one tick trigger a SINGLE peer refresh (aggregated
    /// push), not one-per-source plus one. The convergence callback must NOT fire from the auto-refresh
    /// RefreshAsync path (that would double-push); only LoopAsync aggregates and pushes once.
    /// </summary>
    [Fact]
    public async Task MultipleChangedSources_SingleAggregatedPeerRefresh()
    {
        var time = new FakeTimeProvider();
        var svc = new CountingPrefixSourceService
        {
            Conditional = { ["a"] = true, ["b"] = true, ["c"] = true },
            ReportChanged = true
        };
        var sessions = new RecordingSessionManager();
        var config = ConfigWith(
            new AutoRefreshConfig { Enabled = true, IntervalSeconds = 60, NoEtagIntervalSeconds = 60, MaxJitterMs = 0 },
            ("a", "http"), ("b", "http"), ("c", "http"));
        using var service = new PrefixAutoRefreshService(svc, sessions, config,
            NullLogger<PrefixAutoRefreshService>.Instance, time, new Random(0));

        await service.StartAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(50);

        // All 3 sources changed, but exactly ONE aggregated peer refresh (not 3+1).
        Assert.Equal(1, sessions.RefreshAllCalls);

        await service.StopAsync(CancellationToken.None);
    }
}
