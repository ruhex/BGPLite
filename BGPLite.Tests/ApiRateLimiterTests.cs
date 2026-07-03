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

    private static async Task<bool> TryAcquire(PartitionedRateLimiter<string> limiter, string ip)
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
}
