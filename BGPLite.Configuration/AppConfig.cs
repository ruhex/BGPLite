using System.Net;
using System.Net.Sockets;
using BGPLite.Protocol;
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

    /// <summary>
    /// Opt-in (#256): when <c>true</c>, a trusted proxy's <c>X-Real-IP</c> header is accepted as a
    /// client-IP source when no <c>X-Forwarded-For</c> hop resolves. Default <c>false</c> — unlike
    /// X-Forwarded-For, an X-Real-IP value cannot be verified against the trusted-hop chain, so a
    /// proxy that passes the header through instead of overwriting it (plain nginx without
    /// <c>proxy_set_header X-Real-IP $remote_addr;</c>) turns it into an attacker-controlled input:
    /// fresh rate-limit buckets per request and a forged <c>/api/me</c> identity. Enable only for
    /// proxies guaranteed to overwrite the header. Hot-reloadable (applies to new requests).
    /// </summary>
    [YamlMember(Alias = "TrustXRealIp")]
    public bool TrustXRealIp { get; init; }

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
    /// Periodic auto-refresh (#214): a background timer checks all prefix sources for changes using
    /// conditional requests (ETag/Last-Modified → 304). Only changed sources trigger peer refreshes.
    /// Null/absent (default) = disabled — sources are only refreshed on peer connect/ROUTE_REFRESH.
    /// </summary>
    [YamlMember(Alias = "AutoRefresh")]
    public AutoRefreshConfig? AutoRefresh { get; init; }

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
            // #390: an omitted Address used to default to "0.0.0.0" and slip through — require a
            // real unicast address and reject the all-zeros placeholder explicitly.
            if (string.IsNullOrWhiteSpace(peer.Address))
                throw new InvalidOperationException(
                    $"Invalid configuration: Peers[{i}].Address is required — a configured peer must know where it connects from.");
            if (!IPAddress.TryParse(peer.Address, out var address)
                || address.AddressFamily != AddressFamily.InterNetwork
                || IPAddress.Any.Equals(address))
            {
                throw new InvalidOperationException(
                    $"Invalid configuration: Peers[{i}].Address must be a valid IPv4 address other than 0.0.0.0 " +
                    $"(got '{peer.Address}').");
            }
            // #390: a configured peer without a remote ASN can never match an OPEN — fail loud
            // instead of silently relying on auto-registration.
            if (peer.RemoteAsn is null)
                throw new InvalidOperationException(
                    $"Invalid configuration: Peers[{i}].RemoteAsn is required for a configured peer " +
                    "(omit the Peers entry entirely to rely on auto-registration).");
        }

        // #327: prefix-source errors used to surface only at load time, where LoadAllAsync absorbs
        // them into a Warning plus an empty prefix set — a config typo silently served zero prefixes
        // until restart. Fail loud at startup (and reject the file on hot reload) instead. The
        // per-kind required fields mirror the providers' own load-time checks
        // (FilePrefixProvider/HttpPrefixProvider/AsnPrefixProvider); the community rule is
        // CommunityCodec's, single-sourced via the Protocol leaf. The ?? [] guards keep an explicit
        // YAML null ("PrefixSources:") meaning "none" — every runtime consumer treats it that way,
        // and Validate must reject with a message, never with an NRE.
        var prefixSources = PrefixSources ?? [];
        var sourceNames = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < prefixSources.Count; i++)
        {
            var source = prefixSources[i];
            // An empty YAML list item ("- ") deserializes as a null element — reject it with a
            // message instead of an NRE, like the null-collection case above.
            if (source is null)
                throw new InvalidOperationException(
                    $"Invalid configuration: PrefixSources[{i}] is empty — each item must be a mapping (Kind, Name, ...).");
            var at = $"PrefixSources[{i}] ('{source.Name}')";

            if (string.IsNullOrWhiteSpace(source.Name))
                throw new InvalidOperationException($"Invalid configuration: {at} requires a Name.");
            if (!sourceNames.Add(source.Name))
                throw new InvalidOperationException(
                    $"Invalid configuration: duplicate prefix source name '{source.Name}' — sources are addressed by name (subscriptions, per-source cache).");

            switch (source.Kind)
            {
                case "file":
                    if (string.IsNullOrWhiteSpace(source.Path))
                        throw new InvalidOperationException($"Invalid configuration: {at}: Kind=file requires a Path.");
                    break;
                case "http":
                    if (string.IsNullOrWhiteSpace(source.Url))
                        throw new InvalidOperationException($"Invalid configuration: {at}: Kind=http requires a Url.");
                    // A malformed URL only fails inside HttpClient after startup, and LoadAllAsync
                    // absorbs that into a Warning plus zero prefixes — the same silent class this
                    // validation exists to prevent. Deeper checks (SSRF ranges, ports) stay at fetch
                    // time in PrefixSourceUrlValidator.
                    if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var url)
                        || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                        throw new InvalidOperationException(
                            $"Invalid configuration: {at}: Url must be an absolute http(s) URL (got '{source.Url}').");
                    break;
                case "asn":
                    if (!source.Asn.HasValue)
                        throw new InvalidOperationException($"Invalid configuration: {at}: Kind=asn requires an Asn.");
                    if (source.Asn.Value == 0)
                        throw new InvalidOperationException(
                            $"Invalid configuration: {at}: Asn must be a positive AS number (RFC 7607 rejects AS 0).");
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Invalid configuration: {at}: unknown Kind '{source.Kind}' (expected file, http or asn).");
            }

            if (source.Timeout is <= 0)
                throw new InvalidOperationException(
                    $"Invalid configuration: {at}: Timeout must be a positive number of seconds (got {source.Timeout}).");

            ValidateCommunity(source.Community, $"{at}: Community");
        }

        if (!string.IsNullOrEmpty(DefaultPrefixSource) && !sourceNames.Contains(DefaultPrefixSource))
            throw new InvalidOperationException(
                $"Invalid configuration: DefaultPrefixSource '{DefaultPrefixSource}' does not match any PrefixSources entry — unconfigured peers would silently get zero prefixes.");

        // Every community string in this file goes through the same rule so a typo cannot silently
        // fall back at send time (ConfigCommunityResolver) — the runtime half landed with #335.
        ValidateCommunity(CustomPrefixCommunity, "CustomPrefixCommunity");
        ValidateCommunity(CustomAsnCommunity, "CustomAsnCommunity");
        var asnLists = RipeStat?.AsnLists ?? [];
        for (var i = 0; i < asnLists.Count; i++)
        {
            var list = asnLists[i];
            if (list is null)
                throw new InvalidOperationException(
                    $"Invalid configuration: RipeStat.AsnLists[{i}] is empty — each item must be a mapping (Name, Asns, ...).");
            ValidateCommunity(list.Community, $"RipeStat.AsnLists[{i}] ('{list.Name}'): Community");
        }

        // #390: resilience/auto-refresh tunables were taken verbatim — a negative silently
        // disabled retries or (worse) scheduled a zero-second timer storm. Fail loud.
        if (RipeStat is { } ripe)
        {
            if (ripe.TimeoutSeconds < 0)
                throw new InvalidOperationException(
                    $"Invalid configuration: RipeStat.TimeoutSeconds must be >= 0 seconds (got {ripe.TimeoutSeconds}).");
            if (ripe.RetryAttempts < 0)
                throw new InvalidOperationException(
                    $"Invalid configuration: RipeStat.RetryAttempts must be >= 0 (got {ripe.RetryAttempts}).");
            if (ripe.RetryDelaySeconds < 0)
                throw new InvalidOperationException(
                    $"Invalid configuration: RipeStat.RetryDelaySeconds must be >= 0 seconds (got {ripe.RetryDelaySeconds}).");
        }

        if (AutoRefresh is { } auto)
        {
            if (auto.IntervalSeconds < 1)
                throw new InvalidOperationException(
                    $"Invalid configuration: AutoRefresh.IntervalSeconds must be a positive number of seconds (got {auto.IntervalSeconds}).");
            if (auto.NoEtagIntervalSeconds < 1)
                throw new InvalidOperationException(
                    $"Invalid configuration: AutoRefresh.NoEtagIntervalSeconds must be a positive number of seconds (got {auto.NoEtagIntervalSeconds}).");
            if (auto.MaxJitterMs < 0)
                throw new InvalidOperationException(
                    $"Invalid configuration: AutoRefresh.MaxJitterMs must be >= 0 ms (got {auto.MaxJitterMs}).");
        }
    }

    /// <summary>
    /// Fail-loud variant of the community format check: the runtime layers (ConfigCommunityResolver,
    /// RouteSeedingService) deliberately never throw and fall back to defaults/untagged, so config
    /// validation is the only place a malformed community is actually rejected (#327).
    /// </summary>
    private void ValidateCommunity(string? community, string field)
    {
        if (string.IsNullOrEmpty(community))
            return;
        try { CommunityCodec.Parse(community); }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"Invalid configuration: {field} is not a valid 'ASN:VALUE' community — {ex.Message}", ex);
        }
    }
}
