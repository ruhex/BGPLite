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

    public async Task<SourceLoadResult> LoadAsync(
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
            return SourceLoadResult.NotModifiedResult(lastModified: fileMtime);
        }

        // #321 item 5: async read with the caller's token — the sync ReadAllText held a threadpool
        // thread under the per-source gate for the whole file. The metadata probes above stay
        // sync: they are single stat calls, not reads.
        // #487: cap parity with the HTTP paths (HttpPrefixProvider.MaxResponseBytes) — the file is
        // operator-controlled, but a multi-GB "prefix list" is never legitimate and the uncapped
        // read was an asymmetric memory-amplification footgun.
        var length = new FileInfo(fullPath).Length;
        if (length > HttpPrefixProvider.MaxResponseBytes)
            throw new InvalidOperationException(
                $"Prefix file for source '{source.Name}' is {length} bytes — over the {HttpPrefixProvider.MaxResponseBytes}-byte cap.");
        var text = await File.ReadAllTextAsync(fullPath, ct);
        var prefixes = PrefixListParser.Parse(text);
        logger.LogInformation("Source '{Name}' (file): loaded {Count} prefixes from {Path}", source.Name, prefixes.Count, fullPath);
        return SourceLoadResult.Ok(prefixes, lastModified: fileMtime);
    }
}
