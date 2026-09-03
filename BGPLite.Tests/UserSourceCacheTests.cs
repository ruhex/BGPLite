using BGPLite.Providers;
using Xunit;
using BGPLite.Protocol;

namespace BGPLite.Tests;

/// <summary>
/// Unit coverage for <see cref="UserSourceCache"/> (#150) — the URL-keyed TTL cache for per-peer
/// user-supplied prefix-list sources. Exercises the cache directly with a fake fetch delegate, so no
/// <c>HttpPrefixProvider</c>/HTTP layer is involved. Mirrors the shape of <c>PrefixSourceService</c>'s
/// cache: positive/negative TTL, per-key serialization, stale-on-failure, OCE propagation (#114).
/// </summary>
public class UserSourceCacheTests
{
    private static IReadOnlyList<IpPrefix> P(params (UInt128 Address, byte Length, bool IsIpv4)[] xs) =>
        xs.Select(x => new IpPrefix(x.Item1, x.Item2, x.Item3)).ToList();

    /// <summary>A controllable fetcher: counts calls, returns a canned list or throws.</summary>
    private sealed class Fetcher
    {
        public int Calls;
        public Func<IReadOnlyList<IpPrefix>>? OnSuccess;
        public Exception? Throw;
        public Task<IReadOnlyList<IpPrefix>> Invoke(CancellationToken ct)
        {
            Calls++;
            if (Throw is OperationCanceledException oce) throw oce;
            if (Throw is not null) throw Throw;
            return Task.FromResult(OnSuccess?.Invoke() ?? []);
        }
    }

    [Fact]
    public async Task Same_Url_Dedupes_Across_Calls_FetcherInvokedOnce()
    {
        // The point of URL-keying (#150): two calls for the same URL — e.g. two peers refreshing the
        // same popular list — share one fetch.
        var cache = new UserSourceCache();
        var f = new Fetcher { OnSuccess = () => P((0xC0A80000u, (byte)24, true)) };

        var a = await cache.GetOrLoadAsync("https://example.com/l", "src", f.Invoke, CancellationToken.None);
        var b = await cache.GetOrLoadAsync("https://example.com/l", "src", f.Invoke, CancellationToken.None);

        Assert.Equal(1, f.Calls);
        Assert.Same(a, b);
    }

