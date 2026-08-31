using System.Net;
using BGPLite.Configuration;
using BGPLite.Providers;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// Tests for <see cref="RipeStatProvider"/>. Since #104, retry/circuit-breaker is handled by the
/// Polly resilience handler on the named client (configured in Program.cs), NOT by the provider —
/// so these tests cover the provider's single-attempt behavior: parsing, error propagation, and
/// cancellation. The resilience pipeline itself is integration-tested by the live named-client
/// registration; HttpPrefixProviderTests analogously exercises the http client without the pipeline.
/// </summary>
public class RipeStatProviderTests
{
    private const string TwoPrefixBody =
        """
        {"status":"ok","data":{"resource":"65001","prefixes":{"v4":{"originating":["10.0.0.0/24","192.168.0.0/16"]},"v6":{"originating":[]}}}}
        """;

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public int Calls { get; private set; }

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static RipeStatProvider Provider(HttpMessageHandler handler) =>
        new(new StubFactory(handler), NullLogger<RipeStatProvider>.Instance, new RipeStatConfig());

    /// <summary>
    /// #358 review (hardens #319): a non-string element in the originating array (number/object)
    /// used to throw InvalidOperationException out of GetString() and discard the whole ASN's
    /// valid prefixes; it must be skipped like any other non-canonical row.
    /// </summary>
    [Fact]
    public async Task NonStringJsonElement_SkippedWithoutAborting()
    {
        const string body =
            """{"status":"ok","data":{"resource":"65001","prefixes":{"v4":{"originating":[42,{"bad":1},"10.0.0.0/24"]},"v6":{"originating":[]}}}}""";
        var handler = new StubHandler(HttpStatusCode.OK, body);

        var result = await Provider(handler).GetPrefixesAsync(65001);

        var row = Assert.Single(result);
        Assert.Equal(0x0A000000u, row.Prefix);
        Assert.Equal(24, row.PrefixLength);
    }

    [Fact]
    public async Task ParsesPrefixes()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TwoPrefixBody);
        var result = await Provider(handler).GetPrefixesAsync(65001);

        Assert.Equal(2, result.Count);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PropagatesTransient5xx_AsHttpRequestException()
    {
        // #104: with retry moved to the Polly pipeline on the named client, the provider performs a
        // single attempt and propagates the transient failure. The resilience pipeline (Program.cs)
        // is what retries — these unit tests cover the provider without the pipeline.
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable, "");

        await Assert.ThrowsAsync<HttpRequestException>(() => Provider(handler).GetPrefixesAsync(65001));
        Assert.Equal(1, handler.Calls); // single attempt — no in-provider retry
    }

    [Fact]
    public async Task PropagatesNonTransientStatus_AsHttpRequestException()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, "");

        await Assert.ThrowsAsync<HttpRequestException>(() => Provider(handler).GetPrefixesAsync(65001));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PropagatesCallerCancellation()
    {
        // A handler that honors the CancellationToken (as HttpClient's real handler does) — throws
        // OCE when the caller already cancelled. The provider propagates it (does not swallow).
        var handler = new CancelAwareHandler();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Provider(handler).GetPrefixesAsync(65001, cts.Token));
    }

    [Fact]
    public async Task GetPrefixesAsync_BuildsCorrectUrl_ForAsn()
    {
        // Pin the URL shape so a future refactor that changes the endpoint is caught.
        string? capturedUrl = null;
        var handler = new InterceptingHandler(TwoPrefixBody, url => capturedUrl = url);
        var provider = new RipeStatProvider(
            new StubFactory(handler), NullLogger<RipeStatProvider>.Instance, new RipeStatConfig());

        await provider.GetPrefixesAsync(64512);

        Assert.NotNull(capturedUrl);
        Assert.Contains("resource=AS64512", capturedUrl);
    }

    private sealed class InterceptingHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly Action<string?> _onUrl;
        public InterceptingHandler(string body, Action<string?> onUrl) { _body = body; _onUrl = onUrl; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _onUrl(request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) });
        }
    }


    /// <summary>
    /// #319: RIPEstat returns what third parties ANNOUNCED — non-canonical NLRI must go through
    /// the canonical parser (#236): host bits masked, /0 rejected, length range enforced, garbage
    /// skipped instead of throwing the whole fetch. Pre-fix, "10.0.0.1/8" landed unmasked under a
    /// corrupt route-table key and "0.0.0.0/0" was a default-route leak.
    /// </summary>
    [Fact]
    public async Task NonCanonicalPrefixes_MaskedOrSkipped()
    {
        const string body =
            """{"status":"ok","data":{"resource":"65001","prefixes":{"v4":{"originating":["10.0.0.1/8","0.0.0.0/0","1.2.3.4/33","not-a-cidr","192.168.0.0/16"]},"v6":{"originating":[]}}}}""";
        var handler = new StubHandler(HttpStatusCode.OK, body);
        var result = await Provider(handler).GetPrefixesAsync(65001);

        Assert.Contains((0x0A000000u, (byte)8), result);      // 10.0.0.1/8 masked to the network
        Assert.DoesNotContain((0x0A000001u, (byte)8), result); // unmasked key never stored
        Assert.Contains((0xC0A80000u, (byte)16), result);      // canonical row unaffected
        Assert.DoesNotContain(result, p => p.PrefixLength is 0 or > 32); // /0 and /33 rejected
        Assert.Equal(2, result.Count);                          // garbage skipped, not thrown
    }

    /// <summary>A handler that honors the CancellationToken like HttpClient's real handler does.</summary>
    private sealed class CancelAwareHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
