using System.Net;
using BGPLite.Configuration;
using BGPLite.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests.Integration;

/// <summary>
/// The REAL provider chain end-to-end — RipeStatProvider (JSON parsing over a stubbed HTTP
/// transport) → RipeStatPrefixCache (TTL/negative caching) → PrefixService (fan-out, partial
/// failure, RU projection) — plus the real file pipeline (FilePrefixProvider through
/// PrefixSourceService) and the SSRF gate on the user-source fetch path. This is the layer the
/// scripted E2E stubs out; here the scripted behavior is replaced by controlled upstream
/// responses, pinning the #556/#611 contracts at the source.
/// </summary>
public sealed class ProvidersChainE2eTests
{
    private static readonly RipeStatConfig NoRetry = new() { TimeoutSeconds = 10, RetryAttempts = 0, RetryDelaySeconds = 0 };

    private static PrefixService BuildService(
        HttpMessageHandler handler,
        AppConfig? config = null,
        string? sourceFilePath = null,
        string defaultSourceName = "ru")
    {
        var appConfig = config ?? new AppConfig
        {
            Bgp = new BgpConfig { Asn = 65000, RouterId = "198.51.100.1" },
            RipeStat = NoRetry,
            DefaultPrefixSource = defaultSourceName,
            PrefixSources = sourceFilePath is null
                ? []
                : [new PrefixSourceConfig { Name = defaultSourceName, Kind = "file", Path = sourceFilePath }],
        };
        var sources = new PrefixSourceService(
            appConfig,
            new PrefixSourceProviderFactory([new FilePrefixProvider(NullLogger<FilePrefixProvider>.Instance)]),
            NullLogger<PrefixSourceService>.Instance,
            cacheTtl: TimeSpan.Zero);
        var provider = new RipeStatProvider(new StubFactory(handler), NullLogger<RipeStatProvider>.Instance, NoRetry);
        var cache = new RipeStatPrefixCache(provider, NullLogger<RipeStatPrefixCache>.Instance);
        return new PrefixService(
            appConfig, cache, sources,
            new HttpPrefixProvider(new StubFactory(handler), NullLogger<HttpPrefixProvider>.Instance),
            ruCacheTtl: TimeSpan.Zero,
            logger: NullLogger<PrefixService>.Instance);
    }

    private static string RipeStatJson(params string[] v4)
    {
        var list = string.Join(",", v4.Select(p => "\"" + p + "\""));
        return "{\"data\":{\"prefixes\":{\"v4\":{\"originating\":[" + list +
               "]}," + "\"v6\":{\"originating\":[\"2001:db8:1::/48\"]}}}}";
    }

