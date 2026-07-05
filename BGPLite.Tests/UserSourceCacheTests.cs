using BGPLite.Providers;
using Xunit;

namespace BGPLite.Tests;

/// <summary>
/// Unit coverage for <see cref="UserSourceCache"/> (#150) — the URL-keyed TTL cache for per-peer
/// user-supplied prefix-list sources. Exercises the cache directly with a fake fetch delegate, so no
/// <c>HttpPrefixProvider</c>/HTTP layer is involved. Mirrors the shape of <c>PrefixSourceService</c>'s
/// cache: positive/negative TTL, per-key serialization, stale-on-failure, OCE propagation (#114).
/// </summary>
public class UserSourceCacheTests
{
    private static IReadOnlyList<(uint Prefix, byte Length)> P(params (uint, byte)[] xs) =>
        xs.Select(x => (x.Item1, x.Item2)).ToList();

    /// <summary>A controllable fetcher: counts calls, returns a canned list or throws.</summary>
    private sealed class Fetcher
    {
        public int Calls;
        public Func<IReadOnlyList<(uint Prefix, byte Length)>>? OnSuccess;
        public Exception? Throw;
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> Invoke(CancellationToken ct)
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
        var f = new Fetcher { OnSuccess = () => P((0xC0A80000u, (byte)24)) };

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
        Task<IReadOnlyList<(uint Prefix, byte Length)>> Load(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return HoldAndReturn();
        }
        async Task<IReadOnlyList<(uint Prefix, byte Length)>> HoldAndReturn()
        {
            await hold.Task;          // keep the in-flight fetch blocked until all racers are queued
            return P((0u, 0));
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
        var f = new Fetcher { OnSuccess = () => P((0u, 0)) };
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
        // #114: cancellation must propagate and must not be recorded as a negative entry.
        var cache = new UserSourceCache();
        var f = new Fetcher { Throw = new OperationCanceledException() };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => cache.GetOrLoadAsync("https://example.com/l", "src", f.Invoke, default));
        Assert.Equal(1, f.Calls);

        // No negative cache → the next call reaches the fetcher again.
        f.Throw = null;
        f.OnSuccess = () => P((1u, 1));
        var served = await cache.GetOrLoadAsync("https://example.com/l", "src", f.Invoke, default);
        Assert.Equal(2, f.Calls);
        Assert.Single(served);
    }

    [Fact]
    public async Task Positive_Ttl_Expiry_Triggers_Refetch()
    {
        var cache = new UserSourceCache(positiveTtl: TimeSpan.FromMilliseconds(80));
        var f = new Fetcher { OnSuccess = () => P((1u, 1)) };

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
}
