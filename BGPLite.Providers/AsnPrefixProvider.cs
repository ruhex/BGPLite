using BGPLite.Configuration;
using Microsoft.Extensions.Logging;

namespace BGPLite.Providers;

/// <summary>
/// Loads the prefixes originated by a single AS via RIPEstat (Kind = <c>"asn"</c>).
/// Requires <see cref="PrefixSourceConfig.Asn"/>. Goes through the SHARED per-ASN cache
/// (<see cref="RipeStatPrefixCache"/>, #267 item 5) — the same instance PrefixService uses for
/// <c>RipeStat.AsnLists</c> and custom ASNs — so an ASN configured in both mechanisms is fetched
/// and cached once (the previous direct-to-wire path doubled RIPEstat traffic and served
/// disagreeing snapshots with independent TTLs).
/// <para>RIPEstat does not support ETag / Last-Modified — the <paramref name="etag"/> and
/// <paramref name="lastModified"/> parameters are ignored. Content-change detection for auto-refresh
/// (#214) stays at the source-NAME level: hash comparison in <see cref="PrefixSourceService"/> over
/// this provider's (cache-served) result.</para>
/// </summary>
public sealed class AsnPrefixProvider : IPrefixSourceProvider
{
    private readonly RipeStatPrefixCache _cache;
    private readonly ILogger<AsnPrefixProvider> _logger;

    public AsnPrefixProvider(RipeStatPrefixCache cache, ILogger<AsnPrefixProvider> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public string Kind => "asn";

    /// <summary>
    /// RIPEstat does not support ETag/Last-Modified (#214) — every check re-fetches the full prefix
    /// list and relies on content-hash comparison. Returns false so the auto-refresh timer polls these
    /// sources at the longer <c>NoEtagIntervalSeconds</c> to avoid hammering RIPEstat.
    /// </summary>
    public bool SupportsConditionalRequests => false;

    public async Task<SourceLoadResult> LoadAsync(
        PrefixSourceConfig source,
        string? etag = null,
        DateTimeOffset? lastModified = null,
        CancellationToken ct = default)
    {
        if (!source.Asn.HasValue)
            throw new InvalidOperationException($"Prefix source '{source.Name}': Kind=asn requires an Asn.");

        // serveNegativeEntries: false (#377 review) — a cached failure must propagate (throw), not
        // come back as an empty list the name-level cache would store as a POSITIVE result.
        var prefixes = await _cache.GetPrefixesAsync(source.Asn.Value, ct, serveNegativeEntries: false);
        _logger.LogInformation(
            "Source '{Name}' (asn AS{Asn}): loaded {Count} prefixes via RIPEstat",
            source.Name, source.Asn.Value, prefixes.Count);
        return SourceLoadResult.Ok(prefixes.Select(p => (Prefix: p.Prefix, Length: p.Length)).ToList());
    }
}
