using BGPLite.Configuration;
using BGPLite.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// Regression coverage for #229: GetRuPrefixesAsync used plain non-volatile fields shared across
/// concurrent sessions — torn reads (new reference with old timestamp) and thundering herd (N
/// concurrent callers all fetched the default source on TTL expiry). The fix adds Volatile/Interlocked
/// field access + a SemaphoreSlim gate + stale-on-failure, mirroring the per-ASN cache.
/// </summary>
public class PrefixCacheRaceTests
{
    /// <summary>
    /// #229: N concurrent callers on a cold cache must share a single default-source fetch
    /// (thundering-herd defense). Before the fix, all N callers missed the cache simultaneously and
    /// each invoked GetDefaultAsync — N duplicate loads of the ~11k-prefix list.
    /// </summary>
    [Fact]
    public async Task GetRuPrefixesAsync_ConcurrentColdCache_FetchesOnce()
    {
        var source = new CountingPrefixSource();
        var service = Service(source, cacheTtl: TimeSpan.FromHours(1));

        // 10 concurrent callers on a cold cache.
        var tasks = Enumerable.Range(0, 10).Select(_ => service.GetRuPrefixesAsync()).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, source.DefaultCalls); // single fetch, shared across all 10
        // All callers get the same projected list.
        Assert.All(results, r => Assert.Equal(results[0], r));
        Assert.True(results[0].Count > 0);
    }

    /// <summary>
    /// #229: after the TTL elapses, concurrent callers again share a single fetch (the gate
    /// re-check inside the lock means a caller that arrived while the first was still fetching
    /// gets the freshly-cached result, not a second fetch).
    /// </summary>
    [Fact]
    public async Task GetRuPrefixesAsync_AfterTtlExpiry_ConcurrentFetchesOnce()
    {
        var source = new CountingPrefixSource();
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var service = Service(source, cacheTtl: TimeSpan.FromMilliseconds(100), timeProvider: fakeTime);

        // First call: populates the cache.
        await service.GetRuPrefixesAsync();
        Assert.Equal(1, source.DefaultCalls);

        // Advance past the TTL.
        fakeTime.Advance(TimeSpan.FromMilliseconds(150));

        // 10 concurrent callers on the now-expired cache.
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => service.GetRuPrefixesAsync()));

        Assert.Equal(2, source.DefaultCalls); // exactly one more fetch, not ten
    }

    /// <summary>
    /// #229 stale-on-failure parity with GetPrefixesAsync (#163): when the default-source fetch
    /// throws AFTER a good copy was cached AND the TTL has elapsed, the cached (stale) copy is
    /// served instead of propagating the exception. Note: the production <c>PrefixSourceService.
    /// GetDefaultAsync</c> swallows non-OCE exceptions and returns <c>[]</c>, so this throw-path is
    /// defence-in-depth — it covers a future/mock <c>IPrefixSourceService</c> implementation that
    /// does throw, and keeps <c>GetRuPrefixesAsync</c> resilient regardless of the source service's
    /// own error handling. The test uses a fake source that throws directly to drive the branch.
    /// </summary>
    [Fact]
    public async Task GetRuPrefixesAsync_FetchFailure_AfterExpiredCache_ServesStaleCopy()
    {
        var source = new CountingPrefixSource();
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var service = Service(source, cacheTtl: TimeSpan.FromMilliseconds(100), timeProvider: fakeTime);

        // Populate a good copy.
        var first = await service.GetRuPrefixesAsync();
        Assert.Equal(1, source.DefaultCalls);
        Assert.True(first.Count > 0);

        // Advance past the TTL so the next call must re-fetch, then make the source fail.
        fakeTime.Advance(TimeSpan.FromMilliseconds(150));
        source.FailNext = true;

        // The re-fetch fails, but the stale cached copy is served (NOT thrown) — #163 parity.
        var stale = await service.GetRuPrefixesAsync();
        Assert.Equal(first, stale);                  // same projection as the original good copy
        Assert.Equal(2, source.DefaultCalls);        // the failed fetch DID happen (expired cache)
    }

    /// <summary>
    /// #229: when the fetch fails AND there is no cached copy (first-ever call), the exception
    /// propagates — there is nothing stale to serve. Matches GetPrefixesAsync's no-cache path. As
    /// above, the production source swallows failures; this drives the defence-in-depth branch via
    /// a fake source that throws.
    /// </summary>
    [Fact]
    public async Task GetRuPrefixesAsync_FetchFailure_NoCache_Throws()
    {
        var source = new CountingPrefixSource { FailNext = true };
        var service = Service(source, cacheTtl: TimeSpan.FromHours(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetRuPrefixesAsync());
    }

    // ---- helpers ----

    private static PrefixService Service(CountingPrefixSource source, TimeSpan? cacheTtl = null, TimeProvider? timeProvider = null) =>
        new(new AppConfig(),
            ripeStat: null,
            source,
            cacheTtl: cacheTtl ?? TimeSpan.FromHours(1),
            logger: NullLogger<PrefixService>.Instance,
            timeProvider: timeProvider);

    /// <summary>
    /// Fake IPrefixSourceService that counts GetDefaultAsync calls and can be made to fail. Only
    /// GetDefaultAsync is exercised by GetRuPrefixesAsync; the other members throw NotImplemented
    /// to catch accidental reliance.
    /// </summary>
    private sealed class CountingPrefixSource : IPrefixSourceService
    {
        private int _defaultCalls;
        public int DefaultCalls => Volatile.Read(ref _defaultCalls);
        public bool FailNext { get; set; }

        public Task<IReadOnlyList<(PrefixSourceConfig Source, IReadOnlyList<(uint Prefix, byte Length)> Prefixes)>> LoadAllAsync(CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetAsync(string name, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public async Task<IReadOnlyList<(uint Prefix, byte Length)>> GetDefaultAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _defaultCalls);
            // Simulate real I/O latency so concurrent callers actually overlap inside the gate.
            await Task.Delay(20, ct);
            if (FailNext) throw new InvalidOperationException("simulated default-source failure");
            return new List<(uint Prefix, byte Length)>
            {
                (0x0A000000u, 8),
                (0xC0A80000u, 16),
            };
        }

        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RefreshAsync(string sourceName, CancellationToken ct = default) => Task.FromResult(false);
        public bool SourceSupportsConditional(string sourceName) => false;
    }
}
