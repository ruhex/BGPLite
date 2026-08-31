using System.Net;
using BGPLite.Configuration;
using BGPLite.Providers;
using BGPLite.Protocol;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
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

    private static PrefixService Service(PerAsnHandler handler, TimeSpan? cacheTtl = null, TimeSpan? negativeTtl = null, int? maxCacheEntries = null, int retryAttempts = 2, ILogger<PrefixService>? logger = null) =>
        new(new AppConfig(),
            // #267 item 5: per-ASN TTL/negative-TTL/eviction knobs moved into the shared cache.
            new RipeStatPrefixCache(
                new RipeStatProvider(new StubFactory(handler),
                    NullLogger<RipeStatProvider>.Instance,
                    new RipeStatConfig { RetryAttempts = retryAttempts, RetryDelaySeconds = 0 }),
                cacheTtl: cacheTtl,
                negativeTtl: negativeTtl,
                maxCacheEntries: maxCacheEntries),
            null!, // IPrefixSourceService is not on the GetPrefixesForAsns path
            null!, // HttpPrefixProvider is only on the per-peer user-source path (#263)
            logger: logger);

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
    public async Task GetPrefixesForAsns_FailedAsn_LogsWarning()
    {
        // #330: a transient RIPEstat failure for one ASN must not be silent — its prefixes vanish
        // from this cycle's advertisement and the operator has to tell "no prefixes" from
        // "fetch failed". Previously the bare catch returned [] without any log line.
        var handler = new PerAsnHandler(200);
        var logger = new CapturingLogger();
        var service = Service(handler, logger: logger);

        await service.GetPrefixesForAsns([100, 200, 300]);

        Assert.Contains(logger.Entries, e => e.Contains("AS200") && e.Contains("RIPEstat resolve failed"));
    }

    private sealed class CapturingLogger : ILogger<PrefixService>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(formatter(state, exception));
    }

    [Fact]
    public async Task GetPrefixesForAsns_EmptyInput_ReturnsEmpty()
    {
        var service = Service(new PerAsnHandler());
        var result = await service.GetPrefixesForAsns([]);
        Assert.Empty(result);
    }

    /// <summary>
    /// Regression for #225: when the cancellation token is cancelled, GetPrefixesForAsns must
    /// throw OperationCanceledException (or a TaskCanceledException subclass — the OCE family)
    /// instead of returning a partial list (cancelled ASNs were silently dropped to [] by
    /// ResolveAsnAsync's bare catch). The cancellation contract — OCE always propagates, never
    /// swallowed (#114) — must hold here as it does everywhere else. ThrowsAnyAsync accepts the
    /// TaskCanceledException subclass that the gate.WaitAsync(ct) surfaces when the token is
    /// already cancelled.
    /// </summary>
    [Fact]
    public async Task GetPrefixesForAsns_CancelledToken_PropagatesOperationCanceledException()
    {
        var service = Service(new PerAsnHandler());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetPrefixesForAsns([100, 200, 300], cts.Token));
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

    /// <summary>
    /// #320: user sources get a fetch budget end-to-end. A server that answers headers instantly
    /// and then never sends the body used to hang the fetch (and the per-URL gate, and the whole
    /// route dump) forever. With the budget armed, the fetch fails within it and the negative
    /// cache throttles the retry — one HTTP attempt total.
    /// </summary>
    private sealed class HeadersThenHangHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var body = new HangingBodyStream();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(body)
            };
            response.Content.Headers.ContentLength = 1024; // plausible, under the size cap
            return Task.FromResult(response);
        }
    }

    /// <summary>Delivers nothing; honors the read token like a real socket stream would.</summary>
    private sealed class HangingBodyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 1024;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task UserSource_SlowBody_BoundedByBudget_NegativeCached()
    {
        var handler = new HeadersThenHangHandler();
        var httpProvider = new HttpPrefixProvider(new StubFactory(handler), NullLogger<HttpPrefixProvider>.Instance);
        var service = new PrefixService(
            new AppConfig(),
            null!, // RipeStatProvider is not on the user-source path
            null!, // IPrefixSourceService is not on the user-source path
            httpProvider,
            userSourceTimeoutSeconds: 1);

        // The fetch must fail within the budget (not hang) — the 8 s guard doubles as the red
        // guard: on budget-less code the call never completes.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetUserSourcePrefixesAsync("slow", "https://example.com/slow.txt", null)
                .WaitAsync(TimeSpan.FromSeconds(8)));

        // The negative cache throttles the retry: no second HTTP attempt.
        var second = await service.GetUserSourcePrefixesAsync("slow", "https://example.com/slow.txt", null);
        Assert.Empty(second);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>
    /// #324: a config source that hangs must not wedge sequential seeding — LoadAllAsync awaits
    /// sources one by one, so before the default budget one dripping source stalled every later
    /// source, WarmUp, and the final peer push until restart. With the provider-level budget the
    /// hung source fails on its own deadline and the GOOD source still loads. The outer WaitAsync
    /// guard doubles as the red guard on budget-less code.
    /// </summary>
    private sealed class PerUrlHandler : HttpMessageHandler
    {
        private readonly string _hangUrl;
        public PerUrlHandler(string hangUrl) => _hangUrl = hangUrl;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.ToString() == _hangUrl)
            {
                var hang = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HangingStream()) };
                hang.Content.Headers.ContentLength = 1024;
                return Task.FromResult(hang);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("10.2.0.0/24\n")
            });
        }
    }

    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 1024;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task LoadAllAsync_HangingFirstSource_DoesNotWedgeTheRest()
    {
        var handler = new PerUrlHandler("https://example.com/hang.txt");
        var httpProvider = new HttpPrefixProvider(
            new StubFactory(handler), NullLogger<HttpPrefixProvider>.Instance, defaultFetchTimeoutSeconds: 1);
        var yaml = "Bgp:\n  Asn: 65444\n  RouterId: 10.0.0.1\nPrefixSources:\n" +
                   "  - Name: hang\n    Kind: http\n    Url: https://example.com/hang.txt\n" +
                   "  - Name: good\n    Kind: http\n    Url: https://example.com/good.txt\n";
        var svc = new PrefixSourceService(
            ConfigLoader.LoadFromText(yaml),
            new PrefixSourceProviderFactory([httpProvider]),
            NullLogger<PrefixSourceService>.Instance);

        var all = await svc.LoadAllAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, all.Count);
        Assert.Empty(all[0].Prefixes);       // hung source: budget fired → failure → empty set
        Assert.Single(all[1].Prefixes);      // good source still loaded — seeding not wedged
    }
}
