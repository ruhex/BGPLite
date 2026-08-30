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
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var prefixes = doc.RootElement
            .GetProperty("data")
            .GetProperty("prefixes")
            .GetProperty("v4")
            .GetProperty("originating");

        var result = new List<(uint Prefix, byte PrefixLength)>(prefixes.GetArrayLength());

        foreach (var element in prefixes.EnumerateArray())
        {
            var cidr = element.GetString();
            // #319: the canonical parser every other prefix input path uses (#236): host-bit
            // masking, /0 rejection, length 1..32, IPv4-only. RIS collectors return what third
            // parties ANNOUNCED — non-canonical NLRI ("10.0.0.1/8") must not reach the route
            // table under a corrupt key, and "0.0.0.0/0" must not become a default-route leak
            // (#162 closed the same hole for URL sources). Skip + warn, like stored custom
            // prefixes; also covers a null/garbage element without throwing (previously NRE/
            // FormatException took the whole ASN fetch down).
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
