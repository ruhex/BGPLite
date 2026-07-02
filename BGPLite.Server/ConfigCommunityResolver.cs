using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Routing;
using Microsoft.Extensions.Logging;

namespace BGPLite.Server;

/// <summary>
/// Resolves communities from static config: <see cref="AsnList.Community"/> (for AsnList/Country)
/// and <see cref="PrefixSourceConfig.Community"/> (for PrefixSource, incl. the default source).
/// Parsed values are cached; malformed communities are logged and treated as none (never throw
/// during a peer send). <see cref="CommunitySourceKind.Custom"/>/<see cref="CommunitySourceKind.Default"/>
/// without a list name resolve to none in Phase 1.
/// </summary>
public sealed class ConfigCommunityResolver : ICommunityResolver
{
    private readonly AppConfig _config;
    private readonly ILogger<ConfigCommunityResolver>? _logger;
    private readonly Dictionary<string, uint[]> _parsed = new();
    private static readonly uint[] Empty = [];

    public ConfigCommunityResolver(AppConfig config, BgpConfig bgpConfig, ILogger<ConfigCommunityResolver>? logger = null)
    {
        _config = config;
        _logger = logger;
    }

    public uint[] Resolve(CommunitySource source)
    {
        var raw = source.Kind switch
        {
            CommunitySourceKind.AsnList => FindAsnListCommunity(source.ListName),
            CommunitySourceKind.Country => FindAsnListCommunity(source.ListName),
            CommunitySourceKind.PrefixSource => FindPrefixSourceCommunity(source.ListName ?? _config.DefaultPrefixSource),
            // Custom / Default carry no list identity in Phase 1 → untagged.
            _ => null,
        };
        return string.IsNullOrWhiteSpace(raw) ? Empty : ParseCached(raw!);
    }

    private string? FindAsnListCommunity(string? name) =>
        name is null ? null : _config.RipeStat?.AsnLists.FirstOrDefault(l => l.Name == name)?.Community;

    private string? FindPrefixSourceCommunity(string? name) =>
        name is null ? null : _config.PrefixSources.FirstOrDefault(s => s.Name == name)?.Community;

    private uint[] ParseCached(string community)
    {
        if (_parsed.TryGetValue(community, out var cached)) return cached;
        uint[] result;
        try
        {
            result = [CommunityCodec.Parse(community)];
        }
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
