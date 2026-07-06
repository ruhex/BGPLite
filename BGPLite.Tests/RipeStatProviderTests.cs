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
