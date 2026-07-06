using System.Collections.Concurrent;
using System.Net;
using BGPLite.Configuration;
using BGPLite.Providers;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

public class HttpPrefixProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public Uri? LastRequestUri { get; private set; }

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        private readonly string? _defaultUserAgent;
        public HttpClient? LastClient { get; private set; }
        public StubFactory(HttpMessageHandler handler, string? defaultUserAgent = null)
        {
            _handler = handler;
            _defaultUserAgent = defaultUserAgent;
        }
        public HttpClient CreateClient(string name)
        {
            LastClient = new HttpClient(_handler, disposeHandler: false);
            if (_defaultUserAgent is not null)
                LastClient.DefaultRequestHeaders.UserAgent.ParseAdd(_defaultUserAgent);
            return LastClient;
        }
    }

    /// <summary>Records the URL, Authorization, X-API-Key headers + the per-request state of every
    /// incoming request (thread-safe). Used to assert per-source headers land on the REQUEST message
    /// and never mutate the shared client's DefaultRequestHeaders (#155).</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public ConcurrentQueue<HttpRequestMessage> Seen { get; } = new();
        public string? LastUserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUserAgent = request.Headers.UserAgent.ToString();
            Seen.Enqueue(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("1.2.3.0/24\n")
            });
        }
    }

    private static HttpPrefixProvider Provider(StubHandler handler) =>
        new(new StubFactory(handler), NullLogger<HttpPrefixProvider>.Instance);

    private static PrefixSourceConfig HttpSource(string url) =>
        new() { Name = "t", Kind = "http", Url = url };

    [Fact]
    public async Task ParsesBody()
    {
        var provider = Provider(new StubHandler(HttpStatusCode.OK, "1.2.3.0/24\n5.6.0.0/16\n"));
        var result = await provider.LoadAsync(HttpSource("https://raw.githubusercontent.com/o/r/main/x.txt"));
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task FetchesArbitraryUrl()
    {
        // Any direct raw-file URL works, not only GitHub.
        var handler = new StubHandler(HttpStatusCode.OK, "10.0.0.0/8\n");
        var provider = Provider(handler);

        var result = await provider.LoadAsync(HttpSource("https://example.com/lists/ru.txt"));

        Assert.Single(result);
        Assert.Equal("https://example.com/lists/ru.txt", handler.LastRequestUri!.ToString());
    }

    [Fact]
    public async Task AppliesPerSourceTimeout_DoesNotMutateSharedClient()
    {
        // #155: per-source timeout must NOT mutate http.Timeout (the named client is pooled). A slow
        // source that times out must do so via a linked CTS, leaving the client's Timeout untouched
        // for the next caller.
        var factory = new StubFactory(new StubHandler(HttpStatusCode.OK, "1.2.3.0/24\n"));
        var provider = new HttpPrefixProvider(factory, NullLogger<HttpPrefixProvider>.Instance);

        await provider.LoadAsync(new PrefixSourceConfig { Name = "t", Kind = "http", Url = "https://example.com/x.txt", Timeout = 7 });

        Assert.NotNull(factory.LastClient);
        // The client's own Timeout is the default (100s) — NOT mutated to 7s by LoadAsync.
        Assert.Equal(TimeSpan.FromSeconds(100), factory.LastClient!.Timeout);
    }

    [Fact]
    public async Task AppliesPerSourceHeaders_OnRequestMessage()
    {
        // #155: per-source headers must land on the REQUEST message, never on
        // http.DefaultRequestHeaders (the named client is pooled — mutating it leaks credentials
        // across sources). RecordingHandler captures the actual request that reached the handler.
        var handler = new RecordingHandler();
        var provider = new HttpPrefixProvider(new StubFactory(handler), NullLogger<HttpPrefixProvider>.Instance);

        await provider.LoadAsync(new PrefixSourceConfig
        {
            Name = "t",
            Kind = "http",
            Url = "https://example.com/x.txt",
            Headers = new() { ["Authorization"] = "Bearer secret", ["X-API-Key"] = "k" }
        });

        var request = Assert.Single(handler.Seen);
        Assert.Equal("Bearer secret", request.Headers.GetValues("Authorization").First());
        Assert.Equal("k", request.Headers.GetValues("X-API-Key").First());
    }

    [Fact]
    public async Task PerSourceHeaders_DoNotLeakOntoNextRequest()
    {
        // #155 regression: source A's Authorization must NOT appear on source B's request when they
        // reuse the same named client. The prior code mutated DefaultRequestHeaders, so source A's
        // credentials bled onto source B.
        var handler = new RecordingHandler();
        var provider = new HttpPrefixProvider(new StubFactory(handler), NullLogger<HttpPrefixProvider>.Instance);

        var srcA = new PrefixSourceConfig { Name = "a", Kind = "http", Url = "https://a.example/x.txt", Headers = new() { ["Authorization"] = "Bearer AAA" } };
        var srcB = new PrefixSourceConfig { Name = "b", Kind = "http", Url = "https://b.example/x.txt" };

        await provider.LoadAsync(srcA);
        await provider.LoadAsync(srcB);

        var requests = handler.Seen.ToList();
        Assert.Equal(2, requests.Count);
        Assert.Equal("Bearer AAA", requests[0].Headers.GetValues("Authorization").First());
        // Source B must carry NO Authorization header — A's credentials did not leak.
        Assert.False(requests[1].Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task ParallelRequests_DoNotShareHeaders()
    {
        // CreateClient returns a fresh HttpClient per call, so per-source headers stay isolated
        // even when many sources are fetched concurrently through the singleton provider.
        var handler = new RecordingHandler();
        var provider = new HttpPrefixProvider(new StubFactory(handler), NullLogger<HttpPrefixProvider>.Instance);

        var srcA = new PrefixSourceConfig { Name = "a", Kind = "http", Url = "https://a.example/x.txt", Headers = new() { ["Authorization"] = "Bearer AAA" } };
        var srcB = new PrefixSourceConfig { Name = "b", Kind = "http", Url = "https://b.example/x.txt", Headers = new() { ["Authorization"] = "Bearer BBB" } };

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => provider.LoadAsync(i % 2 == 0 ? srcA : srcB)));

        var seen = handler.Seen.ToList();
        Assert.Equal(40, seen.Count);
        Assert.All(seen.Where(r => r.RequestUri!.ToString().Contains("a.example")), r => Assert.Equal("Bearer AAA", r.Headers.GetValues("Authorization").First()));
        Assert.All(seen.Where(r => r.RequestUri!.ToString().Contains("b.example")), r => Assert.Equal("Bearer BBB", r.Headers.GetValues("Authorization").First()));
    }

    [Fact]
    public async Task DefaultUserAgent_AppliedWhenNoOverride()
    {
        var handler = new RecordingHandler();
        var provider = new HttpPrefixProvider(new StubFactory(handler, defaultUserAgent: "BGPLite/1.0"), NullLogger<HttpPrefixProvider>.Instance);

        await provider.LoadAsync(new PrefixSourceConfig { Name = "t", Kind = "http", Url = "https://x.example/y.txt" });

        Assert.Equal("BGPLite/1.0", handler.LastUserAgent);
    }

    [Fact]
    public async Task PerSourceUserAgent_OverridesDefault()
    {
        var handler = new RecordingHandler();
        var provider = new HttpPrefixProvider(new StubFactory(handler, defaultUserAgent: "BGPLite/1.0"), NullLogger<HttpPrefixProvider>.Instance);

        await provider.LoadAsync(new PrefixSourceConfig
        {
            Name = "t",
            Kind = "http",
            Url = "https://x.example/y.txt",
            Headers = new() { ["User-Agent"] = "curl/8.0" }
        });

        Assert.Equal("curl/8.0", handler.LastUserAgent);
    }

    [Fact]
    public async Task HttpErrorThrows()
    {
        var provider = Provider(new StubHandler(HttpStatusCode.InternalServerError, ""));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.LoadAsync(HttpSource("https://raw.githubusercontent.com/o/r/main/x.txt")));
    }

    [Fact]
    public async Task MissingUrlThrowsInvalidOperation()
    {
        var provider = Provider(new StubHandler(HttpStatusCode.OK, ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.LoadAsync(new PrefixSourceConfig { Name = "t", Kind = "http", Url = null }));
    }
}
