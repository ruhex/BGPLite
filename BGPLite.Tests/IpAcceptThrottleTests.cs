using BGPLite.Server;

namespace BGPLite.Tests;

/// <summary>
/// Tests for <see cref="IpAcceptThrottle"/> (#115): the per-source-IP accept throttle for the BGP
/// listener. Covers both the pure sliding-window decision (<see cref="IpAcceptThrottle.Decide"/>) and
/// the stateful <see cref="IpAcceptThrottle.TryAccept"/> wrapper (thread-safety, per-IP isolation,
/// disabled-mode, stale-entry pruning).
/// </summary>
public class IpAcceptThrottleTests
{
    private const long MinuteTicks = 600_000_000L; // 60s in ticks (TimeSpan.TicksPerSecond=10^7)

    [Fact]
    public void Decide_Allows_First_Accept_For_Empty_Window()
    {
        var allowed = IpAcceptThrottle.Decide([], nowTicks: 1_000, MinuteTicks, limit: 60, out var updated);

        Assert.True(allowed);
        var stamp = Assert.Single(updated);
        Assert.Equal(1_000, stamp);
    }

    [Fact]
    public void Decide_Allows_UpToLimit_Then_Denies_Next()
    {
        // Window [0..60s]. With limit=3, the 1st/2nd/3rd accepts in-window are allowed; the 4th is denied.
        var inWindow = new List<long> { 1_000, 2_000, 3_000 }; // already at the limit
        var allowed = IpAcceptThrottle.Decide(inWindow, nowTicks: 4_000, MinuteTicks, limit: 3, out var updated);

        Assert.False(allowed, "4th accept within the window must be rejected");
        // Denied attempt is NOT recorded (does not extend the window): only the 3 pruned in-window stamps.
        Assert.Equal(3, updated.Count);
        Assert.DoesNotContain(4_000L, updated);
    }

    [Fact]
    public void Decide_Prunes_Stale_Timestamps_And_Allows_Again()
    {
        // Timestamps from > 60s ago must be pruned, so a fresh accept is allowed even though the raw
        // count is at the limit. Use a coarse second-based scale so "older than the window" is
        // unambiguous (1s/2s/3s vs now=70s → cutoff=10s; all three are stale).
        var s = 10_000_000L; // 1 second in ticks
        var stale = new List<long> { 1 * s, 2 * s, 3 * s };
        var now = 70 * s; // 70s: 1s/2s/3s are all older than the 60s window

        var allowed = IpAcceptThrottle.Decide(stale, now, MinuteTicks, limit: 3, out var updated);

        Assert.True(allowed, "stale entries must be pruned so the accept is allowed");
        var stamp = Assert.Single(updated); // only the new timestamp remains
        Assert.Equal(now, stamp);
    }

    [Fact]
    public void Decide_LimitZero_OrNegative_Always_Allows()
    {
        // limit <= 0 = disabled (matches BgpConfig.MaxAcceptsPerIpPerMinute = 0 semantics).
        var allowed = IpAcceptThrottle.Decide(
            new List<long> { 1, 2, 3, 4, 5 }, nowTicks: 6, MinuteTicks, limit: 0, out var updated);

        Assert.True(allowed);
        Assert.Equal(6, updated.Count); // all 5 pruned + the new one
    }

    [Fact]
    public void Decide_KeepsOnly_Recent_Mixed_Timestamps()
    {
        // Mix of stale and fresh entries: only the fresh ones survive pruning.
        var mixed = new List<long>
        {
            100,                // stale
            200,                // stale
            MinuteTicks + 500,  // fresh
            MinuteTicks + 600   // fresh
        };
        var now = MinuteTicks + 1_000;

        var allowed = IpAcceptThrottle.Decide(mixed, now, MinuteTicks, limit: 10, out var updated);

        Assert.True(allowed);
        Assert.Equal(3, updated.Count); // 2 fresh + the new one
        Assert.Contains(MinuteTicks + 500, updated);
        Assert.Contains(MinuteTicks + 600, updated);
        Assert.Contains((long)now, updated);
        Assert.DoesNotContain(100L, updated);
        Assert.DoesNotContain(200L, updated);
    }

    [Fact]
    public void TryAccept_Disabled_When_Limit_NonPositive()
    {
        var throttle = new IpAcceptThrottle(maxPerMinute: 0);

        for (var i = 0; i < 100; i++)
            Assert.True(throttle.TryAccept("198.51.100.1"));
    }

    [Fact]
    public void TryAccept_Allows_UpToLimit_Then_Denies_For_SameIp()
    {
        // Deterministic clock so the test does not depend on wall-clock timing.
        var ticks = 1_000L;
        var throttle = new IpAcceptThrottle(maxPerMinute: 3, nowTicks: () => ticks);

        Assert.True(throttle.TryAccept("198.51.100.1"));
        Assert.True(throttle.TryAccept("198.51.100.1"));
        Assert.True(throttle.TryAccept("198.51.100.1"));
        Assert.False(throttle.TryAccept("198.51.100.1"), "4th accept from the same IP must be throttled");
    }

    [Fact]
    public void TryAccept_Partitions_ByIp_Independently()
    {
        var ticks = 1_000L;
        var throttle = new IpAcceptThrottle(maxPerMinute: 2, nowTicks: () => ticks);

        Assert.True(throttle.TryAccept("198.51.100.1"));
        Assert.True(throttle.TryAccept("198.51.100.1"));
        Assert.False(throttle.TryAccept("198.51.100.1"));

        // A different IP has its own independent window — still allowed.
        Assert.True(throttle.TryAccept("198.51.100.2"));
    }

    [Fact]
    public void TryAccept_Allows_Again_After_Window_Expires()
    {
        var ticks = 1_000L;
        var throttle = new IpAcceptThrottle(maxPerMinute: 2, nowTicks: () => ticks);

        Assert.True(throttle.TryAccept("198.51.100.1"));
        Assert.True(throttle.TryAccept("198.51.100.1"));
        Assert.False(throttle.TryAccept("198.51.100.1"));

        // Advance the clock past the 60s window — the old accepts are pruned, accepts allowed again.
        ticks += MinuteTicks + 1;
        Assert.True(throttle.TryAccept("198.51.100.1"));
    }

    [Fact]
    public async Task TryAccept_Is_ThreadSafe_Under_Concurrency()
    {
        // Hammer one IP from many threads: exactly `limit` accepts must be admitted, no more
        // (a non-thread-safe counter would let more than `limit` through under contention).
        const int limit = 50;
        var throttle = new IpAcceptThrottle(maxPerMinute: limit);
        var allowedCount = 0;

        await Parallel.ForEachAsync(Enumerable.Range(0, 1_000), async (_, _) =>
        {
            if (throttle.TryAccept("198.51.100.1"))
                Interlocked.Increment(ref allowedCount);
            await Task.Yield();
        });

        Assert.Equal(limit, allowedCount);
    }
}
