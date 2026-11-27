using BGPLite.Configuration;
using BGPLite.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BGPLite.Protocol;

namespace BGPLite.Tests;

/// <summary>
/// Regression coverage: GetRuPrefixesAsync used plain non-volatile fields shared across
/// concurrent sessions — torn reads (new reference with old timestamp) and thundering herd (N
/// concurrent callers all fetched the default source on TTL expiry). The fix adds Volatile/Interlocked
/// field access + a SemaphoreSlim gate + stale-on-failure, mirroring the per-ASN cache.
/// </summary>
public class PrefixCacheRaceTests
{
    /// <summary>
    /// N concurrent callers on a cold cache must share a single default-source fetch
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
    /// After the TTL elapses, concurrent callers again share a single fetch (the gate
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
    /// Stale-on-failure parity with GetPrefixesAsync: when the default-source fetch
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

        // The re-fetch fails, but the stale cached copy is served (NOT thrown) — stale-on-failure parity.
        var stale = await service.GetRuPrefixesAsync();
        Assert.Equal(first, stale);                  // same projection as the original good copy
        Assert.Equal(2, source.LoadCalls);        // the failed fetch DID happen (expired cache)
    }

    /// <summary>
    /// When the fetch fails AND there is no cached copy (first-ever call), the exception
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
    /// A changed default-source load must not deadlock when the convergence push re-enters
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
            // The production push (RefreshAllEstablishedAsync) rebuilds every established session's
            // route set; a RU-consuming session's build re-enters GetRuPrefixesAsync right here.
            reentrant = service.GetRuPrefixesAsync();
            // Signalled only once the re-entry is recorded: the push runs detached, so the test
            // must never get past its wait while `reentrant` still holds its initial value.
            pushStarted.TrySetResult();
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
    /// A changed default-source load starts the push but does not wait for it: the push refreshes
    /// every established session and can take minutes, while the load is already complete. The
    /// caller — an API request under its budget, or a session's route build — is not held by it.
    /// </summary>
    [Fact]
    public async Task GetRuPrefixesAsync_ChangedLoad_StartsThePush_ButDoesNotWaitForIt()
    {
        var source = new CountingPrefixSource { ChangedOnNextLoad = true };
        var push = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pushes = 0;
        var service = Service(source, onSourceChanged: _ => { Interlocked.Increment(ref pushes); started.TrySetResult(); return push.Task; });

        await service.GetRuPrefixesAsync().WaitAsync(TimeSpan.FromSeconds(5));   // pre-fix: TimeoutException — the load waits for the push
        Assert.Equal(1, service.PendingPushes);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, pushes);

        push.SetResult();   // nothing left running for the next test
        await PrefixPushWait.IdleAsync(() => service.PendingPushes);
    }

    /// <summary>
    /// Nothing of the push may run on the caller's flow (see the same test in
    /// <c>PrefixSourceServiceTests</c>): a route build that committed a change must not carry
    /// every idle session's refresh.
    /// </summary>
    [Fact]
    public async Task GetRuPrefixesAsync_ChangedLoad_RunsThePushOffTheCallersFlow()
    {
        using var blocked = new ManualResetEventSlim();
        var service = Service(
            new CountingPrefixSource { ChangedOnNextLoad = true },
            onSourceChanged: _ => { blocked.Wait(); return Task.CompletedTask; });   // blocks before it ever yields

        var load = service.GetRuPrefixesAsync();
        try
        {
            // Returns although the push has not got past its first line.
            Assert.Equal(2, (await load.WaitAsync(TimeSpan.FromSeconds(5))).Count);
            Assert.Equal(1, service.PendingPushes);
        }
        finally
        {
            blocked.Set();
        }
        await PrefixPushWait.IdleAsync(() => service.PendingPushes);
    }

    /// <summary>
    /// A push that fails is logged and observed by this service's copy of the push hand-off too —
    /// it must not surface from the load, nor go unobserved on the thread pool.
    /// </summary>
    [Fact]
    public async Task GetRuPrefixesAsync_FailingPush_IsLogged_NotSurfacedFromTheLoad()
    {
        var logger = new RecordingLogger();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new PrefixService(
            new AppConfig(),
            null!,   // RipeStatPrefixCache — not on the GetRuPrefixesAsync path
            new CountingPrefixSource { ChangedOnNextLoad = true },
            null!,   // the HTTP provider is only on the per-peer user-source path
            logger: logger,
            onSourceChanged: async _ =>
            {
                await release.Task;
                throw new InvalidOperationException("push failed");
            });

        Assert.Equal(2, (await service.GetRuPrefixesAsync().WaitAsync(TimeSpan.FromSeconds(5))).Count);
        Assert.Empty(logger.Messages);
        release.TrySetResult();
        // The push logs before it finishes: once none is pending, the log is complete to read.
        await PrefixPushWait.IdleAsync(() => service.PendingPushes);

        var warning = Assert.Single(logger.Messages);
        Assert.Contains("OnSourceChanged callback failed for the default prefix source", warning);
        Assert.Contains("push failed", warning);
    }

    /// <summary>Records every formatted log message with its exception.</summary>
    private sealed class RecordingLogger : ILogger<PrefixService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add($"{formatter(state, exception)} | {exception}");
        }
    }

    /// <summary>
    /// The RU push fires exactly when the load reports a content change — not on the fresh
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

        // Cold load reporting changed → push #1 (on the thread pool: wait for it to finish).
        source.ChangedOnNextLoad = true;
        await service.GetRuPrefixesAsync();
        await PrefixPushWait.IdleAsync(() => service.PendingPushes);
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
        await PrefixPushWait.IdleAsync(() => service.PendingPushes);
        Assert.Equal(2, pushes.Count);
    }

    [Fact]
    /// <summary>Commits during an old load and verifies that generation validation rejects its stale projection.</summary>
    public async Task GetRuPrefixesAsync_CommitDuringOldLoad_DoesNotPublishStaleProjection()
    {
        var source = new CountingPrefixSource();
        var service = Service(source, defaultSource: string.Empty);
        var committed = false;
        service.RuPublishProbe = () =>
        {
            if (committed) return;
            committed = true;
            source.Commit([new IpPrefix(0xCB000000u, 8)]); // 203.0.0.0/8
        };

        await service.GetRuPrefixesAsync();
        var latest = await service.GetRuPrefixesAsync();

        var prefix = Assert.Single(latest);
        Assert.Equal(0xCB000000u, prefix.Prefix);
        Assert.Equal(2, source.LoadCalls); // stale publication was rejected, so the new content reloads
    }

    /// <summary>Pauses publication after its generation check and commits at the invalidation-lock boundary; no timing delay decides the order.</summary>
    [Fact]
    public async Task GetRuPrefixesAsync_CommitAfterGenerationCheck_CannotRestoreStaleProjection()
    {
        var source = new CountingPrefixSource();
        var service = Service(source, defaultSource: string.Empty);
        using var generationChecked = new ManualResetEventSlim();
        using var releasePublication = new ManualResetEventSlim();
        using var invalidationStarted = new ManualResetEventSlim();
        service.RuInvalidationProbe = invalidationStarted.Set;
        service.RuAfterGenerationCheckProbe = () =>
        {
            generationChecked.Set();
            releasePublication.Wait(TimeSpan.FromSeconds(5));
        };

        var oldLoad = Task.Run(() => service.GetRuPrefixesAsync());
        Assert.True(generationChecked.Wait(TimeSpan.FromSeconds(5)));
        var commit = Task.Run(() => source.Commit([new IpPrefix(0xCB000000u, 8)]));
        Assert.True(invalidationStarted.Wait(TimeSpan.FromSeconds(5)));
        AssertPublicationLockUnavailable(service);
        releasePublication.Set();
        await Task.WhenAll(oldLoad, commit);
        var latest = await service.GetRuPrefixesAsync();

        var prefix = Assert.Single(latest);
        Assert.Equal(0xCB000000u, prefix.Prefix);
        Assert.Equal(2, source.LoadCalls);
    }

    /// <summary>Proves that invalidation holds the same lock used by publication and never restores stale content.</summary>
    [Fact]
    public async Task SourceCommit_InvalidationCriticalSection_ExcludesPublicationLock()
    {
        var source = new CountingPrefixSource();
        var service = Service(source, defaultSource: string.Empty);
        using var invalidationHeld = new ManualResetEventSlim();
        using var releaseInvalidation = new ManualResetEventSlim();
        service.RuInvalidationCriticalProbe = () =>
        {
            invalidationHeld.Set();
            releaseInvalidation.Wait(TimeSpan.FromSeconds(5));
        };

        var commit = Task.Run(() => source.Commit([new IpPrefix(0xCB000000u, 8)]));
        Assert.True(invalidationHeld.Wait(TimeSpan.FromSeconds(5)));
        AssertPublicationLockUnavailable(service);
        releaseInvalidation.Set();
        await commit;

        var latest = await service.GetRuPrefixesAsync();
        Assert.Equal(0xCB000000u, Assert.Single(latest).Prefix);
    }

    /// <summary>Asserts mutual exclusion without leaving the lock held when the assertion fails.</summary>
    private static void AssertPublicationLockUnavailable(PrefixService service)
    {
        var entered = Monitor.TryEnter(service.RuPublicationSync);
        if (entered)
            Monitor.Exit(service.RuPublicationSync);
        Assert.False(entered, "publication or invalidation must hold the shared lock at this checkpoint");
    }

    // ---- helpers ----

    /// <summary>Composes only the dependencies used by the RU cache path, with explicit nulls for unreachable collaborators.</summary>
    private static PrefixService Service(CountingPrefixSource source, TimeSpan? cacheTtl = null, TimeProvider? timeProvider = null, Func<string, Task>? onSourceChanged = null, string? defaultSource = null) =>
        new(new AppConfig { DefaultPrefixSource = defaultSource },
            // Both are required in production; neither is on the GetRuPrefixesAsync path this
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
    /// report a content change. Only LoadDefaultAsync is exercised by GetRuPrefixesAsync (the
    /// RU path no longer goes through GetDefaultAsync); the other members throw NotImplemented
    /// to catch accidental reliance.
    /// </summary>
    private sealed class CountingPrefixSource : IPrefixSourceService
    {

        public event Action<string>? ContentCommitted;
        private int _loadCalls;
        public int LoadCalls => Volatile.Read(ref _loadCalls);
        public bool FailNext { get; set; }
        /// <summary>Whether the next completed load reports a content change (the push trigger).</summary>
        public bool ChangedOnNextLoad { get; set; }
        public IReadOnlyList<IpPrefix> Current { get; private set; } =
            [new IpPrefix(0x0A000000u, 8), new IpPrefix(0xC0A80000u, 16)];

        /// <summary>Changes source content and fires the same commit event used by production refresh.</summary>
        public void Commit(IReadOnlyList<IpPrefix> prefixes)
        {
            Current = prefixes;
            ContentCommitted?.Invoke(string.Empty);
        }

        public Task<IReadOnlyList<(PrefixSourceConfig Source, IReadOnlyList<IpPrefix> Prefixes)>> LoadAllAsync(CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<IpPrefix>> GetAsync(string name, CancellationToken ct = default) =>
            throw new NotImplementedException();

        /// <summary>Counts default-source loads and provides controllable latency, failure, and change signals.</summary>
        public async Task<(IReadOnlyList<IpPrefix> Prefixes, bool Changed)> LoadDefaultAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _loadCalls);
            // Simulate real I/O latency so concurrent callers actually overlap inside the gate.
            await Task.Delay(20, ct);
            if (FailNext) throw new InvalidOperationException("simulated default-source failure");
            var changed = ChangedOnNextLoad;
            ChangedOnNextLoad = false;
            return (Current, changed);
        }

        public Task<IReadOnlyList<IpPrefix>> GetDefaultAsync(CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RefreshAsync(string sourceName, CancellationToken ct = default) => Task.FromResult(false);
        public bool SourceSupportsConditional(string sourceName) => false;
        public bool TryGetCached(string name, out IReadOnlyList<IpPrefix>? prefixes)
        {
            prefixes = null;
            return false;
        }
    }
}
