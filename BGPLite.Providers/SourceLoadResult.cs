using BGPLite.Protocol;

namespace BGPLite.Providers;

/// <summary>
/// Result of loading a prefix source — carries the parsed prefixes plus the HTTP validators
/// (ETag / Last-Modified) that enable conditional re-fetches on subsequent loads (#214).
/// When <see cref="NotModified"/> is true, the server returned 304 Not Modified and the caller
/// should keep the existing cached data (just refresh the timestamp).
/// </summary>
public sealed record SourceLoadResult(
    IReadOnlyList<IpPrefix> Prefixes,
    string? ETag = null,
    DateTimeOffset? LastModified = null,
    bool NotModified = false)
{
    /// <summary>Convenience for a normal (200 OK) load — prefixes + validators from the response.</summary>
    public static SourceLoadResult Ok(IReadOnlyList<IpPrefix> prefixes, string? etag = null, DateTimeOffset? lastModified = null) =>
        new(prefixes, etag, lastModified, NotModified: false);

    /// <summary>Convenience for a 304 Not Modified response — no prefixes, caller keeps cached data.</summary>
    public static SourceLoadResult NotModifiedResult(string? etag = null, DateTimeOffset? lastModified = null) =>
        new([], etag, lastModified, NotModified: true);
}
