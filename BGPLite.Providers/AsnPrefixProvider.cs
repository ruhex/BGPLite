using BGPLite.Configuration;
using Microsoft.Extensions.Logging;

namespace BGPLite.Providers;

/// <summary>
/// Loads the prefixes originated by a single AS via RIPEstat (Kind = <c>"asn"</c>).
/// Requires <see cref="PrefixSourceConfig.Asn"/>. Shares the RIPEstat client, retry, and per-ASN
/// caching of <see cref="RipeStatProvider"/>/PrefixService, so adding it as a <c>Kind: asn</c>
/// source under <c>PrefixSources</c> does not multiply RIPEstat traffic.
/// </summary>
public sealed class AsnPrefixProvider : IPrefixSourceProvider
{
    private readonly RipeStatProvider _ripe;
    private readonly ILogger<AsnPrefixProvider> _logger;

    public AsnPrefixProvider(RipeStatProvider ripe, ILogger<AsnPrefixProvider> logger)
    {
        _ripe = ripe;
        _logger = logger;
    }

    public string Kind => "asn";

    public async Task<IReadOnlyList<(uint Prefix, byte Length)>> LoadAsync(PrefixSourceConfig source, CancellationToken ct = default)
    {
        if (!source.Asn.HasValue)
            throw new InvalidOperationException($"Prefix source '{source.Name}': Kind=asn requires an Asn.");

        var prefixes = await _ripe.GetPrefixesAsync(source.Asn.Value, ct);
        _logger.LogInformation(
            "Source '{Name}' (asn AS{Asn}): loaded {Count} prefixes via RIPEstat",
            source.Name, source.Asn.Value, prefixes.Count);
        return prefixes.Select(p => (Prefix: p.Prefix, Length: p.PrefixLength)).ToList();
    }
}
