using System.Threading.RateLimiting;
using BGPLite.Api;
using BGPLite.Configuration;

namespace BGPLite.Tests;

/// <summary>
/// Tests for <see cref="ManagementApi.CreateConcurrencyLimiter"/> (#119): a GLOBAL cap that allows up
/// to PermitLimit in-flight requests then denies the next (503 path), releasing the slot on completion.
/// </summary>
public class ApiConcurrencyLimiterTests
{
    private static ApiRateLimitConfig Cfg(int maxConcurrent) => new() { MaxConcurrentRequests = maxConcurrent };

    [Fact]
    public async Task Allows_UpToPermitLimit_Then_Denies_Next()
    {
        await using var limiter = ManagementApi.CreateConcurrencyLimiter(Cfg(2));
        var held = new List<RateLimitLease>(2);

        var first = await limiter.AcquireAsync();
        var second = await limiter.AcquireAsync();
        held.Add(first);
        held.Add(second);
        Assert.True(first.IsAcquired);
        Assert.True(second.IsAcquired);

        // Capacity full (both slots held open) → next acquire is denied (the 503 path).
        // RateLimitLease is IDisposable, not IAsyncDisposable.
        using var third = await limiter.AcquireAsync();
        Assert.False(third.IsAcquired);

        foreach (var lease in held) lease.Dispose();
    }

    [Fact]
    public async Task Releasing_Lease_Frees_Slot_For_Next_Acquire()
    {
        await using var limiter = ManagementApi.CreateConcurrencyLimiter(Cfg(1));

        var first = await limiter.AcquireAsync();
        Assert.True(first.IsAcquired);

        using var blocked = await limiter.AcquireAsync();
        Assert.False(blocked.IsAcquired); // the single slot is held

        first.Dispose(); // request completes → slot returned to the pool
        using var after = await limiter.AcquireAsync();
        Assert.True(after.IsAcquired); // slot available again
    }
}
