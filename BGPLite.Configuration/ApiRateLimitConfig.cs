using YamlDotNet.Serialization;

namespace BGPLite.Configuration;

/// <summary>
/// Per-client-IP token-bucket rate limiting for the management API (#116). Limits are generous by
/// default so normal management-API usage never trips them; they exist to protect the process from
/// request floods. Set <see cref="Enabled"/> = false to disable.
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
}
