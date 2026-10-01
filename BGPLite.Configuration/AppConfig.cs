using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
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
    /// The IP address the management API binds to. Default <c>null</c> → loopback
    /// (<c>127.0.0.1</c>): the API is unauthenticated and reachable ONLY from the same host, so an
    /// operator who wants to expose it MUST put an authenticated reverse proxy (Caddy/nginx with
    /// TLS + auth) in front and set this to <c>"0.0.0.0"</c> (or a specific interface) — a
    /// wildcard <c>http://+:port</c> bind would expose the control plane on every interface.
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
    /// and any client-supplied <c>X-Forwarded-For</c> is ignored. When the API runs behind a
    /// reverse proxy, list the proxy's CIDR here so the real client IP is resolved.
    /// </summary>
    [YamlMember(Alias = "TrustedProxies")]
    public List<string> TrustedProxies { get; init; } = [];

    /// <summary>
    /// Opt-in: when <c>true</c>, a trusted proxy's <c>X-Real-IP</c> header is accepted as a
    /// client-IP source when no <c>X-Forwarded-For</c> hop resolves. Default <c>false</c> — unlike
    /// X-Forwarded-For, an X-Real-IP value cannot be verified against the trusted-hop chain, so a
    /// proxy that passes the header through instead of overwriting it (plain nginx without
    /// <c>proxy_set_header X-Real-IP $remote_addr;</c>) turns it into an attacker-controlled input:
    /// fresh rate-limit buckets per request and a forged <c>/api/me</c> identity. Enable only for
    /// proxies guaranteed to overwrite the header. Hot-reloadable (applies to new requests).
    /// </summary>
    [YamlMember(Alias = "TrustXRealIp")]
    public bool TrustXRealIp { get; init; }

    /// <summary>Per-client-IP rate limiting for the management API. Null = defaults applied.</summary>
    [YamlMember(Alias = "ApiRateLimit")]
    public ApiRateLimitConfig? ApiRateLimit { get; init; }

    /// <summary>
    /// Maximum request body size in bytes accepted by the management API on POST/PUT/PATCH routes.
    /// Bodies larger than this are rejected with <c>413 Payload Too Large</c> before
    /// deserialization, defending against memory-exhaustion DoS (<c>HttpListener</c> has no default
    /// body cap). 1 MiB comfortably fits any realistic peer-config payload (hundreds of CIDRs /
    /// ASNs); raise it only if an operator legitimately needs larger writes. Defaults to 1 MiB.
    /// </summary>
    [YamlMember(Alias = "MaxRequestBodyBytes")]
    public long MaxRequestBodyBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Origins allowed to make cross-origin (CORS) requests to the management API, e.g.
    /// <c>["https://operator.example.com", "https://bgp.example.net"]</c>. A request's
    /// <c>Origin</c> header is echoed back as <c>Access-Control-Allow-Origin</c> only when it
    /// exactly matches an entry here (case-insensitive); otherwise <c>no</c> CORS headers are
    /// emitted, so the browser withholds the <em>response body</em> from a cross-origin caller.
    /// Null/empty (default) = CORS fully disabled (secure default, consistent with
    /// <see cref="TrustedProxies"/> opt-in) — the previous blanket <c>"*"</c> leaked every
    /// response to any origin.
    /// <para>
    /// This is a RESPONSE-READ control, not CSRF protection. It does not stop a cross-origin
    /// request from being SENT or APPLIED: for a CORS-"simple" request (no preflight) the browser
    /// blocks only the response, after the state change has already happened. CSRF is handled
    /// separately — mutating routes require <c>Content-Type: application/json</c>, which forces a
    /// preflight gated by this allowlist, and refuse <c>Sec-Fetch-Site: cross-site</c>.
    /// </para>
    /// </summary>
    [YamlMember(Alias = "CorsAllowedOrigins")]
    public List<string>? CorsAllowedOrigins { get; init; }

    /// <summary>
    /// Periodic auto-refresh: a background timer checks all prefix sources for changes using
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
    /// failing later at runtime. Invalid config is never silently accepted — the operator must fix
    /// their YAML.
    /// </summary>
    public void Validate()
    {
        // Structural pass FIRST: it walks the whole config graph and rejects null list ELEMENTS
        // (an empty YAML "- " item) with their full path, before any hand-rolled per-list loop
        // below gets the chance to dereference one. Reflection is deliberate — a new collection
        // property added to any config type is covered automatically, so the invariant is a
        // property of the graph, not of each call site.
        ValidateListElements(this, "");

        Bgp.Validate();

        if (ApiPort < 1 || ApiPort > 65535)
            throw new InvalidOperationException(
                $"Invalid configuration: ApiPort must be between 1 and 65535 (got {ApiPort}).");

        // MaxRequestBodyBytes is a security boundary (memory-exhaustion DoS cap); reject
        // nonsensical values at startup so a bad YAML cannot break all mutating API requests
        // (<= 0) or weaken the cap to nothing (impractically large). 1 KiB lower bound leaves
        // room for a minimal peer payload; 64 MiB upper bound is far beyond any legitimate
        // peer-config write.
        if (MaxRequestBodyBytes is < 1024 or > 64 * 1024 * 1024)
            throw new InvalidOperationException(
                $"Invalid configuration: MaxRequestBodyBytes must be between 1024 and 67108864 bytes " +
                $"(got {MaxRequestBodyBytes}).");

        // "Peers:" is NOT a supported way to declare peers. It binds, it is listed in
        // README.md and appsettings.Example.yml, and this method used to validate every element
        // of it — but no production code path ever read the property, so an operator who declared
        // peers in YAML got a green validation, a clean startup, and no peers in the database
        // until each one connected and was auto-registered (D11). `git log -S 'config.Peers'`
        // shows it was never wired up, not that it was removed.
        //
        // Rejecting it is the honest outcome and matches the config rule ("fail loud at startup —
        // never a runtime catch-and-continue"): a key that validates but does nothing is the exact
        // failure this repo forbids. It is NOT an allow-list either — per D11 any peer completing
        // an OPEN is upserted regardless of this list — so silently ignoring it left operators
        // believing they had restricted who could peer in.
        //
        // An explicit YAML null ("Peers:") keeps meaning "none" and stays valid.
        if (Peers is { Count: > 0 })
            throw new InvalidOperationException(
                "Invalid configuration: the 'Peers:' list is no longer applied and cannot be used to " +
                "declare peers — it has never been read by any code path, and it is not an allow-list " +
                $"(any peer that completes an OPEN is registered automatically, see D11). Found {Peers.Count} " +
                "entry/entries. Remove the 'Peers:' block and register peers through the management API " +
                "instead: POST http://127.0.0.1:5001/api/peers with {\"ip\":\"...\",\"asn\":N}.");

        // Prefix-source errors otherwise surface only at load time, where LoadAllAsync absorbs
        // them into a Warning plus an empty prefix set — a config typo silently serves zero prefixes
        // until restart. Fail loud at startup (and reject the file on hot reload) instead. The
        // per-kind required fields mirror the providers' own load-time checks; the community rule
        // is CommunityCodec's, single-sourced via the Protocol leaf. The ?? [] guards keep an
        // explicit YAML null ("PrefixSources:") meaning "none" — every runtime consumer treats it
        // that way, and Validate must reject with a message, never with an NRE.
        var prefixSources = PrefixSources ?? [];
        var sourceNames = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < prefixSources.Count; i++)
        {
            // Null elements are rejected by the structural pass at the top of Validate.
            var source = prefixSources[i];
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
                    // AS 0 goes through the single validation point (see AsnValidation).
                    AsnValidation.RequirePositive(source.Asn.Value, $"{at}: Asn");
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
        // fall back to the default/untagged value at send time (ConfigCommunityResolver only logs).
        ValidateCommunity(CustomPrefixCommunity, "CustomPrefixCommunity");
        ValidateCommunity(CustomAsnCommunity, "CustomAsnCommunity");
        var asnLists = RipeStat?.AsnLists ?? [];
        for (var i = 0; i < asnLists.Count; i++)
        {
            var list = asnLists[i];
            // Null elements were already rejected by the structural pass above. A null Asns
            // COLLECTION ("Asns:" with no value) is different: every runtime consumer
            // dereferences it unconditionally, so accepting it as "none" would only move the
            // NRE from startup to the first route build. "None" here is [] (or omitting the
            // key, which deserializes to the empty default) — reject with the path instead.
            if (list.Asns is null)
                throw new InvalidOperationException(
                    $"Invalid configuration: RipeStat.AsnLists[{i}].Asns is null — use [] (or omit the key) for a country-only list.");
            ValidateCommunity(list.Community, $"RipeStat.AsnLists[{i}] ('{list.Name}'): Community");
            for (var j = 0; j < list.Asns.Count; j++)
                AsnValidation.RequirePositive(list.Asns[j], $"RipeStat.AsnLists[{i}].Asns[{j}]");
        }

        // Resilience/auto-refresh tunables are applied verbatim: a negative value would silently
        // disable retries or (worse) schedule a zero-second timer storm. Fail loud.
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
    /// validation is the only place a malformed community is actually rejected.
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

    /// <summary>
    /// The structural choke point for collection shape: recurses the config graph (objects and
    /// list elements alike) and rejects a null ELEMENT with its full path. A null COLLECTION is
    /// deliberately NOT rejected — an explicit YAML null ("Peers:", "PrefixSources:") means "none"
    /// and every consumer normalizes with <c>?? []</c>; it is the null element (an empty list item
    /// "-" that YamlDotNet materializes as null) that used to reach a hand-rolled loop and die as
    /// a bare NullReferenceException with no hint about which key was malformed. Value-type
    /// elements (e.g. <c>Asns: List&lt;uint&gt;</c>) can never be null and are skipped by the
    /// <c>item is null</c> check naturally; their semantic rules (AS 0) belong to
    /// <see cref="AsnValidation"/>, not to this shape pass.
    /// </summary>
    private static void ValidateListElements(object node, string path)
    {
        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0)
                continue;
            var value = property.GetValue(node);
            if (value is null || value is string)
                continue;

            var childPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
            if (value is IEnumerable items)
            {
                var index = 0;
                foreach (var item in items)
                {
                    if (item is null)
                        throw new InvalidOperationException(
                            $"Invalid configuration: {childPath}[{index}] is empty — each item must be a " +
                            "non-null value (an empty YAML list item '-' deserializes to null).");
                    if (IsConfigNode(item))
                        ValidateListElements(item, $"{childPath}[{index}]");
                    index++;
                }
            }
            else if (IsConfigNode(value))
            {
                ValidateListElements(value, childPath);
            }
        }
    }

    /// <summary>Whether <paramref name="value"/> is a nested config record worth recursing into —
    /// anything else (primitives, framework types) carries no collections of ours.</summary>
    private static bool IsConfigNode(object value) =>
        value.GetType().Namespace == typeof(AppConfig).Namespace;
}