    [Fact]
    public async Task Concurrent_Same_Url_Fetched_Once_Gate_Serializes()
    {
        // Exercises the per-URL SemaphoreSlim (the actual thundering-herd defense): many concurrent
        // callers for the same URL share a single fetch. A blocking fetcher holds all racers in-flight
        // until released, so this can't pass by accident via sequential cache reuse.
        var cache = new UserSourceCache();
        var hold = new TaskCompletionSource();
        int calls = 0;
        Task<IReadOnlyList<IpPrefix>> Load(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return HoldAndReturn();
        }
        async Task<IReadOnlyList<IpPrefix>> HoldAndReturn()
        {
            await hold.Task;          // keep the in-flight fetch blocked until all racers are queued
            return P((0u, (byte)0, true));
        }

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => cache.GetOrLoadAsync("https://example.com/l", "src", Load, CancellationToken.None))
            .ToArray();
        hold.SetResult();             // release the single fetch
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, calls);       // exactly one fetch despite 16 concurrent callers
        Assert.All(results, r => Assert.Single(r));
    }

    [Fact]
    public async Task Different_Urls_Fetched_Separately()
    {
        var cache = new UserSourceCache();
        var f = new Fetcher { OnSuccess = () => [] };

        await cache.GetOrLoadAsync("https://example.com/a", "a", f.Invoke, default);
        await cache.GetOrLoadAsync("https://example.com/b", "b", f.Invoke, default);

        Assert.Equal(2, f.Calls);
    }

    [Fact]
    public async Task Stale_On_Failure_Serves_Last_Good_Copy()
    {
        // Stale-serving only triggers on a refetch that fails — so let the positive entry expire first,
        // then make the refetch throw. The (now-expired) last good copy is served regardless of age.
        var cache = new UserSourceCache(positiveTtl: TimeSpan.FromMilliseconds(80));
        var f = new Fetcher { OnSuccess = () => P((0u, (byte)0, true)) };
        await cache.GetOrLoadAsync("https://example.com/l", "src", f.Invoke, default); // prime positive
        await Task.Delay(120);                                                        // let it expire

        f.OnSuccess = null;
        f.Throw = new InvalidOperationException("boom");
        var served = await cache.GetOrLoadAsync("https://example.com/l", "src", f.Invoke, default); // refetch fails → stale

        Assert.Equal(2, f.Calls);      // attempted refetch, failed, served stale
        Assert.Single(served);         // the primed prefix survived the transient failure
    }

    [Fact]
    public async Task Cold_Failure_Propagates_And_Negative_Caches()
    {
        var cache = new UserSourceCache();
        var f = new Fetcher { Throw = new InvalidOperationException("boom") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrLoadAsync("https://example.com/l", "src", f.Invoke, default));

        // Second call within the negative TTL returns [] WITHOUT invoking the fetcher — repeated
        // failures don't hammer the upstream.
        var served = await cache.GetOrLoadAsync("https://example.com/l", "src", f.Invoke, default);
        Assert.Equal(1, f.Calls);
        Assert.Empty(served);
    }

    [Fact]
    public async Task OperationCanceled_Propagates_And_Is_Not_Negative_Cached()
    {
        // #114: CALLER cancellation must propagate and must not be recorded as a negative entry.
        // #320 refined the discriminator: the cache rethrows only OCEs whose CALLER token is
        // cancelled — a foreign-token OCE (the per-fetch budget) is a load failure and IS
        // negative-cached (see ForeignTokenOCE_IsAFailure_NegativeCachedAndThrottled). The
        // faithful simulation is cancellation arriving WHILE the fetch is in flight (a
        // pre-cancelled token never even passes the gate).
        var cache = new UserSourceCache();
        var cts = new CancellationTokenSource();
        var calls = 0;
        // #358 review: a fixed delay does not prove the caller entered the loader — synchronize
        // on entry explicitly instead of racing Task.Run's start.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        static async Task<IReadOnlyList<IpPrefix>> Parked(CancellationToken ct, TaskCompletionSource entered)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return [];
        }

        Task<IReadOnlyList<IpPrefix>> Invoke(CancellationToken ct)
        {
            calls++;
            return Parked(ct, entered);
        }

        var fetch = Task.Run(() => cache.GetOrLoadAsync("https://example.com/l", "src", Invoke, cts.Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        Assert.Equal(1, calls);

        // No negative cache → the next call reaches the fetcher again.
        var f = new Fetcher { OnSuccess = () => P((1u, (byte)1, true)) };
        var served = await cache.GetOrLoadAsync("https://example.com/l", "src", f.Invoke, default);
        Assert.Equal(1, f.Calls); // a fresh fetcher ran once more
        Assert.Single(served);
    }

    [Fact]
    public async Task Positive_Ttl_Expiry_Triggers_Refetch()
    {
        var cache = new UserSourceCache(positiveTtl: TimeSpan.FromMilliseconds(80));
        var f = new Fetcher { OnSuccess = () => P((1u, (byte)1, true)) };

        await cache.GetOrLoadAsync("https://example.com/l", "src", f.Invoke, default);
        await Task.Delay(120);
        await cache.GetOrLoadAsync("https://example.com/l", "src", f.Invoke, default);

        Assert.Equal(2, f.Calls);
    }

    [Fact]
    public async Task Negative_Ttl_Expiry_Triggers_Refetch()
    {
        var cache = new UserSourceCache(negativeTtl: TimeSpan.FromMilliseconds(80));
        var f = new Fetcher { Throw = new InvalidOperationException("boom") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrLoadAsync("https://example.com/l", "src", f.Invoke, default));
        await Task.Delay(120);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrLoadAsync("https://example.com/l", "src", f.Invoke, default));

        Assert.Equal(2, f.Calls); // negative entry expired → refetched
    }

    /// <summary>
    /// #261: unique peer-supplied URLs must not grow the cache without bound — port of the #165
    /// cap. Inserting more distinct URLs than maxCacheEntries keeps the tracked count at the cap.
    /// </summary>
    [Fact]
    public async Task UniqueUrls_BeyondCap_KeepCacheBounded()
    {
        var cache = new UserSourceCache(maxCacheEntries: 5);
        var f = new Fetcher { OnSuccess = () => P((0x0A000000u, (byte)8, true)) };

        for (var i = 0; i < 12; i++)
            await cache.GetOrLoadAsync($"https://example.com/list-{i}", "src", f.Invoke, CancellationToken.None);

        Assert.Equal(5, cache.TrackedCount);
    }

    /// <summary>
    /// #261: the sweep drops the OLDEST entries (expired-first, then by CachedAt), so a fresh
    /// entry survives and an evicted one refetches on next use.
    /// </summary>
    [Fact]
    public async Task Eviction_DropsOldest_First_FreshEntriesSurvive()
    {
        var cache = new UserSourceCache(maxCacheEntries: 3);
        var calls = new Dictionary<string, int>();
        Task<IReadOnlyList<IpPrefix>> Load(string url) => Task.Run(async () =>
        {
            lock (calls) calls[url] = calls.TryGetValue(url, out var c) ? c + 1 : 1;
            await Task.Yield();
            return P((0x0A000000u, (byte)8, true));
        });

        await cache.GetOrLoadAsync("https://example.com/a", "a", ct => Load("a"), CancellationToken.None);
        await cache.GetOrLoadAsync("https://example.com/b", "b", ct => Load("b"), CancellationToken.None);
        await cache.GetOrLoadAsync("https://example.com/c", "c", ct => Load("c"), CancellationToken.None);
        await cache.GetOrLoadAsync("https://example.com/d", "d", ct => Load("d"), CancellationToken.None); // evicts "a"

        Assert.Equal(3, cache.TrackedCount);
        lock (calls) Assert.Equal(1, calls["d"]); // fresh entry is cached
        lock (calls) Assert.Equal(1, calls["b"]); // survivor served from cache below
        lock (calls) Assert.Equal(1, calls["c"]);

        var b = await cache.GetOrLoadAsync("https://example.com/b", "b", ct => Load("b"), CancellationToken.None);
        Assert.NotNull(b);
        lock (calls) Assert.Equal(1, calls["b"]); // still cached — no refetch

        var a = await cache.GetOrLoadAsync("https://example.com/a", "a", ct => Load("a"), CancellationToken.None);
        Assert.NotNull(a);
        lock (calls) Assert.Equal(2, calls["a"]); // evicted earlier → refetched
    }

    /// <summary>
    /// #320: a fetch budget fires as a foreign-token OCE (live caller token). It is a load
    /// FAILURE, not teardown: it still throws (the caller's #342 boundary treats it as a
    /// per-source failure), it is negative-cached, and the negative entry throttles the next
    /// call — no loader invocation, no re-paying the budget.
    /// </summary>
    [Fact]
    public async Task ForeignTokenOCE_IsAFailure_NegativeCachedAndThrottled()
    {
        var cache = new UserSourceCache(negativeTtl: TimeSpan.FromSeconds(5));
        var f = new Fetcher { Throw = new OperationCanceledException() }; // no live caller token attached

        // First call: the budget fired → OCE propagates (failure semantics), entry negative-cached.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.GetOrLoadAsync("https://example.com/l", "src", f.Invoke, CancellationToken.None));

        // Second call within the negative TTL: throttled — the loader is NOT invoked again and
        // the negative entry reports "no prefixes" instead of re-paying the budget.
        var second = await cache.GetOrLoadAsync("https://example.com/l", "src", f.Invoke, CancellationToken.None);

        Assert.Empty(second);
        Assert.Equal(1, f.Calls);
    }

    /// <summary>
    /// #358 review (orphan-lock hygiene for #261): a caller cancelled while queued on the gate
    /// never writes a cache entry, so its freshly-created gate had nothing to evict — cancelled
    /// URLs accumulated SemaphoreSlims forever. The cancellation path must pair-remove its gate.
    /// </summary>
    [Fact]
    public async Task CancelledWhileQueued_DoesNotLeaveAnOrphanGate()
    {
        var cache = new UserSourceCache();
        Assert.Equal(0, cache.TrackedGateCount);

        var holder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdCts = new CancellationTokenSource();

        Task<IReadOnlyList<IpPrefix>> Hold(CancellationToken ct)
        {
            entered.TrySetResult();
            return holder.Task.ContinueWith<IReadOnlyList<IpPrefix>>(_ => [], CancellationToken.None);
        }

        // First caller holds the gate without completing.
        var holding = cache.GetOrLoadAsync("https://example.com/l", "src", Hold, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Second caller queues on the same gate, then is cancelled while queued.
        var queuedCts = new CancellationTokenSource();
        var queued = Task.Run(() => cache.GetOrLoadAsync("https://example.com/l", "src", Hold, queuedCts.Token));
        await Task.Delay(50);            // let it queue behind the holder
        queuedCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        // Release the holder; its load completes and caches the entry. The cancelled caller must
        // not have left a gate dictionary that grows: still exactly one gate for one url.
        holder.TrySetResult();
        await holding.WaitAsync(TimeSpan.FromSeconds(5));

        // The cancelled caller's pair-remove took the SHARED gate instance (the holder kept
        // running on its own reference — the accepted duplicate-fetch tradeoff), and nothing
        // re-adds a gate until the next call: zero gates, one cached entry, no orphan.
        Assert.Equal(0, cache.TrackedGateCount);
        Assert.Equal(1, cache.TrackedCount);
    }

    /// <summary>
    /// #358 review: repeated cancelled-then-never-loaded URLs must not accumulate gates — the
    /// observable orphan-growth case (fresh url per cancelled call, no cache entry ever written).
    /// </summary>
    [Fact]
    public async Task CancelledFreshUrls_DoNotAccumulateGates()
    {
        var cache = new UserSourceCache();

        for (var i = 0; i < 5; i++)
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();   // cancelled before the gate is even acquired
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                cache.GetOrLoadAsync($"https://example.com/never-{i}", "src", ct => Task.FromResult<IReadOnlyList<IpPrefix>>([]), cts.Token));
        }

        Assert.Equal(0, cache.TrackedGateCount);   // pre-fix: 5 orphan gates
    }
}
