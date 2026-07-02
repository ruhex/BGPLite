using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Routing;
using Microsoft.Extensions.Logging;

namespace BGPLite.Server;

/// <summary>
/// Resolves communities from static config: <see cref="AsnList.Community"/> (AsnList/Country),
/// <see cref="PrefixSourceConfig.Community"/> (PrefixSource, incl. the default source), and two
/// static per-category communities for the per-peer custom paths — custom prefixes (<c>&lt;Asn&gt;:100</c>)
/// and custom AS (<c>&lt;Asn&gt;:200</c>), each overridable via <see cref="AppConfig.CustomPrefixCommunity"/>
/// / <see cref="AppConfig.CustomAsnCommunity"/>. Malformed values are logged and fall back to the
/// default (never throw during a peer send).
/// </summary>
public sealed class ConfigCommunityResolver : ICommunityResolver
{
    private readonly AppConfig _config;
    private readonly ILogger<ConfigCommunityResolver>? _logger;
    private readonly Dictionary<string, uint[]> _parsed = new();
    private static readonly uint[] Empty = [];

    // Static communities for the per-peer custom categories, resolved once in the ctor.
    private readonly uint[] _customPrefixComms;
    private readonly uint[] _customAsnComms;

    public ConfigCommunityResolver(AppConfig config, BgpConfig bgpConfig, ILogger<ConfigCommunityResolver>? logger = null)
    {
        _config = config;
        _logger = logger;
        _customPrefixComms = ResolveStaticCommunity(config.CustomPrefixCommunity, bgpConfig.Asn, 100, nameof(config.CustomPrefixCommunity));
        _customAsnComms = ResolveStaticCommunity(config.CustomAsnCommunity, bgpConfig.Asn, 200, nameof(config.CustomAsnCommunity));
    }

    public uint[] Resolve(CommunitySource source) => source.Kind switch
    {
        CommunitySourceKind.AsnList => ParseOrDefault(FindAsnListCommunity(source.ListName)),
        CommunitySourceKind.Country => ParseOrDefault(FindAsnListCommunity(source.ListName)),
        CommunitySourceKind.PrefixSource => ParseOrDefault(FindPrefixSourceCommunity(source.ListName ?? _config.DefaultPrefixSource)),
        CommunitySourceKind.Custom => _customPrefixComms,
        CommunitySourceKind.CustomAsn => _customAsnComms,
        _ => Empty, // Default
    };

    private uint[] ParseOrDefault(string? raw) => string.IsNullOrWhiteSpace(raw) ? Empty : ParseCached(raw!);

    private string? FindAsnListCommunity(string? name) =>
        name is null ? null : _config.RipeStat?.AsnLists.FirstOrDefault(l => l.Name == name)?.Community;

    private string? FindPrefixSourceCommunity(string? name) =>
        name is null ? null : _config.PrefixSources.FirstOrDefault(s => s.Name == name)?.Community;

    /// <summary>
    /// Static category community: the config override if set and valid, otherwise the hardcoded
    /// default <c>"&lt;Asn&gt;:&lt;defaultValue&gt;"</c>. Returns empty if the local ASN exceeds 16 bits.
    /// </summary>
    private uint[] ResolveStaticCommunity(string? overrideValue, uint asn, int defaultValue, string fieldName)
    {
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            try { return [CommunityCodec.Parse(overrideValue!)]; }
            catch (FormatException ex)
            {
                _logger?.LogWarning(ex,
                    "Invalid community '{Community}' in config ({Field}); falling back to default {Asn}:{Default}.",
                    overrideValue, fieldName, asn, defaultValue);
            }
        }
        if (asn > 0xFFFF)
        {
            _logger?.LogWarning("Local ASN {Asn} > 65535; cannot form the default {Field} community '{Asn}:{Default}' — these prefixes will be untagged.",
                asn, fieldName, asn, defaultValue);
            return Empty;
        }
        return [(asn << 16) | (uint)(defaultValue & 0xFFFF)];
    }

    private uint[] ParseCached(string community)
    {
        if (_parsed.TryGetValue(community, out var cached)) return cached;
        uint[] result;
        try { result = [CommunityCodec.Parse(community)]; }
        catch (FormatException ex)
        {
            _logger?.LogWarning(ex,
                "Invalid community '{Community}' in config; prefixes from this list will be advertised untagged.",
                community);
            result = Empty;
        }
        _parsed[community] = result;
        return result;
    }
}
