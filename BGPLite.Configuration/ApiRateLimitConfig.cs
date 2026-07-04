using YamlDotNet.Serialization;

namespace BGPLite.Configuration;

/// <summary>
/// Per-client-IP token-bucket rate limiting for the management API (#116), plus an opt-in GLOBAL
/// concurrency cap on in-flight requests (#119). The per-IP limits are generous by default so normal
/// management-API usage never trips them; they exist to protect the process from request floods.
/// The concurrency cap bounds total resource use regardless of source (defense in depth with the
/// per-IP rate, which only bounds flood speed per client). Set <see cref="Enabled"/> = false to disable.
/// </summary>
public sealed class ApiRateLimitConfig
{
    /// <summary>Master switch. Default <c>true</c> (protection out of the box).</summary>
    [YamlMember(Alias = "Enabled")]
    public bool Enabled { get; init; } = true;

    /// <summary>Token-bucket capacity — the largest burst allowed at once (default 120).</summary>
    [YamlMember(Alias = "TokenLimit")]
    public int TokenLimit { get; init; } = 120;

    /// <summary>Tokens replenished each period (default 120).</summary>
    [YamlMember(Alias = "TokensPerPeriod")]
    public int TokensPerPeriod { get; init; } = 120;

    /// <summary>Replenishment period in seconds (default 60).</summary>
    [YamlMember(Alias = "PeriodSeconds")]
    public int PeriodSeconds { get; init; } = 60;

    /// <summary>
    /// GLOBAL cap on concurrently in-flight management-API requests (#119). Default <c>0</c> = no
    /// concurrency cap (live behavior unchanged — opt-in, consistent with the per-IP rate). When
    /// greater than zero and <see cref="Enabled"/> is true, at most this many requests run at once
    /// across ALL clients; the next is rejected with 503 until an in-flight request completes. Bounds
    /// total resource use (in-flight RIPEstat fetches / DB ops) regardless of how fast clients flood.
    /// </summary>
    [YamlMember(Alias = "MaxConcurrentRequests")]
    public int MaxConcurrentRequests { get; init; } = 0;
}
