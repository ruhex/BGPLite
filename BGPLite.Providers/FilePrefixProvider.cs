using BGPLite.Configuration;
using Microsoft.Extensions.Logging;

namespace BGPLite.Providers;

/// <summary>Loads prefixes from a local CIDR file (Kind = <c>"file"</c>).
/// Conditional reload (#214): when <paramref name="lastModified"/> matches the file's
/// <c>LastWriteTimeUtc</c>, returns <see cref="SourceLoadResult.NotModified"/> = true (no re-parse).</summary>
public sealed class FilePrefixProvider(ILogger<FilePrefixProvider> logger) : IPrefixSourceProvider
{
    public string Kind => "file";

    /// <summary>File mtime comparison is a zero-cost conditional check (#214).</summary>
    public bool SupportsConditionalRequests => true;

    public Task<SourceLoadResult> LoadAsync(
        PrefixSourceConfig source,
        string? etag = null,
        DateTimeOffset? lastModified = null,
        CancellationToken ct = default)
    {
        var path = source.Path;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException($"Prefix source '{source.Name}': Kind=file requires a Path.");

        var fullPath = Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Prefix file not found for source '{source.Name}': {fullPath}", fullPath);

        // #214: conditional check — if the file hasn't been modified since the last load, skip re-parsing.
        var fileMtime = new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath), TimeSpan.Zero);
        if (lastModified is not null && fileMtime == lastModified)
        {
            logger.LogDebug("Source '{Name}' (file): unchanged (mtime match)", source.Name);
            return Task.FromResult(SourceLoadResult.NotModifiedResult(lastModified: fileMtime));
        }

        var prefixes = PrefixListParser.Parse(File.ReadAllText(fullPath));
        logger.LogInformation("Source '{Name}' (file): loaded {Count} prefixes from {Path}", source.Name, prefixes.Count, fullPath);
        return Task.FromResult(SourceLoadResult.Ok(prefixes, lastModified: fileMtime));
    }
}
