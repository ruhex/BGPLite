using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BGPLite.Configuration;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace BGPLite.Providers;

/// <summary>
/// Loads prefixes from a remote CIDR list over HTTP/HTTPS (Kind = <c>"http"</c>). Any direct
/// raw-file URL works — raw.githubusercontent.com, a gist, a pastebin, a self-hosted list, etc.
/// Redirects are followed by the provider itself, hop by hop (see <see cref="RedirectTarget"/>).
/// Uses <see cref="IHttpClientFactory"/> so the handler pool is recycled
/// by the factory (the provider is stateless and safe to hold as a singleton).
/// <para>SSRF defense: the named-client's <c>SocketsHttpHandler.ConnectCallback</c> validates
/// every connection's DNS resolution at the socket level — no TOCTOU race, and a redirect cannot
/// bypass it: a hop to another origin needs a new connection, which re-enters the callback (a
/// same-origin hop reuses the pooled connection that already passed it).
/// Response body is capped at <see cref="MaxResponseBytes"/> to prevent OOM.</para>
/// <para>Conditional requests: when <paramref name="etag"/> / <paramref name="lastModified"/>
/// are provided from a prior load, sends <c>If-None-Match</c> / <c>If-Modified-Since</c>. A 304
/// response skips the body download entirely (~1 KB headers-only round-trip). Validators go on the
/// first hop only, so a source that redirects on every load is downloaded in full each time.</para>
/// </summary>
public sealed class HttpPrefixProvider(
    IHttpClientFactory httpFactory,
    ILogger<HttpPrefixProvider> logger,
    int defaultFetchTimeoutSeconds = 30, // mirrors DefaultFetchTimeoutSeconds (a ctor default cannot reference the member const)
    string? clientName = null) // selects the resilience pipeline — see UserSourceClientName
    : IPrefixSourceProvider
{
    public const string ClientName = "http";

    /// <summary>
    /// The peer-supplied user-source path uses this named client — a retry WITHOUT a circuit
    /// breaker. The shared breaker coupled peer-controlled failure rates to operator sources: a few
    /// blackholed user URLs opened the shared breaker and suppressed every operator source fetch
    /// for 30s windows. SSRF handler and body cap are identical for both pipelines.
    /// </summary>
    public const string UserSourceClientName = "http-user-source";

    /// <summary>Maximum response body size (10 MB) — defends against OOM from huge/malicious files.</summary>
    internal const int MaxResponseBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Budget applied when the source does not configure one. Without it, a YAML-configured
    /// PrefixSource omitting <c>Timeout</c> armed no deadline at all — the named client is
    /// InfiniteTimeSpan (Polly) and the pipeline's timeout only wrapped SendAsync up to the
    /// headers, so a headers-then-drip body hung seeding and auto-refresh indefinitely (size is
    /// bounded by <see cref="MaxResponseBytes"/>, time was not). Injectable for tests via the ctor.
    /// </summary>
    public const int DefaultFetchTimeoutSeconds = 30;

    public string Kind => "http";

    /// <summary>HTTP conditional requests (If-None-Match / If-Modified-Since → 304) are supported.</summary>
    public bool SupportsConditionalRequests => true;

    public async Task<SourceLoadResult> LoadAsync(
        PrefixSourceConfig source,
        string? etag = null,
        DateTimeOffset? lastModified = null,
        CancellationToken ct = default)
    {
        // User-source names are peer-supplied and only length-validated at save time — sanitize
        // once, for every log line and error text below.
        var sourceName = LogSanitizer.SanitizeForLog(source.Name);
        if (string.IsNullOrWhiteSpace(source.Url))
            throw new InvalidOperationException($"Prefix source '{sourceName}': Kind=http requires a Url.");

        var http = httpFactory.CreateClient(clientName ?? ClientName);

        // Per-source timeout: link the caller's token with a CancelAfter so a slow source can't pin
        // the fetch past its budget — always armed: a configured Timeout wins, otherwise
        // the default budget covers config sources that omit it (user sources pass it
        // explicitly). The linked token covers headers AND the body loop below.
        // We do NOT mutate http.Timeout: the named client is pooled by
        // IHttpClientFactory, and mutating it leaks the per-source timeout onto the next caller.
        var seconds = source.Timeout is int configured && configured > 0 ? configured : defaultFetchTimeoutSeconds;
        CancellationTokenSource? timeoutCts = null;
        CancellationToken linkedToken;
        if (seconds > 0)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(seconds));
            linkedToken = timeoutCts.Token;
        }
        else
        {
            linkedToken = ct;
        }

        // Per-source headers go on the REQUEST message, never on http.DefaultRequestHeaders:
        // the named client is pooled and shared, so mutating its default headers leaks source A's
        // Authorization / X-API-Key onto source B's next request. Per-message headers merge with the
        // client's configured defaults (User-Agent: BGPLite/1.0) and override per-source.
        var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        if (source.Headers is { Count: > 0 } headers)
            foreach (var (key, value) in headers)
                if (!request.Headers.TryAddWithoutValidation(key, value))
                    logger.LogWarning("Source '{Name}': could not add request header '{Header}'.", sourceName, key);

        // Conditional request — send validators from the prior load. GitHub raw, CDNs, and
        // most HTTP servers support ETag / Last-Modified and reply 304 Not Modified (no body) when
        // the content hasn't changed, making the periodic check ~1 KB instead of a full re-download.
        if (etag is not null)
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        if (lastModified is not null)
            request.Headers.IfModifiedSince = lastModified;

        try
        {
            // Stream-read with size cap: ResponseHeadersRead gets headers first (fast Content-Length
            // check), then stream the body with a hard cap to prevent OOM. SSRF validation is at the
            // handler level (SocketsHttpHandler.ConnectCallback in Program.cs) — no pre-resolve here.
            for (var hop = 0; ; hop++)
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedToken);

                // 304 Not Modified — server confirms data unchanged. No body to parse. Extract any
                // updated validators the server may have returned (some servers refresh ETag on 304).
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    logger.LogDebug("Source '{Name}' (http): 304 Not Modified", sourceName);
                    var updatedEtag = response.Headers.ETag?.Tag;
                    var updatedLm = response.Content.Headers.LastModified;
                    return SourceLoadResult.NotModifiedResult(updatedEtag ?? etag, updatedLm ?? lastModified);
                }

                if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
                {
                    // Redirects are followed here, not by the handler — see RedirectTarget. The next
                    // hop is a plain GET; the validators above belong to the configured URL and are
                    // not repeated.
                    if (hop == MaxRedirects)
                        throw new InvalidOperationException(
                            $"Prefix source '{sourceName}': more than {MaxRedirects} redirects — giving up.");
                    var target = RedirectTarget(source, request.RequestUri!, response.StatusCode, response.Headers.Location);
                    request.Dispose();
                    request = new HttpRequestMessage(HttpMethod.Get, target);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                // Extract validators from the 200 response for next time.
                var newEtag = response.Headers.ETag?.Tag;
                var newLm = response.Content.Headers.LastModified;

                if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaxResponseBytes)
                    throw new InvalidOperationException(
                        $"Prefix source '{sourceName}': response too large ({contentLength} bytes, max {MaxResponseBytes}).");

                using var stream = await response.Content.ReadAsStreamAsync(linkedToken);
                using var ms = new MemoryStream();
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, linkedToken)) > 0)
                {
                    ms.Write(buffer, 0, read);
                    if (ms.Length > MaxResponseBytes)
                        throw new InvalidOperationException(
                            $"Prefix source '{sourceName}': response exceeded {MaxResponseBytes} bytes during stream.");
                }

                var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                var prefixes = PrefixListParser.Parse(text);
                // Log only the source Name (the operator/peer-supplied identifier), never the URL — peer URLs
                // may carry tokens in the query string that must not reach application logs.
                logger.LogInformation("Source '{Name}' (http): loaded {Count} prefixes", sourceName, prefixes.Count);
                return SourceLoadResult.Ok(prefixes, newEtag, newLm);
            }
        }
        finally
        {
            request.Dispose();
            timeoutCts?.Dispose();
        }
    }

    /// <summary>Redirect hops followed per load. Release assets and moved lists need one or two.</summary>
    public const int MaxRedirects = 5;

    /// <summary>
    /// The URI of the next hop for a redirect response. The handler never follows a redirect
    /// itself (AllowAutoRedirect=false on the named clients): it would re-send every per-source
    /// header except Authorization to each new host, and the conditional validators belong to the
    /// original representation — so each hop is a plain GET without the source's headers or validators.
    /// <para>
    /// A hop to another origin opens a new connection and so re-enters the named client's
    /// ConnectCallback, which validates the resolved IPs and the port (80/443) again — a redirect to
    /// an internal address or a non-standard port is blocked there like any other URL; a same-origin
    /// hop reuses the pooled connection that already passed it. This method only decides where the
    /// next request goes: a relative Location is resolved against the current URI; other schemes
    /// than http/https, a downgrade from https to http, and credentials other than the configured
    /// URL's own on its own host are refused without a request — the list would travel in clear
    /// text, or with credentials to a host nobody chose.
    /// </para>
    /// </summary>
    internal static Uri RedirectTarget(PrefixSourceConfig source, Uri currentUri, HttpStatusCode status, Uri? location)
    {
        var name = LogSanitizer.SanitizeForLog(source.Name);
        if (location is null)
            throw new InvalidOperationException(
                $"Prefix source '{name}': redirect ({(int)status}) without a Location header.");
        var target = new Uri(currentUri, location);
        if (target.Scheme is not ("http" or "https"))
            throw new InvalidOperationException(
                $"Prefix source '{name}': redirect ({(int)status}) to the unsupported scheme '{target.Scheme}' is not followed.");
        // The configured URL's credentials may follow a relative redirect — same host, same
        // credentials — as they would on any other request to that host.
        var configured = Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) ? uri : null;
        var carriesOwnCredentials = configured is not null
            && target.Authority.Equals(configured.Authority, StringComparison.OrdinalIgnoreCase)
            && target.UserInfo == configured.UserInfo;
        if (!string.IsNullOrEmpty(target.UserInfo) && !carriesOwnCredentials)
            throw new InvalidOperationException(
                $"Prefix source '{name}': redirect ({(int)status}) to a URL with embedded credentials is not followed.");
        if (currentUri.Scheme == "https" && target.Scheme == "http")
            throw new InvalidOperationException(
                $"Prefix source '{name}': redirect ({(int)status}) from https to http is not followed.");
        return target;
    }
}
