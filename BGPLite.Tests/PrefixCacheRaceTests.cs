using BGPLite.Configuration;
using BGPLite.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using BGPLite.Protocol;

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

        Assert.Equal(1, source.LoadCalls); // single fetch, shared across all 10
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
        Assert.Equal(1, source.LoadCalls);

        // Advance past the TTL.
        fakeTime.Advance(TimeSpan.FromMilliseconds(150));

        // 10 concurrent callers on the now-expired cache.
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => service.GetRuPrefixesAsync()));

        Assert.Equal(2, source.LoadCalls); // exactly one more fetch, not ten
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
        Assert.Equal(1, source.LoadCalls);
        Assert.True(first.Count > 0);

        // Advance past the TTL so the next call must re-fetch, then make the source fail.
        fakeTime.Advance(TimeSpan.FromMilliseconds(150));
        source.FailNext = true;

        // The re-fetch fails, but the stale cached copy is served (NOT thrown) — #163 parity.
        var stale = await service.GetRuPrefixesAsync();
        Assert.Equal(first, stale);                  // same projection as the original good copy
        Assert.Equal(2, source.LoadCalls);        // the failed fetch DID happen (expired cache)
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

    /// <summary>
    /// #416: a changed default-source load must not deadlock when the convergence push re-enters
    /// the RU path. Pre-fix, <see cref="PrefixService.GetRuPrefixesAsync"/> held <c>_ruGate</c>
    /// across the default-source load and a changed load fired <c>onSourceChanged</c> INLINE on
    /// that stack; the push (<c>RefreshAllEstablishedAsync</c> in production — a second RU consumer
    /// here) blocked on the gate the outer frame still held, while that frame awaited the push —
    /// a permanent circular wait with sessions staying Established and nothing in the logs.
    /// Post-fix the load runs callback-free (<c>LoadDefaultAsync</c>) and the push fires AFTER
    /// <c>_ruGate.Release()</c>, so the re-entrant caller takes the fast path against the
    /// just-written cache. The pre-fix deadlock is structural (both waits unconditional), so the
    /// bounded <see cref="Task.WaitAsync(TimeSpan)"/> makes this test FAIL against the pre-fix
    /// implementation (TimeoutException) and pass after it — no timing dependence in the green
    /// direction.
    /// </summary>
    [Fact]
    public async Task GetRuPrefixesAsync_ChangedLoad_PushReenteringTheRuPath_DoesNotDeadlock()
    {
        var source = new CountingPrefixSource { ChangedOnNextLoad = true };
        PrefixService service = null!;
        Task reentrant = Task.CompletedTask;
        var pushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        service = Service(source, onSourceChanged: async _ =>
        {
            pushStarted.TrySetResult();
            // The production push (RefreshAllEstablishedAsync) rebuilds every established session's
            // route set; a RU-consuming session's build re-enters GetRuPrefixesAsync right here.
            reentrant = service.GetRuPrefixesAsync();
            await reentrant;
        });

        var main = service.GetRuPrefixesAsync();

        // The changed load must trigger the push…
        await pushStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // …and both the triggering call and the re-entrant consumer must complete.
        await main.WaitAsync(TimeSpan.FromSeconds(10));       // pre-fix: TimeoutException (deadlock)
        await reentrant.WaitAsync(TimeSpan.FromSeconds(10));  // pre-fix: never completes (gate held)

        Assert.Equal(1, source.LoadCalls); // the re-entry hit the fresh cache — no second fetch
    }

    /// <summary>
    /// #416: the RU push fires exactly when the load reports a content change — not on the fresh
    /// fast path, not on an unchanged reload. The name passed through is the configured default
    /// source name (empty string when none is configured).
    /// </summary>
    [Fact]
    public async Task GetRuPrefixesAsync_PushFiresOnlyOnChange()
    {
        var source = new CountingPrefixSource();
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var pushes = new List<string>();
        var service = Service(
            source, cacheTtl: TimeSpan.FromMilliseconds(100), timeProvider: fakeTime,
            onSourceChanged: name => { lock (pushes) pushes.Add(name); return Task.CompletedTask; });

        // Cold load reporting changed → push #1.
        source.ChangedOnNextLoad = true;
        await service.GetRuPrefixesAsync();
        Assert.Single(pushes);
        Assert.Equal(string.Empty, pushes[0]); // no DefaultPrefixSource configured in this fixture

        // Fresh fast path → no push.
        await service.GetRuPrefixesAsync();
        Assert.Single(pushes);

        // Expired reload, unchanged content → no push.
        fakeTime.Advance(TimeSpan.FromMilliseconds(150));
        await service.GetRuPrefixesAsync();
        Assert.Single(pushes);

        // Expired reload, changed content → push #2.
        fakeTime.Advance(TimeSpan.FromMilliseconds(150));
        source.ChangedOnNextLoad = true;
        await service.GetRuPrefixesAsync();
        Assert.Equal(2, pushes.Count);
    }

    // ---- helpers ----

    private static PrefixService Service(CountingPrefixSource source, TimeSpan? cacheTtl = null, TimeProvider? timeProvider = null, Func<string, Task>? onSourceChanged = null) =>
        new(new AppConfig(),
            // #263 made both required in production; neither is on the GetRuPrefixesAsync path this
            // fixture exercises, so the fake composition states that explicitly rather than relying
            // on a nullable parameter that production code could also leave unset.
            null!, // RipeStatPrefixCache — not on the GetRuPrefixesAsync path
            prefixSources: source,
            httpProvider: null!,
            ruCacheTtl: cacheTtl ?? TimeSpan.FromHours(1),
            logger: NullLogger<PrefixService>.Instance,
            timeProvider: timeProvider,
            onSourceChanged: onSourceChanged);

    /// <summary>
    /// Fake IPrefixSourceService that counts LoadDefaultAsync calls, can be made to fail, and can
    /// report a content change. Only LoadDefaultAsync is exercised by GetRuPrefixesAsync (#416 —
    /// the RU path no longer goes through GetDefaultAsync); the other members throw NotImplemented
    /// to catch accidental reliance.
    /// </summary>
    private sealed class CountingPrefixSource : IPrefixSourceService
    {
        private int _loadCalls;
        public int LoadCalls => Volatile.Read(ref _loadCalls);
        public bool FailNext { get; set; }
        /// <summary>Whether the next completed load reports a content change (#416 push trigger).</summary>
        public bool ChangedOnNextLoad { get; set; }

        public Task<IReadOnlyList<(PrefixSourceConfig Source, IReadOnlyList<IpPrefix> Prefixes)>> LoadAllAsync(CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<IpPrefix>> GetAsync(string name, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public async Task<(IReadOnlyList<IpPrefix> Prefixes, bool Changed)> LoadDefaultAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _loadCalls);
            // Simulate real I/O latency so concurrent callers actually overlap inside the gate.
            await Task.Delay(20, ct);
            if (FailNext) throw new InvalidOperationException("simulated default-source failure");
            var changed = ChangedOnNextLoad;
            ChangedOnNextLoad = false;
            IReadOnlyList<IpPrefix> prefixes = new List<IpPrefix>
            {
                new(0x0A000000u, 8),
                new(0xC0A80000u, 16),
            };
            return (prefixes, changed);
        }

        public Task<IReadOnlyList<IpPrefix>> GetDefaultAsync(CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RefreshAsync(string sourceName, CancellationToken ct = default) => Task.FromResult(false);
        public bool SourceSupportsConditional(string sourceName) => false;
    }
}
