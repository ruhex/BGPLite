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
            var cidr = element.GetString()!;
            var slash = cidr.IndexOf('/');
            var ip = IPAddress.Parse(cidr[..slash]);
            var length = byte.Parse(cidr[(slash + 1)..]);
            var prefix = BgpConstants.IPAddressToUint(ip);
            result.Add((prefix, length));
        }

        _logger.LogInformation("AS{Asn}: fetched {Count} prefixes", asn, result.Count);
        return result;
    }
}
