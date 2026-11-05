using System.Net;
using System.Net.Http;
using BGPLite.Configuration;
using BGPLite.Providers;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BGPLite.Tests;

public class RipeStatGateEvictionTests
{
    [Fact]
    public async Task ExpiredGateEviction_RacingRefetch_KeepsSingleFlight()
    {
        var handler = new BlockingHandler();
        var time = new FakeTimeProvider();
        var cache = new RipeStatPrefixCache(
            new RipeStatProvider(new StubFactory(handler), NullLogger<RipeStatProvider>.Instance,
                new RipeStatConfig { RetryAttempts = 0, RetryDelaySeconds = 0 }),
            cacheTtl: TimeSpan.FromMilliseconds(100), timeProvider: time, sweepEvery: 1000);
        await cache.GetPrefixesAsync(100);
        time.Advance(TimeSpan.FromMilliseconds(150));

        var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.EvictionProbe = _ =>
        {
            probeEntered.TrySetResult();
            releaseProbe.Task.GetAwaiter().GetResult();
        };
        handler.Hold = true;

        try
        {
            var sweep = Task.Run(cache.SweepExpired);
            await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var firstRefetch = Task.Run(() => cache.GetPrefixesAsync(100));
            releaseProbe.TrySetResult();
            await sweep.WaitAsync(TimeSpan.FromSeconds(5));
            await handler.FirstRefetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var secondRefetch = cache.GetPrefixesAsync(100);
            await Task.WhenAny(handler.SecondRefetchEntered.Task, Task.Delay(100));
            handler.Release.TrySetResult();
            await Task.WhenAll(firstRefetch, secondRefetch).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, handler.Calls); // initial seed + one shared refetch
            Assert.Equal(1, handler.MaxActive);
        }
        finally
        {
            releaseProbe.TrySetResult();
            handler.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task CapacityGateEviction_RacingRefetch_KeepsSingleFlight()
    {
        var handler = new BlockingHandler { FirstRefetchCall = 3, HoldFromCall = 3 };
        var time = new FakeTimeProvider();
        var cache = new RipeStatPrefixCache(
            new RipeStatProvider(new StubFactory(handler), NullLogger<RipeStatProvider>.Instance,
                new RipeStatConfig { RetryAttempts = 0, RetryDelaySeconds = 0 }),
            maxCacheEntries: 1, cacheTtl: TimeSpan.FromMilliseconds(100), timeProvider: time, sweepEvery: 1000);
        await cache.GetPrefixesAsync(100);
        time.Advance(TimeSpan.FromMilliseconds(150));

        var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.EvictionProbe = _ =>
        {
            probeEntered.TrySetResult();
            releaseProbe.Task.GetAwaiter().GetResult();
        };
        handler.Hold = true;

        try
        {
            var evictionFetch = Task.Run(() => cache.GetPrefixesAsync(200));
            await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var firstRefetch = Task.Run(() => cache.GetPrefixesAsync(100));
            releaseProbe.TrySetResult();
            await evictionFetch.WaitAsync(TimeSpan.FromSeconds(5));
            await handler.FirstRefetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var secondRefetch = cache.GetPrefixesAsync(100);
            await Task.WhenAny(handler.SecondRefetchEntered.Task, Task.Delay(100));
            handler.Release.TrySetResult();
            await Task.WhenAll(evictionFetch, firstRefetch, secondRefetch).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(3, handler.Calls); // AS100 seed + AS200 + one shared AS100 refetch
            Assert.Equal(1, handler.MaxActive);
        }
        finally
        {
            releaseProbe.TrySetResult();
            handler.Release.TrySetResult();
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private int _calls;
        private int _active;
        public int Calls => Volatile.Read(ref _calls);
        public int MaxActive { get; private set; }
        public bool Hold { get; set; }
        public int FirstRefetchCall { get; init; } = 2;
        public int HoldFromCall { get; init; } = 2;
        public TaskCompletionSource FirstRefetchEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRefetchEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Controls concurrent RIPEstat responses to exercise gate eviction at deterministic checkpoints.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            if (active > MaxActive) MaxActive = active;
            try
            {
                if (Hold && call >= HoldFromCall)
                {
                    if (call == FirstRefetchCall) FirstRefetchEntered.TrySetResult();
                    if (call > FirstRefetchCall) SecondRefetchEntered.TrySetResult();
                    await Release.Task.WaitAsync(ct);
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"status":"ok","data":{"resource":"AS100","prefixes":{"v4":{"originating":["10.0.0.0/8"]},"v6":{"originating":[]}}}}""")
                };
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
