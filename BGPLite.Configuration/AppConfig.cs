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
}
