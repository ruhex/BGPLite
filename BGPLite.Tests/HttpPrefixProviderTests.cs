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

    /// <summary>Records the URL + Authorization header of every incoming request (thread-safe).</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public ConcurrentQueue<(string Url, string? Auth)> Seen { get; } = new();
        public string? LastUserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var auth = request.Headers.Authorization is { } h ? $"{h.Scheme} {h.Parameter}" : null;
            LastUserAgent = request.Headers.UserAgent.ToString();
            Seen.Enqueue((request.RequestUri!.ToString(), auth));
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
    public async Task AppliesPerSourceTimeout()
    {
        var factory = new StubFactory(new StubHandler(HttpStatusCode.OK, "1.2.3.0/24\n"));
        var provider = new HttpPrefixProvider(factory, NullLogger<HttpPrefixProvider>.Instance);

        await provider.LoadAsync(new PrefixSourceConfig { Name = "t", Kind = "http", Url = "https://example.com/x.txt", Timeout = 7 });

        Assert.NotNull(factory.LastClient);
        Assert.Equal(TimeSpan.FromSeconds(7), factory.LastClient!.Timeout);
    }

    [Fact]
    public async Task AppliesPerSourceHeaders()
    {
        var factory = new StubFactory(new StubHandler(HttpStatusCode.OK, "1.2.3.0/24\n"));
        var provider = new HttpPrefixProvider(factory, NullLogger<HttpPrefixProvider>.Instance);

        await provider.LoadAsync(new PrefixSourceConfig
        {
            Name = "t",
            Kind = "http",
            Url = "https://example.com/x.txt",
            Headers = new() { ["Authorization"] = "Bearer secret", ["X-API-Key"] = "k" }
        });

        var headers = factory.LastClient!.DefaultRequestHeaders;
        Assert.Equal("Bearer secret", headers.GetValues("Authorization").First());
        Assert.Equal("k", headers.GetValues("X-API-Key").First());
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
        Assert.All(seen.Where(x => x.Url.Contains("a.example")), x => Assert.Equal("Bearer AAA", x.Auth));
        Assert.All(seen.Where(x => x.Url.Contains("b.example")), x => Assert.Equal("Bearer BBB", x.Auth));
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
