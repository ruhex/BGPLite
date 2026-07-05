using System.Net;
using System.Net.Sockets;

namespace BGPLite.Providers;

/// <summary>
/// SSRF defense for user-supplied prefix-list URLs (#144). Two layers:
/// <list type="bullet">
/// <item><see cref="CreateValidatedConnectionAsync"/> — wired into SocketsHttpHandler.ConnectCallback;
/// resolves DNS ONCE and validates/connects the same IP (no TOCTOU race, no redirect bypass).</item>
/// <item><see cref="IsBlockedAddress"/> — pure IP check, reused by the callback and by tests.</item>
/// </list>
/// </summary>
public static class PrefixSourceUrlValidator
{
    private static readonly IPNetwork[] BlockedRanges =
    [
        IPNetwork.Parse("0.0.0.0/8"),           // unspecified / current-network
        IPNetwork.Parse("10.0.0.0/8"),          // private (RFC 1918)
        IPNetwork.Parse("100.64.0.0/10"),       // CGNAT (RFC 6598)
        IPNetwork.Parse("127.0.0.0/8"),         // loopback
        IPNetwork.Parse("169.254.0.0/16"),      // link-local (incl. cloud metadata 169.254.169.254)
        IPNetwork.Parse("172.16.0.0/12"),       // private (RFC 1918, incl. Docker bridge 172.17.x.x)
        IPNetwork.Parse("192.168.0.0/16"),      // private (RFC 1918)
        IPNetwork.Parse("198.18.0.0/15"),       // benchmarking (RFC 2544)
        IPNetwork.Parse("224.0.0.0/4"),         // multicast
        IPNetwork.Parse("240.0.0.0/4"),         // reserved (future use)
        IPNetwork.Parse("::1/128"),             // IPv6 loopback
        IPNetwork.Parse("::/128"),              // IPv6 unspecified
        IPNetwork.Parse("fc00::/7"),            // IPv6 unique-local
        IPNetwork.Parse("fe80::/10"),           // IPv6 link-local
    ];

    /// <summary>Per-address connect budget so one blackholed candidate can't consume the whole
    /// ConnectTimeout before the next resolved address is tried.</summary>
    private static readonly TimeSpan PerAttemptConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>True if the address falls in a blocked (non-public) range.</summary>
    internal static bool IsBlockedAddress(IPAddress address)
    {
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        foreach (var range in BlockedRanges)
            if (range.Contains(normalized)) return true;
        return false;
    }

    /// <summary>
    /// SocketsHttpHandler.ConnectCallback: resolves DNS, validates ALL resolved IPs are public,
    /// then connects with a matching-family socket per address (IPv4 preferred) until one succeeds.
    /// No TOCTOU — every address is validated above and the connected IP is one of them.
    /// SocketsHttpHandler does NOT follow redirects (no 302-to-internal-IP bypass).
    /// </summary>
    public static async ValueTask<Stream> CreateValidatedConnectionAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"DNS resolution failed for '{host}': {ex.Message}", ex);
        }

        if (addresses.Length == 0)
            throw new InvalidOperationException($"DNS returned no addresses for '{host}'.");

        foreach (var addr in addresses)
        {
            if (IsBlockedAddress(addr))
                throw new InvalidOperationException(
                    $"SSRF blocked: '{host}' resolves to non-public address {addr}.");
        }

        // Connect with a matching-family socket per address, IPv4-first, until one succeeds. A host
        // whose first DNS record is IPv6 used to throw SocketException (AddressFamilyNotSupported)
        // on the hardcoded IPv4 socket (#151); and many deployments (e.g. an IPv4-only server with no
        // IPv6 on the interface) can only route IPv4 anyway. Now each validated address gets its own
        // socket and we fall through to the next on failure.
        Exception? last = null;
        foreach (var addr in OrderForConnect(addresses))
        {
            // Bound each attempt so one blackholed address can't consume the whole connect budget
            // before the next candidate is tried. Real cancellation (ct) still propagates.
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(PerAttemptConnectTimeout);

            var socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addr, port, attempt.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                socket.Dispose();
                last = new TimeoutException(
                    $"Timed out connecting to '{host}' ({addr}) after {PerAttemptConnectTimeout.TotalSeconds:0}s; trying next address.");
            }
            catch (OperationCanceledException) { socket.Dispose(); throw; }
            catch (Exception ex)
            {
                socket.Dispose();
                last = ex;
            }
        }

        throw new InvalidOperationException(
            $"Could not connect to '{host}' on any of {addresses.Length} resolved address(es).", last);
    }

    /// <summary>
    /// Orders resolved addresses IPv4-first (stable within each family). IPv4 is preferred because
    /// many deployments (e.g. an IPv4-only server with no IPv6 on the interface) can only route IPv4,
    /// so an IPv6 address returned first by DNS would be unreachable. Internal for unit testing.
    /// </summary>
    internal static IEnumerable<IPAddress> OrderForConnect(IPAddress[] addresses)
        => addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1);

    /// <summary>
    /// Validates that a URL is well-formed, uses http/https, and resolves to a public IP.
    /// For future API-level validation (when a user submits a URL, validate before storing).
    /// The actual fetch-time defense is <see cref="CreateValidatedConnectionAsync"/>.
    /// </summary>
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
        catch (Exception ex) when (ex is not OperationCanceledException)
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
