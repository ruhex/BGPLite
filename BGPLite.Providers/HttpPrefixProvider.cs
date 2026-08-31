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
/// The URL is fetched as-is. Uses <see cref="IHttpClientFactory"/> so the handler pool is recycled
/// by the factory (the provider is stateless and safe to hold as a singleton).
/// <para>SSRF defense (#144): the named-client's <c>SocketsHttpHandler.ConnectCallback</c> validates
/// every connection's DNS resolution at the socket level — no TOCTOU race, no redirect bypass.
/// Response body is capped at <see cref="MaxResponseBytes"/> to prevent OOM.</para>
/// <para>Conditional requests (#214): when <paramref name="etag"/> / <paramref name="lastModified"/>
/// are provided from a prior load, sends <c>If-None-Match</c> / <c>If-Modified-Since</c>. A 304
/// response skips the body download entirely (~1 KB headers-only round-trip).</para>
/// </summary>
public sealed class HttpPrefixProvider(
    IHttpClientFactory httpFactory,
    ILogger<HttpPrefixProvider> logger,
    int defaultFetchTimeoutSeconds = 30) // mirrors DefaultFetchTimeoutSeconds (a ctor default cannot reference the member const)
    : IPrefixSourceProvider
{
    public const string ClientName = "http";

    /// <summary>Maximum response body size (10 MB) — defends against OOM from huge/malicious files (#144).</summary>
    internal const int MaxResponseBytes = 10 * 1024 * 1024;

    /// <summary>
    /// #324: budget applied when the source does not configure one. Without it, a YAML-configured
    /// PrefixSource omitting <c>Timeout</c> armed no deadline at all — the named client is
    /// InfiniteTimeSpan (Polly) and the pipeline's timeout only wrapped SendAsync up to the
    /// headers, so a headers-then-drip body hung seeding and auto-refresh indefinitely (size is
    /// bounded by <see cref="MaxResponseBytes"/>, time was not). Injectable for tests via the ctor.
    /// </summary>
    public const int DefaultFetchTimeoutSeconds = 30;

    public string Kind => "http";

    /// <summary>HTTP conditional requests (If-None-Match / If-Modified-Since → 304) are supported (#214).</summary>
    public bool SupportsConditionalRequests => true;

    public async Task<SourceLoadResult> LoadAsync(
        PrefixSourceConfig source,
        string? etag = null,
        DateTimeOffset? lastModified = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source.Url))
            throw new InvalidOperationException($"Prefix source '{source.Name}': Kind=http requires a Url.");

        var http = httpFactory.CreateClient(ClientName);

        // Per-source timeout: link the caller's token with a CancelAfter so a slow source can't pin
        // the fetch past its budget — now ALWAYS armed (#324): a configured Timeout wins, otherwise
        // the default budget covers config sources that omit it (and #320's user sources pass it
        // explicitly). The linked token covers headers AND the body loop below.
        // We do NOT mutate http.Timeout (#155 regression): the named client is pooled by
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

        // Per-source headers go on the REQUEST message, never on http.DefaultRequestHeaders (#155):
        // the named client is pooled and shared, so mutating its default headers leaks source A's
        // Authorization / X-API-Key onto source B's next request. Per-message headers merge with the
        // client's configured defaults (User-Agent: BGPLite/1.0) and override per-source.
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        if (source.Headers is { Count: > 0 } headers)
            foreach (var (key, value) in headers)
                if (!request.Headers.TryAddWithoutValidation(key, value))
                    logger.LogWarning("Source '{Name}': could not add request header '{Header}'.", source.Name, key);

        // #214: conditional request — send validators from the prior load. GitHub raw, CDNs, and
        // most HTTP servers support ETag / Last-Modified and reply 304 Not Modified (no body) when
        // the content hasn't changed, making the periodic check ~1 KB instead of a full re-download.
        if (etag is not null)
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        if (lastModified is not null)
            request.Headers.IfModifiedSince = lastModified;

        try
        {
            // Stream-read with size cap (#144): ResponseHeadersRead gets headers first (fast Content-Length
            // check), then stream the body with a hard cap to prevent OOM. SSRF validation is at the
            // handler level (SocketsHttpHandler.ConnectCallback in Program.cs) — no pre-resolve here.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedToken);

            // #214: 304 Not Modified — server confirms data unchanged. No body to parse. Extract any
            // updated validators the server may have returned (some servers refresh ETag on 304).
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                logger.LogDebug("Source '{Name}' (http): 304 Not Modified", source.Name);
                var updatedEtag = response.Headers.ETag?.Tag;
                var updatedLm = response.Content.Headers.LastModified;
                return SourceLoadResult.NotModifiedResult(updatedEtag ?? etag, updatedLm ?? lastModified);
            }

            // #321: redirects are not followed (AllowAutoRedirect=false on the named client) — the
            // handler would re-send every per-source header except Authorization to the target
            // host, so a 3xx is an operator-fixable configuration error, not something to chase.
            if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
                throw new InvalidOperationException(
                    $"Prefix source '{source.Name}': the URL redirects ({(int)response.StatusCode}) — " +
                    "redirects are not followed; configure the final URL.");

            response.EnsureSuccessStatusCode();

            // Extract validators from the 200 response for next time (#214).
            var newEtag = response.Headers.ETag?.Tag;
            var newLm = response.Content.Headers.LastModified;

            if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaxResponseBytes)
                throw new InvalidOperationException(
                    $"Prefix source '{source.Name}': response too large ({contentLength} bytes, max {MaxResponseBytes}).");

            using var stream = await response.Content.ReadAsStreamAsync(linkedToken);
            using var ms = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, linkedToken)) > 0)
            {
                ms.Write(buffer, 0, read);
                if (ms.Length > MaxResponseBytes)
                    throw new InvalidOperationException(
                        $"Prefix source '{source.Name}': response exceeded {MaxResponseBytes} bytes during stream.");
            }

            var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            var prefixes = PrefixListParser.Parse(text);
            // Log only the source Name (the operator/peer-supplied identifier), never the URL — peer URLs
            // (#147) may carry tokens in the query string that must not reach application logs.
            logger.LogInformation("Source '{Name}' (http): loaded {Count} prefixes", source.Name, prefixes.Count);
            return SourceLoadResult.Ok(prefixes, newEtag, newLm);
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }
}
