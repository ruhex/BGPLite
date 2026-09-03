using System.Threading.RateLimiting;
using BGPLite.Api;
using BGPLite.Configuration;

namespace BGPLite.Tests;

/// <summary>
/// Tests for <see cref="ManagementApi.CreateRateLimiter"/> (#116): a per-IP token bucket that allows
/// up to the burst then denies (429), partitioned independently per client IP.
/// </summary>
public class ApiRateLimiterTests
{
    private static ApiRateLimitConfig Cfg(int tokenLimit, int tokensPerPeriod, int periodSeconds = 60) => new()
    {
        TokenLimit = tokenLimit,
        TokensPerPeriod = tokensPerPeriod,
        PeriodSeconds = periodSeconds
    };

    private static async Task<bool> TryAcquire(ClientIpRateLimiter limiter, string ip)
    {
        using var lease = await limiter.AcquireAsync(ip); // RateLimitLease is IDisposable, not IAsyncDisposable
        return lease.IsAcquired;
    }

    [Fact]
    public async Task Allows_UpToTokenLimit_Then_Denies()
    {
        await using var limiter = ManagementApi.CreateRateLimiter(Cfg(2, 2, 60));
        Assert.True(await TryAcquire(limiter, "198.51.100.1"));
        Assert.True(await TryAcquire(limiter, "198.51.100.1"));
        Assert.False(await TryAcquire(limiter, "198.51.100.1")); // bucket exhausted → would 429
    }

    [Fact]
    public async Task Partitions_ByIp_Independently()
    {
        await using var limiter = ManagementApi.CreateRateLimiter(Cfg(1, 1, 60));
        Assert.True(await TryAcquire(limiter, "198.51.100.1"));
        Assert.True(await TryAcquire(limiter, "198.51.100.2")); // separate bucket — still allowed
        Assert.False(await TryAcquire(limiter, "198.51.100.1")); // first IP's bucket exhausted
    }

    [Fact]
    public async Task IdlePartitions_AreEvicted_ByTheAmortizedSweep()
    {
        // #423: PartitionedRateLimiter kept every IP ever seen (and its replenishment timer) for
        // the process lifetime — with TrustXRealIp the key is client-controlled, so rotating the
        // header minted fresh buckets without bound. Idle partitions must be evicted.
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        await using var limiter = new ClientIpRateLimiter(
            Cfg(10, 10, 60), timeProvider: time, idleThreshold: TimeSpan.FromSeconds(30), sweepEvery: 1);

        await TryAcquire(limiter, "198.51.100.1");
        await TryAcquire(limiter, "198.51.100.2");
        Assert.Equal(2, limiter.TrackedCount);

        time.Advance(TimeSpan.FromSeconds(31));    // both idle past the threshold
        await TryAcquire(limiter, "198.51.100.3"); // the sweep evicts .1/.2, then mints .3

        Assert.Equal(1, limiter.TrackedCount);
    }

    [Fact]
    public async Task ActivePartition_SurvivesTheSweep()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        await using var limiter = new ClientIpRateLimiter(
            Cfg(10, 10, 60), timeProvider: time, idleThreshold: TimeSpan.FromSeconds(30), sweepEvery: 1);

        await TryAcquire(limiter, "198.51.100.1");
        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10)); // under the idle threshold at every touch
            await TryAcquire(limiter, "198.51.100.1");
        }

        Assert.Equal(1, limiter.TrackedCount);
    }
}
