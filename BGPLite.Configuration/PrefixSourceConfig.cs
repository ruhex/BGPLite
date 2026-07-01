using YamlDotNet.Serialization;

namespace BGPLite.Configuration;

/// <summary>
/// A configurable prefix source. <see cref="Kind"/> selects the loader implementation
/// via <c>PrefixSourceProviderFactory</c> (e.g. <c>"file"</c>, <c>"http"</c>).
/// </summary>
public sealed class PrefixSourceConfig
{
    /// <summary>Loader kind: <c>"file"</c> (reads <see cref="Path"/>) or <c>"http"</c> (fetches <see cref="Url"/>).</summary>
    [YamlMember(Alias = "Kind")]
    public string Kind { get; init; } = "file";

    [YamlMember(Alias = "Name")]
    public string Name { get; init; } = "";

    [YamlMember(Alias = "Description")]
    public string? Description { get; init; }

    /// <summary>Optional community in <c>"ASN:VALUE"</c> form attached to every prefix from this source.</summary>
    [YamlMember(Alias = "Community")]
    public string? Community { get; init; }

    /// <summary>Raw URL of a CIDR list (kind = <c>"http"</c>).</summary>
    [YamlMember(Alias = "Url")]
    public string? Url { get; init; }
    
    /// <summary>AS number(s) this prefix source is valid for (kind = <c>"asn"</c>).</summary>
    [YamlMember(Alias = "Asn")]
    public uint? Asn { get; init; }

    /// <summary>Local file path, relative to <c>AppContext.BaseDirectory</c> (kind = <c>"file"</c>).</summary>
    [YamlMember(Alias = "Path")]
    public string? Path { get; init; }

    /// <summary>Per-source HTTP timeout in seconds (kind = <c>"http"</c>). Overrides the default when set.</summary>
    [YamlMember(Alias = "Timeout")]
    public int? Timeout { get; init; }

    /// <summary>Per-source HTTP request headers, e.g. <c>Authorization</c> / <c>X-API-Key</c> (kind = <c>"http"</c>).</summary>
    [YamlMember(Alias = "Headers")]
    public Dictionary<string, string>? Headers { get; init; }
}
