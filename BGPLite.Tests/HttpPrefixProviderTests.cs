using System.Collections.Concurrent;
using System.Net;
using BGPLite.Configuration;
using BGPLite.Providers;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
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

        public StubHandler() : this(HttpStatusCode.OK, "10.0.0.0/8\n") { }

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
        public string LastName { get; private set; } = "";
        public StubFactory(HttpMessageHandler handler, string? defaultUserAgent = null)
        {
            _handler = handler;
            _defaultUserAgent = defaultUserAgent;
        }
        public HttpClient CreateClient(string name)
        {
            LastName = name;
            LastClient = new HttpClient(_handler, disposeHandler: false);
            if (_defaultUserAgent is not null)
                LastClient.DefaultRequestHeaders.UserAgent.ParseAdd(_defaultUserAgent);
            return LastClient;
        }
    }

    /// <summary>Records the URL, Authorization, X-API-Key headers + the per-request state of every
    /// incoming request (thread-safe). Used to assert per-source headers land on the REQUEST message
    /// and never mutate the shared client's DefaultRequestHeaders.</summary>
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

    private static PrefixSourceConfig HttpSource(string url, string name = "t") =>
        new() { Name = name, Kind = "http", Url = url };

    [Fact]
    public async Task ParsesBody()
    {
        var provider = Provider(new StubHandler(HttpStatusCode.OK, "1.2.3.0/24\n5.6.0.0/16\n"));
        var result = await provider.LoadAsync(HttpSource("https://raw.githubusercontent.com/o/r/main/x.txt"));
        Assert.Equal(2, result.Prefixes.Count);
    }

    [Fact]
    public async Task LoadAsync_UsesTheClientNameFromTheCtor()
    {
        // The user-source instance must draw its HttpClient from the retry-only named
        // pipeline, never from the shared breaker pipeline the operator sources use.
        var factory = new StubFactory(new StubHandler());
        var provider = new HttpPrefixProvider(
            factory, NullLogger<HttpPrefixProvider>.Instance, clientName: HttpPrefixProvider.UserSourceClientName);

        await provider.LoadAsync(HttpSource("https://example.com/u.txt"));

        Assert.Equal(HttpPrefixProvider.UserSourceClientName, factory.LastName);
    }

    [Fact]
    public async Task LoadAsync_DefaultsToTheOperatorClientName()
    {
        var factory = new StubFactory(new StubHandler());
        var provider = new HttpPrefixProvider(factory, NullLogger<HttpPrefixProvider>.Instance);

        await provider.LoadAsync(HttpSource("https://example.com/o.txt"));

        Assert.Equal(HttpPrefixProvider.ClientName, factory.LastName);
    }

    [Fact]
    public async Task FetchesArbitraryUrl()
    {
        // Any direct raw-file URL works, not only GitHub.
        var handler = new StubHandler(HttpStatusCode.OK, "10.0.0.0/8\n");
        var provider = Provider(handler);

        var result = await provider.LoadAsync(HttpSource("https://example.com/lists/ru.txt"));

        Assert.Single(result.Prefixes);
        Assert.Equal("https://example.com/lists/ru.txt", handler.LastRequestUri!.ToString());
    }

    [Fact]
    public async Task AppliesPerSourceTimeout_DoesNotMutateSharedClient()
    {
        // Per-source timeout must NOT mutate http.Timeout (the named client is pooled). A slow
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
        // Per-source headers must land on the REQUEST message, never on
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
        // Source A's Authorization must NOT appear on source B's request when they
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

    /// <summary>
    /// A source with NO configured Timeout still gets a budget — the default is armed, so a
    /// server that answers headers and then never sends the body fails by T+default instead of
    /// hanging the fetch (and seeding/auto-refresh behind it). The outer WaitAsync guard also
    /// bounds the test: without the default the load never completes.
    /// </summary>
    private sealed class HeadersThenHangHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new HangingBodyStream())
            };
            response.Content.Headers.ContentLength = 1024;
            return Task.FromResult(response);
        }
    }

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
    public async Task NoTimeoutConfigured_DefaultBudgetBoundsTheFetch()
    {
        var handler = new HeadersThenHangHandler();
        var provider = new HttpPrefixProvider(
            new StubFactory(handler), NullLogger<HttpPrefixProvider>.Instance,
            defaultFetchTimeoutSeconds: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.LoadAsync(HttpSource("https://example.com/slow.txt"))
                .WaitAsync(TimeSpan.FromSeconds(8)));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ConfiguredTimeout_WinsOverDefault()
    {
        // The configured value overrides the default (both arm the same mechanism); a small
        // configured budget on the same hanging server fails FAST — proving the configured path
        // still arms and is not clipped by anything.
        var handler = new HeadersThenHangHandler();
        var provider = new HttpPrefixProvider(
            new StubFactory(handler), NullLogger<HttpPrefixProvider>.Instance,
            defaultFetchTimeoutSeconds: 30);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.LoadAsync(new PrefixSourceConfig { Name = "t", Kind = "http", Url = "https://example.com/slow.txt", Timeout = 1 })
                .WaitAsync(TimeSpan.FromSeconds(8)));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"configured 1s budget fired in {sw.Elapsed}");
    }

    [Fact]
    public async Task HttpErrorThrows()
    {
        var provider = Provider(new StubHandler(HttpStatusCode.InternalServerError, ""));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.LoadAsync(HttpSource("https://raw.githubusercontent.com/o/r/main/x.txt")));
    }

    /// <summary>Answers through a callback and records what each request carried when it arrived.</summary>
    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Uri, string? ApiKey, string? IfNoneMatch, string? IfModifiedSince)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues("X-API-Key", out var key) ? key.Single() : null,
                request.Headers.IfNoneMatch.Count > 0 ? request.Headers.IfNoneMatch.ToString() : null,
                request.Headers.IfModifiedSince?.ToString()));
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Redirect(HttpStatusCode status, Uri? location)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("") };
        response.Headers.Location = location;
        return response;
    }

    private static HttpPrefixProvider Provider(HttpMessageHandler handler) =>
        new(new StubFactory(handler), NullLogger<HttpPrefixProvider>.Instance);

    private static readonly Uri MovedTo = new("https://cdn.example.com/lists/x.txt");

    [Theory]
    [InlineData(HttpStatusCode.Found)]             // 302 — the classic redirect
    [InlineData(HttpStatusCode.MultipleChoices)]   // 300 — the boundary of the 3xx class
    public async Task Redirect_WithoutLocation_IsRejected_WithClearError(HttpStatusCode status)
    {
        var provider = Provider(new StubHandler(status, ""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.LoadAsync(HttpSource("https://example.com/moved.txt", "moved-list")));

        Assert.Contains("without a Location header", ex.Message);
        Assert.Contains("'moved-list'", ex.Message);      // names the source…
        Assert.DoesNotContain("example.com", ex.Message);  // …never its URL
    }

    [Fact]
    public async Task Redirect_IsFollowed_ToTheLocationTarget()
    {
        // GitHub release assets and moved lists answer 302 to the final URL (v1.5.0 followed them).
        var handler = new ScriptedHandler(r => r.RequestUri!.Host == "example.com"
            ? Redirect(HttpStatusCode.Found, MovedTo)
            : Ok("1.2.3.0/24\n"));

        var result = await Provider(handler).LoadAsync(HttpSource("https://example.com/moved.txt"));

        Assert.Single(result.Prefixes);
        Assert.Equal(
            ["https://example.com/moved.txt", "https://cdn.example.com/lists/x.txt"],
            handler.Seen.Select(s => s.Uri).ToArray());
    }

    [Fact]
    public async Task Redirect_NextHopCarriesNoPerSourceHeadersAndNoValidators()
    {
        // The handler would re-send X-API-Key to the redirect target, and the conditional
        // validators belong to the configured URL's representation. Each hop is a plain GET.
        var handler = new ScriptedHandler(r => r.RequestUri!.Host == "example.com"
            ? Redirect(HttpStatusCode.Found, MovedTo)
            : Ok("1.2.3.0/24\n"));
        var source = new PrefixSourceConfig
        {
            Name = "t",
            Kind = "http",
            Url = "https://example.com/moved.txt",
            Headers = new Dictionary<string, string> { ["X-API-Key"] = "secret-1" }
        };

        await Provider(handler).LoadAsync(source, etag: "\"abc\"", lastModified: DateTimeOffset.UnixEpoch);

        Assert.Equal("secret-1", handler.Seen[0].ApiKey);
        Assert.NotNull(handler.Seen[0].IfNoneMatch);
        Assert.NotNull(handler.Seen[0].IfModifiedSince);
        Assert.Null(handler.Seen[1].ApiKey);
        Assert.Null(handler.Seen[1].IfNoneMatch);
        Assert.Null(handler.Seen[1].IfModifiedSince);
    }

    [Fact]
    public async Task Redirect_RelativeLocation_IsResolvedAgainstTheCurrentUri()
    {
        var handler = new ScriptedHandler(r => r.RequestUri!.AbsolutePath == "/a/b.txt"
            ? Redirect(HttpStatusCode.MovedPermanently, new Uri("/lists/x.txt", UriKind.Relative))
            : Ok("1.2.3.0/24\n"));

        await Provider(handler).LoadAsync(HttpSource("https://example.com/a/b.txt"));

        Assert.Equal("https://example.com/lists/x.txt", handler.Seen[1].Uri);
    }

    [Fact]
    public async Task Redirect_LoopStopsAfterMaxRedirects()
    {
        var handler = new ScriptedHandler(_ => Redirect(HttpStatusCode.Found, MovedTo));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider(handler).LoadAsync(HttpSource("https://example.com/moved.txt")));

        Assert.Contains($"more than {HttpPrefixProvider.MaxRedirects} redirects", ex.Message);
        Assert.Equal(HttpPrefixProvider.MaxRedirects + 1, handler.Seen.Count);   // the original request + MaxRedirects hops
    }

    [Theory]
    [InlineData("https://example.com/x", "file:///etc/passwd", "unsupported scheme")]
    [InlineData("https://example.com/x", "ftp://cdn.example.com/x", "unsupported scheme")]
    [InlineData("https://example.com/x", "http://cdn.example.com/x", "from https to http")]
    [InlineData("https://example.com/x", "https://user:pass@cdn.example.com/x", "embedded credentials")]
    [InlineData("https://example.com/x", "https://cdn.example.com:9000/x", null)]   // the port is the connect-time gate's call
    [InlineData("https://example.com/x", "https://user@cdn.example.com/x", "embedded credentials")]
    [InlineData("https://user:pass@example.com/x", "https://user:pass@cdn.example.com/y", "embedded credentials")]  // the configured credentials on another host
    [InlineData("https://user:pass@example.com/x", "https://other:pass@example.com/y", "embedded credentials")]    // other credentials on the same host
    public void RedirectTarget_RefusesWhatMustNotBeRequested(string current, string location, string? error)
    {
        var source = HttpSource(current);
        if (error is null)
        {
            // Not this method's decision: a non-standard port passes here and is blocked by
            // PrefixSourceUrlValidator at connect time (IsAllowedPort).
            Assert.Equal(new Uri(location), HttpPrefixProvider.RedirectTarget(source, new Uri(current), HttpStatusCode.Found, new Uri(location)));
            return;
        }
        var ex = Assert.Throws<InvalidOperationException>(() =>
            HttpPrefixProvider.RedirectTarget(source, new Uri(current), HttpStatusCode.Found, new Uri(location)));
        Assert.Contains(error, ex.Message);
    }

    [Fact]
    public void RedirectTarget_HttpToHttps_IsFollowed()
    {
        // An upgrade is fine; only the downgrade (https → http) is refused.
        var source = HttpSource("http://example.com/x");

        var upgraded = HttpPrefixProvider.RedirectTarget(source, new Uri("http://example.com/x"), HttpStatusCode.Found, new Uri("https://example.com/x"));

        Assert.Equal("https://example.com/x", upgraded.ToString());
    }

    /// <summary>
    /// The credentials of a configured URL are sent to its host on every request; a relative
    /// redirect stays on that host, so refusing the target's userinfo there would fail a source
    /// that v1.5.0 (auto-redirect) loaded.
    /// </summary>
    [Theory]
    [InlineData("https://user:pass@example.com/x", "/y", "https://user:pass@example.com/y")]
    [InlineData("https://user:pass@example.com/x", "https://example.com/y", "https://example.com/y")]  // target without credentials
    public void RedirectTarget_ConfiguredCredentials_ContinueOnTheirOwnHost(string current, string location, string expected)
    {
        var target = HttpPrefixProvider.RedirectTarget(HttpSource(current), new Uri(current), HttpStatusCode.Found, new Uri(location, UriKind.RelativeOrAbsolute));

        Assert.Equal(new Uri(expected), target);
    }

    [Fact]
    public async Task Redirect_FromAUrlWithCredentials_IsFollowed()
    {
        var handler = new ScriptedHandler(r => r.RequestUri!.AbsolutePath == "/x"
            ? Redirect(HttpStatusCode.Found, new Uri("/y", UriKind.Relative))
            : Ok("1.2.3.0/24\n"));

        var result = await Provider(handler).LoadAsync(HttpSource("https://user:pass@example.com/x"));

        Assert.Single(result.Prefixes);
        Assert.Equal(["https://user:pass@example.com/x", "https://user:pass@example.com/y"], handler.Seen.Select(s => s.Uri).ToArray());
    }

    /// <summary>
    /// A user-source name is peer-supplied and only length-validated at save time, and it reaches
    /// this provider's log lines and error texts — which the user-source cache logs with the
    /// exception. A line break in the name forged log lines.
    /// </summary>
    [Fact]
    public async Task SourceName_WithControlCharacters_IsSanitizedInLogsAndErrors()
    {
        var logger = new RecordingLogger();
        var source = HttpSource("https://example.com/x", "feed\nfail: BGPLite[0]\r\nforged");
        const string clean = "feed fail: BGPLite[0]  forged";   // every control character becomes a space

        await new HttpPrefixProvider(new StubFactory(new StubHandler(HttpStatusCode.OK, "1.2.3.0/24\n")), logger)
            .LoadAsync(source);
        var line = Assert.Single(logger.Messages);
        Assert.Contains(clean, line);
        Assert.DoesNotContain("\n", line);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new HttpPrefixProvider(new StubFactory(new StubHandler(HttpStatusCode.Found, "")), logger)
                .LoadAsync(source));
        Assert.Contains(clean, ex.Message);
        Assert.DoesNotContain("\n", ex.Message);
    }

    /// <summary>Records every formatted log message.</summary>
    private sealed class RecordingLogger : ILogger<HttpPrefixProvider>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task MissingUrlThrowsInvalidOperation()
    {
        var provider = Provider(new StubHandler(HttpStatusCode.OK, ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.LoadAsync(new PrefixSourceConfig { Name = "t", Kind = "http", Url = null }));
    }
}
