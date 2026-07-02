namespace BGPLite.Routing;

/// <summary>Kind of prefix source a route originated from, used to resolve its BGP community/communities.</summary>
public enum CommunitySourceKind
{
    /// <summary>A configured <c>AsnList</c> (RIPEstat ASN subscription).</summary>
    AsnList,
    /// <summary>A configured prefix source (<c>PrefixSources</c>, file/http).</summary>
    PrefixSource,
    /// <summary>A country-based <c>AsnList</c> (resolved like an AsnList by name).</summary>
    Country,
    /// <summary>Per-peer custom prefixes (static community <c>&lt;Asn&gt;:100</c>, overridable via config).</summary>
    Custom,
    /// <summary>Per-peer custom AS-originated prefixes (static community <c>&lt;Asn&gt;:200</c>, overridable via config).</summary>
    CustomAsn,
    /// <summary>Fallback / default source when nothing else applies.</summary>
    Default
}

/// <summary>
/// Identifies where a prefix came from so a community can be resolved for it.
/// <c>ListName</c> carries the list/source name for <see cref="CommunitySourceKind.AsnList"/>,
/// <see cref="CommunitySourceKind.Country"/>, <see cref="CommunitySourceKind.PrefixSource"/>
/// (and the default-source name for <see cref="CommunitySourceKind.Default"/>).
/// </summary>
public sealed record CommunitySource(CommunitySourceKind Kind, string? ListName = null);

/// <summary>
/// Resolves the BGP community/communities to attach to prefixes that came from a given source.
/// Returns an empty array when the source has no community (callers must treat empty as "untagged").
/// Implementations: <see cref="ConfigCommunityResolver"/> (static config); a future DB-backed
/// resolver for named user lists (Phase 2).
/// </summary>
public interface ICommunityResolver
{
    /// <summary>Returns the community value(s) for the given source, or an empty array if none.</summary>
    uint[] Resolve(CommunitySource source);
}

/// <summary>Resolves to no communities. Default when no resolver is wired up.</summary>
public sealed class NullCommunityResolver : ICommunityResolver
{
    public static NullCommunityResolver Instance { get; } = new();
    public uint[] Resolve(CommunitySource source) => [];
}
