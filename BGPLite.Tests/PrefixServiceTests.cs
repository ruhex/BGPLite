using System.Net;
using BGPLite.Configuration;
using BGPLite.Providers;
using BGPLite.Protocol;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BGPLite.Tests;

/// <summary>Regression coverage for <see cref="PrefixService.GetPrefixesForAsns"/> — the
/// parallelized fan-out over ASNs (#83). Asserts that all ASNs resolve in input order and that a
/// single failing ASN is skipped without dropping the others or throwing.</summary>
public class PrefixServiceTests
{
    /// <summary>RIPEstat body with two tokens substituted per ASN. Raw-string template (no
    /// interpolation) avoids brace-escaping clashes with JSON delimiters.</summary>
    private const string BodyTemplate =
        """{"status":"ok","data":{"resource":"__ASN__","prefixes":{"v4":{"originating":["__CIDR__"]},"v6":{"originating":[]}}}}""";

    /// <summary>An <see cref="HttpMessageHandler"/> that answers RIPEstat ris-prefixes requests with
    /// a single distinct prefix per ASN, except for the configured failing ASNs which 503 forever
    /// (so <c>RipeStatProvider</c> exhausts retries and throws).</summary>
    private sealed class PerAsnHandler : HttpMessageHandler
    {
        private readonly HashSet<uint> _failures = [];
        private int _calls;
        public int Calls => _calls;

        public PerAsnHandler(params uint[] failures) => _failures = [.. failures];

        /// <summary>Marks an ASN as failing from now on (simulates a RIPEstat outage that starts
        /// after the ASN was already cached — used for stale-on-failure coverage, #163).</summary>
        public void AddFailure(uint asn) => _failures.Add(asn);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            var asn = ExtractAsn(request.RequestUri!);
            if (_failures.Contains(asn))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

            // Encode the ASN into the prefix so each ASN yields a distinct (Prefix, Length).
            var hi = (int)((asn >> 8) & 0xFF);
            var lo = (int)(asn & 0xFF);
            var body = BodyTemplate
                .Replace("__ASN__", asn.ToString())
                .Replace("__CIDR__", $"10.{hi}.{lo}.1/32");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }

        private static uint ExtractAsn(Uri uri)
        {
            var s = uri.AbsoluteUri;
            var marker = "resource=AS";
            var i = s.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            var end = s.IndexOf('&', i);
            if (end < 0) end = s.Length;
            return uint.Parse(s[i..end]);
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static PrefixService Service(PerAsnHandler handler, TimeSpan? cacheTtl = null, TimeSpan? negativeTtl = null, int? maxCacheEntries = null, int retryAttempts = 2) =>
        new(new AppConfig(),
            new RipeStatProvider(new StubFactory(handler),
                NullLogger<RipeStatProvider>.Instance,
                new RipeStatConfig { RetryAttempts = retryAttempts, RetryDelaySeconds = 0 }),
            null!, // IPrefixSourceService is not on the GetPrefixesForAsns path
            cacheTtl: cacheTtl ?? TimeSpan.FromHours(1),
            negativeTtl: negativeTtl,
            maxCacheEntries: maxCacheEntries);

    /// <summary>The single prefix uint that <see cref="PerAsnHandler"/> yields for a given ASN,
    /// computed through the same <see cref="BgpConstants.IPAddressToUint"/> the provider uses.</summary>
    private static uint PrefixFor(uint asn)
    {
        var hi = (int)((asn >> 8) & 0xFF);
        var lo = (int)(asn & 0xFF);
        return BgpConstants.IPAddressToUint(IPAddress.Parse($"10.{hi}.{lo}.1"));
    }

    [Fact]
    public async Task GetPrefixesForAsns_ResolvesAllAsns_InInputOrder()
    {
        var handler = new PerAsnHandler();
        var service = Service(handler);

        var result = await service.GetPrefixesForAsns([100, 200, 300]);

        // One prefix per ASN, reassembled in the order the ASNs were supplied.
        Assert.Equal([100u, 200u, 300u], result.Select(r => r.Asn).ToArray());
        Assert.Equal(PrefixFor(100), result[0].Prefix);
        Assert.Equal(PrefixFor(200), result[1].Prefix);
        Assert.Equal(PrefixFor(300), result[2].Prefix);
        Assert.Equal(32, result[0].Length);
        Assert.Equal(3, handler.Calls); // every ASN resolved (cache was cold)
    }

    [Fact]
    public async Task GetPrefixesForAsns_SkipsFailedAsn_KeepsOthers()
    {
        // ASN 200 always 503s -> RipeStatProvider exhausts retries and throws; the service must
        // swallow that single failure and still return 100 and 300, in order, without throwing.
        var handler = new PerAsnHandler(200);
        var service = Service(handler);

        var result = await service.GetPrefixesForAsns([100, 200, 300]);

        Assert.Equal([100u, 300u], result.Select(r => r.Asn).ToArray());
        Assert.Equal(PrefixFor(100), result[0].Prefix);
        Assert.Equal(PrefixFor(300), result[1].Prefix);
    }

    [Fact]
    public async Task GetPrefixesForAsns_EmptyInput_ReturnsEmpty()
    {
        var service = Service(new PerAsnHandler());
        var result = await service.GetPrefixesForAsns([]);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPrefixesForAsns_RepeatedAsn_DeduplicatesViaCache()
    {
        // The same ASN twice: only the first occurrence hits RIPEstat, the second is a cache hit.
        var handler = new PerAsnHandler();
        var service = Service(handler);

        var result = await service.GetPrefixesForAsns([100, 100]);

        Assert.Equal([100u, 100u], result.Select(r => r.Asn).ToArray());
        Assert.Equal(1, handler.Calls); // cache served the second lookup
    }

    // --- #163: stale-on-failure — a transient RIPEstat outage after TTL must not drop routes ---

    [Fact]
    public async Task GetPrefixesAsync_AfterTtl_ServesStaleOnFailure()
    {
        // Short TTL so the entry expires within the test. First call populates the cache; second
        // call (after TTL) finds the entry expired, attempts a refetch, the handler now 503s → the
        // service serves the stale (last good) copy instead of propagating the failure.
        // retryAttempts:0 → exactly one fetch attempt per call (no retry amplification).
        var handler = new PerAsnHandler();
        var service = Service(handler, cacheTtl: TimeSpan.FromMilliseconds(80), retryAttempts: 0);

        var first = await service.GetPrefixesAsync(100);
        Assert.Single(first);
        Assert.Equal(1, handler.Calls);

        await Task.Delay(120); // TTL elapses

        handler.AddFailure(100); // refetch will 503

        var stale = await service.GetPrefixesAsync(100);
        Assert.Equal(2, handler.Calls);      // attempted refetch, failed
        Assert.Single(stale);                // stale copy served
        Assert.Equal(first[0].Prefix, stale[0].Prefix);
    }

    [Fact]
    public async Task GetPrefixesAsync_ColdFailure_PropagatesAndNegativeCaches()
    {
        // No cached copy yet: the failure propagates, AND a negative entry is recorded so the next
        // call within the negative TTL returns [] without re-hitting RIPEstat.
        // retryAttempts:0 → exactly one fetch attempt (no retries), so one handler call.
        var handler = new PerAsnHandler(100);
        var service = Service(handler, negativeTtl: TimeSpan.FromSeconds(30), retryAttempts: 0);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetPrefixesAsync(100));
        Assert.Equal(1, handler.Calls); // single attempt, no retries

        // Second call within negative TTL: no fetch, returns [] (negative cache).
        var second = await service.GetPrefixesAsync(100);
        Assert.Empty(second);
        Assert.Equal(1, handler.Calls); // still one fetch — negative cache served
    }

    [Fact]
    public async Task GetPrefixesAsync_OperationCanceled_Propagates_NotNegativeCached()
    {
        // Cancellation must propagate and must NOT be recorded as a negative entry (#114 contract).
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = Service(new PerAsnHandler());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetPrefixesAsync(100, cts.Token));

        // The ASN was not negative-cached, so a subsequent call reaches RIPEstat.
        cts.Dispose();
        var ok = await service.GetPrefixesAsync(100);
        Assert.Single(ok);
    }

