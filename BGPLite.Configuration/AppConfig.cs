using System.Net;
using System.Net.Sockets;
using YamlDotNet.Serialization;

namespace BGPLite.Configuration;

public sealed class AppConfig
{
    [YamlMember(Alias = "Bgp")]
    public BgpConfig Bgp { get; init; } = new();

    [YamlMember(Alias = "Peers")]
    public List<PeerConfig> Peers { get; init; } = [];

    [YamlMember(Alias = "ApiPort")]
    public int ApiPort { get; init; } = 5001;

    /// <summary>
    /// The IP address the management API binds to (#90). Default <c>null</c> → loopback
    /// (<c>127.0.0.1</c>) — the API is reachable ONLY from the same host, so an operator who wants
    /// to expose it MUST put an authenticated reverse proxy (Caddy/nginx with TLS + auth) in front
    /// and set this to <c>"0.0.0.0"</c> (or a specific interface). This is secure-by-default: the
    /// previous <c>http://+:port</c> bind exposed the unauthenticated control plane on every interface.
    /// </summary>
    [YamlMember(Alias = "ApiListen")]
    public string? ApiListen { get; init; }

    [YamlMember(Alias = "RipeStat")]
    public RipeStatConfig? RipeStat { get; init; }

    /// <summary>Configurable prefix sources (file, http, ...) loaded at startup via the provider factory.</summary>
    [YamlMember(Alias = "PrefixSources")]
    public List<PrefixSourceConfig> PrefixSources { get; init; } = [];

    /// <summary>Name of the source served as the RU/default set for unconfigured peers.</summary>
    [YamlMember(Alias = "DefaultPrefixSource")]
    public string? DefaultPrefixSource { get; init; }

    /// <summary>Optional override for the community stamped on per-peer custom prefixes (default <c>&lt;Asn&gt;:100</c>).</summary>
    [YamlMember(Alias = "CustomPrefixCommunity")]
    public string? CustomPrefixCommunity { get; init; }

    /// <summary>Optional override for the community stamped on per-peer custom-AS-originated prefixes (default <c>&lt;Asn&gt;:200</c>).</summary>
    [YamlMember(Alias = "CustomAsnCommunity")]
    public string? CustomAsnCommunity { get; init; }

    /// <summary>
    /// Trusted reverse-proxy CIDRs whose <c>X-Forwarded-For</c> / <c>X-Real-IP</c> headers are
    /// honored when resolving the management-API client IP (e.g. <c>["127.0.0.0/8", "10.0.0.0/8"]</c>).
    /// Empty (default) = never trust forwarding headers — the direct <c>RemoteEndPoint</c> is used,
    /// and any client-supplied <c>X-Forwarded-For</c> is ignored (#91). When the API runs behind a
    /// reverse proxy, list the proxy's CIDR here so the real client IP is resolved.
    /// </summary>
    [YamlMember(Alias = "TrustedProxies")]
    public List<string> TrustedProxies { get; init; } = [];

    /// <summary>Per-client-IP rate limiting for the management API (#116). Null = defaults applied.</summary>
    [YamlMember(Alias = "ApiRateLimit")]
    public ApiRateLimitConfig? ApiRateLimit { get; init; }

    /// <summary>
    /// Maximum request body size in bytes accepted by the management API on POST/PUT/PATCH routes
    /// (#156). Bodies larger than this are rejected with <c>413 Payload Too Large</c> before
    /// deserialization, defending against memory-exhaustion DoS (<c>HttpListener</c> has no default
    /// body cap). 1 MiB comfortably fits any realistic peer-config payload (hundreds of CIDRs /
    /// ASNs); raise it only if an operator legitimately needs larger writes. Defaults to 1 MiB.
    /// </summary>
    [YamlMember(Alias = "MaxRequestBodyBytes")]
    public long MaxRequestBodyBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Origins allowed to make cross-origin (CORS) requests to the management API (#99), e.g.
    /// <c>["https://operator.example.com", "https://bgp.example.net"]</c>. A request's
    /// <c>Origin</c> header is echoed back as <c>Access-Control-Allow-Origin</c> only when it
    /// exactly matches an entry here (case-insensitive); otherwise <c>no</c> CORS headers are
    /// emitted and the browser blocks the cross-origin request. Null/empty (default) = CORS fully
    /// disabled (secure default, consistent with <see cref="TrustedProxies"/> opt-in) — the
    /// previous blanket <c>"*"</c> was a drive-by CSRF hole on the unauthenticated mutating routes.
    /// </summary>
    [YamlMember(Alias = "CorsAllowedOrigins")]
    public List<string>? CorsAllowedOrigins { get; init; }

    /// <summary>
    /// Validates the whole configuration, throwing <see cref="InvalidOperationException"/> with a
    /// clear message on the first violation (fail-loud). Called from Program.cs right after the YAML
    /// is loaded and before the host is built, so invalid config (bad ASN, RouterId=0.0.0.0,
    /// HoldTime=2, out-of-range ApiPort, malformed peer address, ...) aborts startup instead of
    /// failing later at runtime (#89). Intentional behavior change: previously-silent invalid
    /// config now throws — the operator must fix their YAML.
    /// </summary>
    public void Validate()
    {
        Bgp.Validate();

        if (ApiPort < 1 || ApiPort > 65535)
            throw new InvalidOperationException(
                $"Invalid configuration: ApiPort must be between 1 and 65535 (got {ApiPort}).");

        // MaxRequestBodyBytes is a security boundary (#156 DoS cap); reject nonsensical values at
        // startup so a bad YAML cannot break all mutating API requests (<= 0) or weaken the cap to
        // nothing (impractically large). 1 KiB lower bound leaves room for a minimal peer payload;
        // 64 MiB upper bound is far beyond any legitimate peer-config write.
        if (MaxRequestBodyBytes is < 1024 or > 64 * 1024 * 1024)
            throw new InvalidOperationException(
                $"Invalid configuration: MaxRequestBodyBytes must be between 1024 and 67108864 bytes " +
                $"(got {MaxRequestBodyBytes}).");

        for (var i = 0; i < Peers.Count; i++)
        {
            var peer = Peers[i];
            if (!IPAddress.TryParse(peer.Address, out var address)
                || address.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new InvalidOperationException(
                    $"Invalid configuration: Peers[{i}].Address must be a valid IPv4 address " +
                    $"(got '{peer.Address}').");
            }
        }
    }
}
