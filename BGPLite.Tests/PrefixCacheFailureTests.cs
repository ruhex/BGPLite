using System.Net.Http;
using BGPLite.Configuration;
using BGPLite.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using BGPLite.Protocol;

namespace BGPLite.Tests;

/// <summary>
/// Regression coverage for #417: a failed load of the default prefix source used to reach the RU
/// cache as a "successful" empty result — either directly (the old GetDefaultAsync swallowed
/// exceptions) or, after #416's propagation, via the 30s NEGATIVE backoff entry that a retry
/// within the window hits. In both shapes the empty list was cached POSITIVELY for the full RU
/// TTL (1h), so a single transient outage dropped every unconfigured peer's routes for up to an
/// hour. The failure must surface as a failure on every call inside the backoff window; recovery
/// happens on the first real load after the backoff expires.
/// </summary>
public class PrefixCacheFailureTests
{
    private sealed class SwitchableProvider : IPrefixSourceProvider
    {
        public string Kind => "stub";
        public bool SupportsConditionalRequests => true;
        public int Calls { get; private set; }
        public bool FailNext { get; set; }

        public Task<SourceLoadResult> LoadAsync(PrefixSourceConfig source, string? etag = null, DateTimeOffset? lastModified = null, CancellationToken ct = default)
        {
            Calls++;
            if (FailNext) throw new InvalidOperationException("simulated source outage");
            return Task.FromResult(SourceLoadResult.Ok(new List<IpPrefix> { new(0x0A000000u, 8) }));
        }
    }

    private static (PrefixService Service, SwitchableProvider Provider, FakeTimeProvider Time) Build(TimeSpan? ruTtl = null)
    {
        var yaml = "Bgp:\n  Asn: 65444\n  RouterId: 10.0.0.1\n" +
                   "PrefixSources:\n  - Name: ru\n    Kind: stub\nDefaultPrefixSource: ru\n";
        var config = ConfigLoader.LoadFromText(yaml);
        var provider = new SwitchableProvider();
        var time = new FakeTimeProvider();
        var sources = new PrefixSourceService(
            config,
            new PrefixSourceProviderFactory([provider]),
            NullLogger<PrefixSourceService>.Instance,
            timeProvider: time);
        var service = new PrefixService(
            config,
            null!, // RipeStatPrefixCache — not on the GetRuPrefixesAsync path
            sources,
            null!, // HttpPrefixProvider — not on the GetRuPrefixesAsync path
            ruCacheTtl: ruTtl ?? TimeSpan.FromHours(1),
            logger: NullLogger<PrefixService>.Instance,
            timeProvider: time);
        return (service, provider, time);
    }

    [Fact]
    public async Task GetRuPrefixesAsync_FailedColdLoad_DoesNotPoisonTheCache()
    {
        var (service, provider, time) = Build();

        // 1. Cold failure propagates — nothing stale to serve.
        provider.FailNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetRuPrefixesAsync());

