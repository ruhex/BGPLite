using YamlDotNet.Serialization;

namespace BGPLite.Configuration;

public sealed class RipeStatConfig
{
    /// <summary>Default request timeout for RIPEstat queries. Generous on purpose: the
    /// <c>ris-prefixes</c> endpoint can take minutes to generate a response for large origin
    /// ASes (e.g. AS3356 / Lumen — a Tier-1 transit backbone — routinely ~2 minutes).</summary>
    public const int DefaultTimeoutSeconds = 180;

    [YamlMember(Alias = "AsnLists")]
    public List<AsnList> AsnLists { get; init; } = [];

    /// <summary>Per-request HTTP timeout in seconds for RIPEstat queries. Defaults to
    /// <see cref="DefaultTimeoutSeconds"/>. Raise it for very large origin ASes whose
    /// <c>ris-prefixes</c> response takes minutes to generate; lower it for small ASes
    /// to fail fast.</summary>
    [YamlMember(Alias = "TimeoutSeconds")]
    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;

    /// <summary>Number of retries after a transient failure (client timeout, HTTP 429 / 5xx).
    /// 0 disables retries. Each retry uses exponential backoff (see <see cref="RetryDelaySeconds"/>).
    /// Defaults to 2.</summary>
    [YamlMember(Alias = "RetryAttempts")]
    public int RetryAttempts { get; init; } = 2;

    /// <summary>Base delay in seconds between retries; doubled on each attempt (exponential
    /// backoff). Defaults to 2. Set to 0 for immediate retries (e.g. in tests).</summary>
    [YamlMember(Alias = "RetryDelaySeconds")]
    public int RetryDelaySeconds { get; init; } = 2;
}

public sealed class AsnList
{
    [YamlMember(Alias = "Name")]
    public string Name { get; init; } = "";

    [YamlMember(Alias = "Description")]
    public string Description { get; init; } = "";

    [YamlMember(Alias = "Asns")]
    public List<uint> Asns { get; init; } = [];

    [YamlMember(Alias = "Country")]
    public string? Country { get; init; }

    /// <summary>Optional community in <c>"ASN:VALUE"</c> form stamped on every prefix advertised from this list (e.g. <c>"65444:100"</c>).</summary>
    [YamlMember(Alias = "Community")]
    public string? Community { get; init; }
}
