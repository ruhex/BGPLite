using BGPLite.Configuration;

namespace BGPLite.Providers;

/// <summary>
/// Loads a static prefix list from a single kind of source (file, HTTP raw URL, ...).
/// Implementations are registered in DI and selected by <see cref="Kind"/> via
/// <see cref="PrefixSourceProviderFactory"/>. Add a new loading method by implementing
/// this interface and registering the implementation.
/// </summary>
public interface IPrefixSourceProvider
{
    /// <summary>Discriminator matched against <see cref="PrefixSourceConfig.Kind"/>.</summary>
    string Kind { get; }

    /// <summary>
    /// Fetch and parse the CIDR list described by <paramref name="source"/>. When
    /// <paramref name="etag"/> / <paramref name="lastModified"/> are provided (from a prior load),
    /// the provider SHOULD send a conditional request (If-None-Match / If-Modified-Since) and
    /// return <see cref="SourceLoadResult.NotModified"/> = true on a 304 (#214).
    /// </summary>
    Task<SourceLoadResult> LoadAsync(
        PrefixSourceConfig source,
        string? etag = null,
        DateTimeOffset? lastModified = null,
        CancellationToken ct = default);
}
