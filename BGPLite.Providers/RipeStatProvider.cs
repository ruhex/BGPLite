using System.Net;
using System.Text.Json;
using BGPLite.Configuration;
using BGPLite.Protocol;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace BGPLite.Providers;

public sealed class RipeStatProvider
{
    /// <summary>Named-client key registered with <c>IHttpClientFactory</c>.</summary>
    public const string ClientName = "ripestat";

    /// <summary>Maximum response body size (10 MB) — same bound as HttpPrefixProvider (#144/#321).</summary>
    internal const int MaxResponseBytes = 10 * 1024 * 1024;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<RipeStatProvider> _logger;
    private readonly RipeStatConfig _config;

    public RipeStatProvider(IHttpClientFactory httpFactory, ILogger<RipeStatProvider> logger, RipeStatConfig? config = null)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _config = config ?? new RipeStatConfig();
    }

    /// <summary>
    /// Fetches the IPv4 prefixes originated by <paramref name="asn"/> from RIPEstat. The named
    /// client's resilience handler (Program.cs, #104) retries transient HTTP failures (429/5xx/
    /// timeouts/network errors) with exponential backoff + circuit breaker, so this method performs
    /// a single attempt — a transient failure propagates only after the resilience pipeline is
    /// exhausted. The ris-prefixes endpoint can take minutes for large origin ASes (e.g. AS3356).
    /// </summary>
    public async Task<IReadOnlyList<(uint Prefix, byte PrefixLength)>> GetPrefixesAsync(uint asn, CancellationToken ct = default)
    {
        var url = $"https://stat.ripe.net/data/ris-prefixes/data.json?resource=AS{asn}&list_prefixes=true";
        var http = _httpFactory.CreateClient(ClientName);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // #321 item 4: bound the body like every other fetch path (HttpPrefixProvider caps URL
        // sources at 10 MB, #144) — ReadAsStringAsync would fully buffer whatever arrives before
        // parsing. Fast Content-Length check first, then a hard cap while streaming.
        if (response.Content.Headers.ContentLength is long declared && declared > MaxResponseBytes)
            throw new InvalidOperationException(
                $"RIPEstat response for AS{asn} too large ({declared} bytes, max {MaxResponseBytes}).");
        // #324 parity: the resilience pipeline clips at the response headers
        // (ResponseHeadersRead), so the body loop needs its own deadline — a slow-dripping origin
        // must not hold the fetch open; size alone bounds memory, not time.
        using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Math.Max mirrors the pipeline's clamp (Program.cs) — a configured 0/negative must mean
        // "the minimum", not CancelAfter(0) silently cancelling every body read (#321 review).
        bodyCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _config.TimeoutSeconds)));
        await using var stream = await response.Content.ReadAsStreamAsync(bodyCts.Token);
        using var buffered = new MemoryStream();
        var readBuffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(readBuffer, bodyCts.Token)) > 0)
        {
            buffered.Write(readBuffer, 0, read);
            if (buffered.Length > MaxResponseBytes)
                throw new InvalidOperationException(
                    $"RIPEstat response for AS{asn} exceeded {MaxResponseBytes} bytes during stream.");
        }
        using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(buffered.GetBuffer(), 0, (int)buffered.Length));

        var prefixes = doc.RootElement
            .GetProperty("data")
            .GetProperty("prefixes")
            .GetProperty("v4")
            .GetProperty("originating");

        var result = new List<(uint Prefix, byte PrefixLength)>(prefixes.GetArrayLength());

        foreach (var element in prefixes.EnumerateArray())
        {
            // #319/#358-review: the canonical parser every other prefix input path uses (#236):
            // host-bit masking, /0 rejection, length 1..32, IPv4-only. RIS collectors return what
            // third parties ANNOUNCED — non-canonical NLRI ("10.0.0.1/8") must not reach the route
            // table under a corrupt key, and "0.0.0.0/0" must not become a default-route leak
            // (#162 closed the same hole for URL sources). Skip + warn, like stored custom
            // prefixes; a null element, garbage string, or NON-STRING JSON element (GetString
            // throws InvalidOperationException on numbers/objects) is skipped without taking the
            // whole ASN fetch down with it.
            var cidr = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
            if (!PrefixCidr.TryParse(cidr, out var prefix, out var length))
            {
                _logger.LogWarning("AS{Asn}: RIPEstat returned a non-canonical prefix '{Cidr}'; skipped", asn, cidr);
                continue;
            }

            result.Add((prefix, length));
        }

        _logger.LogInformation("AS{Asn}: fetched {Count} prefixes", asn, result.Count);
        return result;
    }
}