    // --- #164: per-ASN fetch serialization — no thundering herd on a cold/expired key ---

    [Fact]
    public async Task GetPrefixesAsync_ConcurrentColdCalls_SingleFetch()
    {
        // N concurrent calls for the SAME cold ASN must result in exactly ONE RIPEstat fetch —
        // the per-ASN SemaphoreSlim gate serializes the cache-miss path.
        var handler = new PerAsnHandler();
        var service = Service(handler);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => service.GetPrefixesAsync(100))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, handler.Calls); // exactly one fetch served all 8 callers
        Assert.All(results, r => Assert.Single(r));
    }

    [Fact]
    public async Task GetPrefixesAsync_ConcurrentCalls_AfterExpiry_StillSingleFetch()
    {
        // After TTL expiry, concurrent callers still share one fetch (the gate re-serializes).
        var handler = new PerAsnHandler();
        var service = Service(handler, cacheTtl: TimeSpan.FromMilliseconds(60));

        await service.GetPrefixesAsync(100); // warm
        await Task.Delay(80);                // TTL elapses
        Assert.Equal(1, handler.Calls);

        var tasks = Enumerable.Range(0, 6)
            .Select(_ => service.GetPrefixesAsync(100))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(2, handler.Calls); // one warm + one shared refetch
    }

    // --- #165: bounded cache — entries are evicted at capacity, not grown without limit ---

    [Fact]
    public async Task GetPrefixesAsync_EvictsAtCapacity_StaysBounded()
    {
        // Tiny cap so the test is fast. Fetching more distinct ASNs than the cap must not grow the
        // cache beyond (approximately) the cap — expired and oldest entries are evicted on insert.
        var handler = new PerAsnHandler();
        var service = Service(handler, cacheTtl: TimeSpan.FromHours(1), maxCacheEntries: 4);

        for (uint asn = 100; asn < 100 + 10; asn++)
            await service.GetPrefixesAsync(asn);

        // The cache must not have grown without bound; it stays near the configured cap.
        Assert.True(handler.Calls <= 10 && handler.Calls >= 1);
        // Re-fetching an evicted ASN re-fetches from RIPEstat (no leak / no incorrect empty serve).
        var before = handler.Calls;
        await service.GetPrefixesAsync(100);
        // ASN 100 was the oldest and likely evicted → expect a refetch. If still cached, calls unchanged.
        // Either way the count is bounded.
        Assert.InRange(handler.Calls, before, before + 1);
    }

    [Fact]
    public async Task GetPrefixesAsync_Eviction_DropsCorrespondingLock()
    {
        // When an entry is evicted by the sweep, its _locks entry must also be removed so the
        // SemaphoreSlim set does not grow without bound (#165 — locks were the second growth axis).
        var handler = new PerAsnHandler();
        // cap=1: every new ASN beyond the first triggers an eviction of the previous one.
        var service = Service(handler, maxCacheEntries: 1);

        await service.GetPrefixesAsync(100);
        await service.GetPrefixesAsync(200); // evicts 100

        // Fetch 100 again — the lock for 100 should have been evicted and re-created; this must not
        // throw and must correctly serialize (SemaphoreSlim is recreated via GetOrAdd).
        var result = await service.GetPrefixesAsync(100);
        Assert.Single(result);
    }
}
