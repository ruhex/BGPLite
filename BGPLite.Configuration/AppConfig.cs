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
