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

        var url = source.Url;
        var http = httpFactory.CreateClient(ClientName);
        if (source.Timeout is int seconds && seconds > 0)
            http.Timeout = TimeSpan.FromSeconds(seconds);
        if (source.Headers is { Count: > 0 } headers)
            foreach (var (key, value) in headers)
            {
                if (key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                    http.DefaultRequestHeaders.Remove(key);
                if (!http.DefaultRequestHeaders.TryAddWithoutValidation(key, value))
                    logger.LogWarning("Source '{Name}': could not add request header '{Header}'.", source.Name, key);
            }

        // Stream-read with size cap (#144): ResponseHeadersRead gets headers first (fast Content-Length
        // check), then stream the body with a hard cap to prevent OOM. SSRF validation is at the
        // handler level (SocketsHttpHandler.ConnectCallback in Program.cs) — no pre-resolve here.
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaxResponseBytes)
            throw new InvalidOperationException(
                $"Prefix source '{source.Name}': response too large ({contentLength} bytes, max {MaxResponseBytes}).");

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxResponseBytes)
                throw new InvalidOperationException(
                    $"Prefix source '{source.Name}': response exceeded {MaxResponseBytes} bytes during stream.");
        }

        var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        var prefixes = PrefixListParser.Parse(text);
        logger.LogInformation("Source '{Name}' (http): loaded {Count} prefixes from {Url}", source.Name, prefixes.Count, url);
        return prefixes;
    }
}
