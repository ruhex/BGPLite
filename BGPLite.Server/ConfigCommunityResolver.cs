using System.Text;
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
    // Local ASN, captured for UserSource auto-generation (<Asn>:5XX).
    private readonly uint _asn;

    public ConfigCommunityResolver(AppConfig config, BgpConfig bgpConfig, ILogger<ConfigCommunityResolver>? logger = null)
    {
        _config = config;
        _logger = logger;
        _asn = bgpConfig.Asn;
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
        // User-supplied URL source (#143/#147): explicit Community overrides; otherwise auto-gen from
        // the reserved 500-599 range via a deterministic FNV-1a hash of the source name (stable across
        // restarts). Reuses ResolveStaticCommunity so an invalid explicit value falls back to the
        // auto-gen default and asn>0xFFFF yields Empty — identical semantics to Custom/CustomAsn.
        CommunitySourceKind.UserSource => ResolveStaticCommunity(
            source.Community, _asn, 500 + (int)(StableHash(source.ListName ?? "") % 100), "user-source community"),
        _ => Empty, // Default
    };

    private uint[] ParseOrDefault(string? raw) => string.IsNullOrWhiteSpace(raw) ? Empty : ParseCached(raw!);

    /// <summary>
    /// Deterministic 32-bit FNV-1a hash over the UTF-8 bytes of <paramref name="s"/>. Required because
    /// <see cref="string.GetHashCode()"/> is randomized per-process, which would make auto-generated
    /// user-source communities drift across restarts and silently break receiving peers' filters.
    /// </summary>
    private static uint StableHash(string s)
    {
        unchecked
        {
            uint hash = 2166136261u;
            foreach (var b in Encoding.UTF8.GetBytes(s))
            {
                hash ^= b;
                hash *= 16777619u;
            }
            return hash;
        }
    }

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
