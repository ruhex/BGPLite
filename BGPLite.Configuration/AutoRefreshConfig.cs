using YamlDotNet.Serialization;

namespace BGPLite.Configuration;

/// <summary>
/// Periodic auto-refresh configuration (#214). When enabled, a background timer periodically
/// checks all prefix sources for changes using conditional requests (ETag / Last-Modified → 304
/// Not Modified when unchanged). Only sources whose data actually changed trigger peer route
/// refreshes — no unnecessary BGP churn.
/// </summary>
public sealed class AutoRefreshConfig
{
    /// <summary>Master switch. When false (default), no background timer runs.</summary>
    [YamlMember(Alias = "Enabled")]
    public bool Enabled { get; init; }

    /// <summary>How often to check sources that support ETag/Last-Modified (304 = ~1 KB per check).
    /// Default 10 minutes — 304 checks are cheap.</summary>
    [YamlMember(Alias = "IntervalSeconds")]
    public int IntervalSeconds { get; init; } = 600;

    /// <summary>How often to check sources WITHOUT ETag support (e.g. RIPEstat ASN lookups).
    /// These require a full re-fetch + hash comparison. RIPEstat prefix data changes rarely
    /// (prefixes are added/withdrawn gradually), so a long interval reduces unnecessary load.
    /// Default 7 days (604800 seconds).</summary>
    [YamlMember(Alias = "NoEtagIntervalSeconds")]
    public int NoEtagIntervalSeconds { get; init; } = 604800;

    /// <summary>Max random delay (ms) between individual source checks within one cycle. Prevents
    /// burst of conditional requests to the same host (GitHub rate limits: 60 req/min unauthenticated).
    /// Default 2000ms.</summary>
    [YamlMember(Alias = "MaxJitterMs")]
    public int MaxJitterMs { get; init; } = 2000;
}
