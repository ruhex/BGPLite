using System.Net;

namespace BGPLite.Providers;

/// <summary>
/// SSRF defense for user-supplied prefix-list URLs (#144): validates that a URL's host resolves
/// to a public IP (not private/loopback/link-local/cloud-metadata) before fetch.
/// </summary>
internal static class PrefixSourceUrlValidator
{
    private static readonly IPNetwork[] BlockedRanges =
    [
        IPNetwork.Parse("127.0.0.0/8"),        // loopback
        IPNetwork.Parse("10.0.0.0/8"),          // private (RFC 1918)
        IPNetwork.Parse("172.16.0.0/12"),       // private (RFC 1918, incl. Docker bridge 172.17.x.x)
        IPNetwork.Parse("192.168.0.0/16"),      // private (RFC 1918)
        IPNetwork.Parse("169.254.0.0/16"),      // link-local (incl. cloud metadata 169.254.169.254)
        IPNetwork.Parse("0.0.0.0/8"),           // unspecified / current-network
        IPNetwork.Parse("::1/128"),             // IPv6 loopback
        IPNetwork.Parse("fc00::/7"),            // IPv6 unique-local
        IPNetwork.Parse("fe80::/10"),           // IPv6 link-local
        IPNetwork.Parse("::/128"),              // IPv6 unspecified
    ];

    /// <summary>True if the address falls in a blocked (non-public) range.</summary>
    internal static bool IsBlockedAddress(IPAddress address)
    {
        // Normalize IPv4-mapped IPv6 (::ffff:x.x.x.x) so IPv4 CIDRs match (CodeRabbit #117 lesson).
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        foreach (var range in BlockedRanges)
            if (range.Contains(normalized)) return true;
        return false;
    }

    /// <summary>
    /// Validates that a URL is well-formed, uses http/https, and resolves to a public IP.
    /// Returns (true, null) if safe; (false, reason) if blocked/malformed.
    /// </summary>
    /// <param name="dnsResolver">Injectable DNS resolver (for testing); default uses real DNS.</param>
    internal static async Task<(bool IsValid, string? Error)> ValidateUrlAsync(
        string url,
        Func<string, CancellationToken, ValueTask<IPAddress[]>>? dnsResolver = null,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return (false, $"Invalid URL: '{url}'.");

        if (uri.Scheme is not ("http" or "https"))
            return (false, $"URL scheme must be http or https: '{url}'.");

        var host = uri.Host;
        var resolver = dnsResolver ?? DefaultDnsResolver;
        IPAddress[] addresses;
        try
        {
            addresses = await resolver(host, ct);
        }
        catch (Exception ex)
        {
            return (false, $"DNS resolution failed for '{host}': {ex.Message}");
        }

        if (addresses.Length == 0)
            return (false, $"DNS returned no addresses for '{host}'.");

        foreach (var addr in addresses)
        {
            if (IsBlockedAddress(addr))
                return (false, $"URL host '{host}' resolves to blocked address {addr} (private/loopback/link-local).");
        }

        return (true, null);
    }

    private static async ValueTask<IPAddress[]> DefaultDnsResolver(string host, CancellationToken ct)
        => await Dns.GetHostAddressesAsync(host, ct);
}
