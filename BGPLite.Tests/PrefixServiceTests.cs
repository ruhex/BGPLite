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
        private readonly HashSet<uint> _failures;
        private int _calls;
        public int Calls => _calls;

        public PerAsnHandler(params uint[] failures) => _failures = [.. failures];

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

    private static PrefixService Service(PerAsnHandler handler) =>
        new(new AppConfig(),
            new RipeStatProvider(new StubFactory(handler),
                NullLogger<RipeStatProvider>.Instance,
                new RipeStatConfig { RetryAttempts = 2, RetryDelaySeconds = 0 }),
            null!, // IPrefixSourceService is not on the GetPrefixesForAsns path
            cacheTtl: TimeSpan.FromHours(1));

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
}
