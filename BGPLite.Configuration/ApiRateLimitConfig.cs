using YamlDotNet.Serialization;

namespace BGPLite.Configuration;

/// <summary>
/// Per-client-IP token-bucket rate limiting for the management API, plus an opt-in GLOBAL
/// concurrency cap on in-flight requests. The per-IP limits are generous by default so normal
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
    /// GLOBAL cap on concurrently in-flight management-API requests. Default <c>0</c> = no
    /// concurrency cap (live behavior unchanged — opt-in, consistent with the per-IP rate). When
    /// greater than zero and <see cref="Enabled"/> is true, at most this many requests run at once
    /// across ALL clients; the next is rejected with 503 until an in-flight request completes. Bounds
    /// total resource use (in-flight RIPEstat fetches / DB ops) regardless of how fast clients flood.
    /// </summary>
    [YamlMember(Alias = "MaxConcurrentRequests")]
    public int MaxConcurrentRequests { get; init; } = 0;

    /// <summary>
    /// Validates the ranges this section is actually honoured in. Called from
    /// <see cref="AppConfig.Validate"/> so a bad value fails at startup instead of being silently
    /// clamped by the runtime.
    /// <para>
    /// The clamps in <c>ClientIpRateLimiter</c> / <c>ManagementApi</c> (<c>Math.Max(1, …)</c>)
    /// are defence against a divide-by-zero, not a normalisation policy: they turned a typo into
    /// silently different behaviour. The worst case is <see cref="PeriodSeconds"/> — a negative
    /// value clamped to 1 s turns 120 tokens/60 s into 120 requests/SECOND, i.e. it disables the
    /// flood protection it is meant to configure. A negative <see cref="MaxConcurrentRequests"/>
    /// is worse still: the limiter is only constructed when the value is <c>&gt; 0</c>, so it is
    /// not clamped at all but silently absent.
    /// </para>
    /// </summary>
    public void Validate()
    {
        if (TokenLimit < 1)
            throw new InvalidOperationException(
                $"Invalid configuration: ApiRateLimit.TokenLimit must be at least 1 (got {TokenLimit}).");
        if (TokensPerPeriod < 1)
            throw new InvalidOperationException(
                $"Invalid configuration: ApiRateLimit.TokensPerPeriod must be at least 1 (got {TokensPerPeriod}).");
        if (PeriodSeconds < 1)
            throw new InvalidOperationException(
                $"Invalid configuration: ApiRateLimit.PeriodSeconds must be at least 1 (got {PeriodSeconds}).");
        // 0 is the documented "no cap" value and must stay valid — only negatives are a mistake.
        if (MaxConcurrentRequests < 0)
            throw new InvalidOperationException(
                $"Invalid configuration: ApiRateLimit.MaxConcurrentRequests must be 0 (no cap) or greater (got {MaxConcurrentRequests}).");
    }
}