    [Fact]
    public async Task RealRipeStatChain_ParsesCanonicalPrefixes_SkipsGarbage()
    {
        // The upstream returns what collectors saw. Canonical-policy outcomes: "10.0.0.1/8" is
        // host-bit MASKED to 10.0.0.0/8 (the canonical parser's contract), "0.0.0.0/0" is rejected
        // (no default-route leak), and null / non-string elements are skipped without dropping the
        // whole ASN fetch.
        var handler = new StaticHandler(HttpStatusCode.OK,
            "{\"data\":{\"prefixes\":{\"v4\":{\"originating\":[\"17.253.14.0/24\",\"10.0.0.1/8\",\"0.0.0.0/0\",null,42]},\"v6\":{\"originating\":[\"2001:db8:1::/48\"]}}}}");
        var service = BuildService(handler);

        var resolution = await service.GetPrefixesForAsnsResultAsync([714]);

        Assert.Empty(resolution.FailedAsns);
        Assert.Equal(
            new[] { "10.0.0.0/8", "17.253.14.0/24", "2001:db8:1::/48" }.OrderBy(p => p, StringComparer.Ordinal),
            resolution.Prefixes.Select(p => new BGPLite.Protocol.IpPrefix(p.Prefix, p.Length, p.IsIpv4).ToString())
                .OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public async Task FanOut_PartialFailure_ReportsFailedAsns()
    {
        // The #611 contract: one dead ASN must not vanish into an empty list — the surviving
        // prefixes carry through and the failed ASN is named.
        var handler = new RoutingHandler(url =>
            url.Contains("AS714")
                ? (HttpStatusCode.OK, RipeStatJson("17.253.14.0/24"))
                : (HttpStatusCode.InternalServerError, "{}"));
        var service = BuildService(handler);

        var resolution = await service.GetPrefixesForAsnsResultAsync([714, 65020]);

        Assert.Equal([65020], resolution.FailedAsns);
        Assert.Contains(resolution.Prefixes, p => new BGPLite.Protocol.IpPrefix(p.Prefix, p.Length, p.IsIpv4).ToString() == "17.253.14.0/24");
    }

    [Fact]
    public async Task FanOut_TotalFailure_ThrowsInsteadOfEmptyList()
    {
        // An empty result would read as "these ASNs announce nothing" and let the RU fallback
        // advertise the whole table — a total failure must surface as an exception.
        var handler = new StaticHandler(HttpStatusCode.InternalServerError, "{}");
        var service = BuildService(handler);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetPrefixesForAsnsResultAsync([714, 65020]));
        Assert.Contains("failed to resolve", failure.Message);
    }

    [Fact]
    public async Task AsnSource_SharesThePerAsnCache_WithPrefixService()
    {
        // Kind=asn sources and RipeStat.AsnLists consume ONE cache instance — AS714 is fetched once
        // no matter which mechanism asks (Program.cs wiring contract).
        var handler = new StaticHandler(HttpStatusCode.OK, RipeStatJson("17.253.14.0/24"));
        var config = new AppConfig
        {
            Bgp = new BgpConfig { Asn = 65000, RouterId = "198.51.100.1" },
            RipeStat = NoRetry,
            PrefixSources = [new PrefixSourceConfig { Name = "as714", Kind = "asn", Asn = 714 }],
        };
        var provider = new RipeStatProvider(new StubFactory(handler), NullLogger<RipeStatProvider>.Instance, NoRetry);
        var cache = new RipeStatPrefixCache(provider, NullLogger<RipeStatPrefixCache>.Instance);
        var sources = new PrefixSourceService(
            config,
            new PrefixSourceProviderFactory(
            [
                new FilePrefixProvider(NullLogger<FilePrefixProvider>.Instance),
                new AsnPrefixProvider(cache, NullLogger<AsnPrefixProvider>.Instance),
            ]),
            NullLogger<PrefixSourceService>.Instance,
            cacheTtl: TimeSpan.Zero);
        var service = new PrefixService(
            config, cache, sources,
            new HttpPrefixProvider(new StubFactory(handler), NullLogger<HttpPrefixProvider>.Instance),
            logger: NullLogger<PrefixService>.Instance);

        var viaSource = await service.GetSourcePrefixesAsync("as714");
        var viaAsn = await service.GetPrefixesAsync(714);

        // Both paths see the full catalog (v4 + v6 entry).
        Assert.Contains(viaSource, p => p.IsIpv4);
        Assert.Contains(viaAsn, p => p.IsIpv4);
        Assert.Equal(viaSource.Count, viaAsn.Count);
        Assert.Equal(1, handler.Calls); // one wire fetch shared by both paths
    }

    [Fact]
    public async Task RuProjection_ComesFromDefaultFileSource_AndFollowsContentChanges()
    {
        var path = TempFile("5.101.220.0/22");
        try
        {
            var service = BuildService(new StaticHandler(HttpStatusCode.OK, "{}"), sourceFilePath: path);

            var first = await service.GetRuPrefixesAsync();
            Assert.Equal(["5.101.220.0/22"], first.Select(p => new BGPLite.Protocol.IpPrefix(p.Prefix, p.Length, p.IsIpv4).ToString()));

            File.WriteAllText(path, "84.201.160.0/19\n");
            var second = await service.GetRuPrefixesAsync();
            Assert.Equal(["84.201.160.0/19"], second.Select(p => new BGPLite.Protocol.IpPrefix(p.Prefix, p.Length, p.IsIpv4).ToString()));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task UserSource_SsrfGate_BlocksLoopbackAtFetchTime()
    {
        // The save-time API gate is defense-in-depth; the authoritative layer is the fetch-time
        // ConnectCallback. A stored loopback URL (seeded directly, bypassing the API) must fail the
        // fetch — never resolve to a successful empty list.
        var handler = new StaticHandler(HttpStatusCode.OK, "ignored");
        var appConfig = new AppConfig { Bgp = new BgpConfig { Asn = 65000, RouterId = "198.51.100.1" }, RipeStat = NoRetry };
        var sources = new PrefixSourceService(
            appConfig,
            new PrefixSourceProviderFactory([new FilePrefixProvider(NullLogger<FilePrefixProvider>.Instance)]),
            NullLogger<PrefixSourceService>.Instance,
            cacheTtl: TimeSpan.Zero);
        var realHandler = new SocketsHttpHandler
        {
            ConnectCallback = PrefixSourceUrlValidator.CreateValidatedConnectionAsync,
            AllowAutoRedirect = false,
        };
        var userProvider = new HttpPrefixProvider(
            new StubFactory(realHandler), NullLogger<HttpPrefixProvider>.Instance, clientName: "e2e-user");
        var cache = new RipeStatPrefixCache(
            new RipeStatProvider(new StubFactory(handler), NullLogger<RipeStatProvider>.Instance, NoRetry),
            NullLogger<RipeStatPrefixCache>.Instance);
        var service = new PrefixService(
            appConfig, cache, sources,
            new HttpPrefixProvider(new StubFactory(handler), NullLogger<HttpPrefixProvider>.Instance),
            userSourceTimeoutSeconds: 3,
            logger: NullLogger<PrefixService>.Instance,
            userSourceHttpProvider: userProvider);

        var failure = await Assert.ThrowsAnyAsync<Exception>(
            () => service.GetUserSourcePrefixesAsync("feed", "http://127.0.0.1:80/list.txt", null));
        Assert.Contains("SSRF", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validator_SaveTimeGate_BlocksLoopbackAndOddPorts()
    {
        var (loopbackOk, _) = await PrefixSourceUrlValidator.ValidateUrlAsync("http://127.0.0.1/list.txt");
        Assert.False(loopbackOk);

        var (portOk, _) = await PrefixSourceUrlValidator.ValidateUrlAsync("http://192.0.2.9:8080/list.txt");
        Assert.False(portOk);
    }

    private static string TempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "bgplite-prov-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, content);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort cleanup */ }
    }

    private sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class RoutingHandler(Func<string, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, body) = respond(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
