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
/// </summary>
public sealed class HttpPrefixProvider(
    IHttpClientFactory httpFactory,
    ILogger<HttpPrefixProvider> logger)
    : IPrefixSourceProvider
{
    public const string ClientName = "http";

    /// <summary>Maximum response body size (10 MB) — defends against OOM from huge/malicious files (#144).</summary>
    internal const int MaxResponseBytes = 10 * 1024 * 1024;

    public string Kind => "http";

    public async Task<IReadOnlyList<(uint Prefix, byte Length)>> LoadAsync(PrefixSourceConfig source, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source.Url))
            throw new InvalidOperationException($"Prefix source '{source.Name}': Kind=http requires a Url.");

        var http = httpFactory.CreateClient(ClientName);

        // Per-source timeout: link the caller's token with a CancelAfter so a slow peer URL can't pin
        // the fetch past its configured budget. We do NOT mutate http.Timeout (#155 regression): the
        // named client is pooled by IHttpClientFactory, and mutating it leaks the per-source timeout
        // onto the next caller that reuses the same client within the handler-lifetime window.
        CancellationTokenSource? timeoutCts = null;
        CancellationToken linkedToken;
        if (source.Timeout is int seconds && seconds > 0)
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

        try
        {
            // Stream-read with size cap (#144): ResponseHeadersRead gets headers first (fast Content-Length
            // check), then stream the body with a hard cap to prevent OOM. SSRF validation is at the
            // handler level (SocketsHttpHandler.ConnectCallback in Program.cs) — no pre-resolve here.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedToken);
            response.EnsureSuccessStatusCode();

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
            return prefixes;
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }
}