        // 2/3. Retries inside the 30s negative-backoff window surface as failures too — they must
        // NOT arrive as a successful empty list (pre-#417 they did, and the empty set was cached
        // positively for the full RU TTL).
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetRuPrefixesAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetRuPrefixesAsync());

        // 4. Recovery: past the backoff window the next call performs a REAL fetch and serves the
        // actual content — never a cached empty set.
        provider.FailNext = false;
        time.Advance(TimeSpan.FromSeconds(31));
        var routes = await service.GetRuPrefixesAsync();
        Assert.Single(routes);
        Assert.Equal(2, provider.Calls); // exactly two real fetches happened (fail + recover)
    }

    [Fact]
    public async Task GetRuPrefixesAsync_FailureWithStaleCopy_ServesStale_NotEmpty()
    {
        var (service, provider, time) = Build();

        // A good copy first.
        var first = await service.GetRuPrefixesAsync();
        Assert.Single(first);

        // The source dies; after the RU TTL the re-fetch fails — the stale copy is served
        // (stale-on-failure, #163 parity), never an empty set (#417).
        provider.FailNext = true;
        time.Advance(TimeSpan.FromHours(2));
        var stale = await service.GetRuPrefixesAsync();
        Assert.Equal(first, stale);

        // And after another full RU TTL the failure path repeats — still stale, still never empty.
        time.Advance(TimeSpan.FromHours(2));
        var staleAgain = await service.GetRuPrefixesAsync();
        Assert.Equal(first, staleAgain);
    }

    [Fact]
    public async Task LoadDefaultAsync_LegitimatelyEmptySource_IsNotBackoff()
    {
        // A source that SUCCEEDS with zero prefixes is real content ("the operator emptied the
        // list"), not failure backoff — it must pass through and cache normally, not throw.
        var yaml = "Bgp:\n  Asn: 65444\n  RouterId: 10.0.0.1\n" +
                   "PrefixSources:\n  - Name: ru\n    Kind: stub\nDefaultPrefixSource: ru\n";
        var config = ConfigLoader.LoadFromText(yaml);
        var time = new FakeTimeProvider();
        var provider = new EmptyOkProvider();
        var sources = new PrefixSourceService(
            config, new PrefixSourceProviderFactory([provider]),
            NullLogger<PrefixSourceService>.Instance, timeProvider: time);
        var service = new PrefixService(
            config, null!, sources, null!,
            logger: NullLogger<PrefixService>.Instance, timeProvider: time);

        var routes = await service.GetRuPrefixesAsync();
        Assert.Empty(routes); // legitimately empty — cached as such, no exception

        // Reload past the RU TTL: the empty CONTENT entry is non-negative, so the reload is a
        // real (successful-empty) load — still no throw, never misread as failure backoff.
        time.Advance(TimeSpan.FromHours(2));
        routes = await service.GetRuPrefixesAsync();
        Assert.Empty(routes);
    }

    private sealed class EmptyOkProvider : IPrefixSourceProvider
    {
        public string Kind => "stub";
        public bool SupportsConditionalRequests => true;
        public Task<SourceLoadResult> LoadAsync(PrefixSourceConfig source, string? etag = null, DateTimeOffset? lastModified = null, CancellationToken ct = default)
            => Task.FromResult(SourceLoadResult.Ok(new List<IpPrefix>()));
    }

    /// <summary>Succeeds normally; throws the CALLER's OCE once the token is cancelled — how
    /// RipeStatProvider surfaces host shutdown to the cache (#485).</summary>
    private sealed class OceWhenCancelledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => ct.IsCancellationRequested
                ? Task.FromException<HttpResponseMessage>(new OperationCanceledException(ct))
                : Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"status":"ok","data":{"resource":"65010","prefixes":{"v4":{"originating":["10.1.10.1/32"]},"v6":{"originating":[]}}}}""")
                });
    }

    private sealed class HttpClientFactoryStub(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task WarmUp_CancelledToken_UnwindsTheLoop_NotAWarnPerAsn()
    {
        // #485: the per-ASN catch (Exception) swallowed the shutdown OCE once per remaining ASN —
        // a "WarmUp failed" WARN storm on every stop. Caller cancellation must unwind the loop.
        var yaml = "Bgp:\n  Asn: 65444\n  RouterId: 10.0.0.1\n" +
                   "RipeStat:\n  AsnLists:\n    - Name: ru\n      Asns: [65010, 65011]\n";
        var config = ConfigLoader.LoadFromText(yaml);
        var cache = new RipeStatPrefixCache(new RipeStatProvider(
            new HttpClientFactoryStub(new OceWhenCancelledHandler()),
            NullLogger<RipeStatProvider>.Instance,
            new RipeStatConfig { RetryAttempts = 0, RetryDelaySeconds = 0 }));
        var sources = new PrefixSourceService(
            config,
            new PrefixSourceProviderFactory([]),
            NullLogger<PrefixSourceService>.Instance);
        var service = new PrefixService(config, cache, sources, null!);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // RED pre-fix: both per-ASN OCEs were swallowed (a WARN each) and WarmUp completed normally.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.WarmUpAsync(cts.Token));
    }
}
