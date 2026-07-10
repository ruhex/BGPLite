using System.Collections.Concurrent;
using BGPLite.Configuration;
using Microsoft.Extensions.Logging;

namespace BGPLite.Providers;

/// <summary>
/// Loads configured <see cref="PrefixSourceConfig"/> entries through the provider factory,
/// keeping results in an in-memory TTL cache. Per-source failures fall back to a stale cached
/// copy (if any) or a short-lived negative entry, never breaking startup. The source named by
/// <see cref="AppConfig.DefaultPrefixSource"/> is exposed as the RU/default set.
/// <para>#214: stores ETag/Last-Modified per source for conditional re-fetches (304 Not Modified).</para>
/// </summary>
public sealed class PrefixSourceService : IPrefixSourceService
{
    private readonly AppConfig _config;
    private readonly PrefixSourceProviderFactory _factory;
    private readonly ILogger<PrefixSourceService> _logger;
    private readonly TimeSpan _cacheTtl;
    private readonly TimeSpan _negativeTtl;
    private readonly TimeProvider _timeProvider;
    // #85: pre-built name→source lookup (replaces per-call FirstOrDefault linear scan).
    private readonly Dictionary<string, PrefixSourceConfig> _sourcesByName;

    // Name → (prefix list, cached at, is negative, ETag, LastModified). Negative entries use _negativeTtl.
    // #214: ETag/LastModified enable conditional re-fetches (If-None-Match / If-Modified-Since → 304).
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    // Name → gate serializing the cache-miss fetch path (prevents thundering-herd on cold/expired keys).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public PrefixSourceService(
        AppConfig config,
        PrefixSourceProviderFactory factory,
        ILogger<PrefixSourceService> logger,
        TimeSpan? cacheTtl = null,
        TimeSpan? negativeTtl = null,
        TimeProvider? timeProvider = null)
    {
        var duplicate = config.PrefixSources
            .GroupBy(s => s.Name)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException(
                $"Duplicate prefix source name '{duplicate.Key}'. Each PrefixSources entry must have a unique Name.");

        _config = config;
        _factory = factory;
        _logger = logger;
        _cacheTtl = cacheTtl ?? TimeSpan.FromHours(1);
        _negativeTtl = negativeTtl ?? TimeSpan.FromSeconds(30);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sourcesByName = config.PrefixSources.ToDictionary(s => s.Name);
    }

    public async Task<IReadOnlyList<(uint Prefix, byte Length)>> GetAsync(string name, CancellationToken ct = default)
    {
        if (!_sourcesByName.TryGetValue(name, out var source))
        {
            _logger.LogWarning("Prefix source '{Name}' not found in configuration.", name);
            return [];
        }

        try { return await LoadCachedAsync(source, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load prefix source '{Name}'.", name);
            return [];
        }
    }

    public async Task<IReadOnlyList<(uint Prefix, byte Length)>> GetDefaultAsync(CancellationToken ct = default)
    {
        var defaultName = _config.DefaultPrefixSource;
        if (string.IsNullOrWhiteSpace(defaultName))
            return [];

        if (!_sourcesByName.TryGetValue(defaultName, out var source))
        {
            _logger.LogWarning("DefaultPrefixSource '{Name}' does not match any configured source.", defaultName);
            return [];
        }

        try { return await LoadCachedAsync(source, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load default prefix source '{Name}'.", defaultName);
            return [];
        }
    }

    public async Task<IReadOnlyList<(PrefixSourceConfig Source, IReadOnlyList<(uint Prefix, byte Length)> Prefixes)>> LoadAllAsync(CancellationToken ct = default)
    {
        var result = new List<(PrefixSourceConfig Source, IReadOnlyList<(uint Prefix, byte Length)> Prefixes)>();
        foreach (var source in _config.PrefixSources)
        {
            IReadOnlyList<(uint Prefix, byte Length)> prefixes;
            try { prefixes = await LoadCachedAsync(source, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load prefix source '{Name}' ({Kind}).", source.Name, source.Kind);
                prefixes = [];
            }
            result.Add((source, prefixes));
        }
        return result;
    }

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        foreach (var (source, prefixes) in await LoadAllAsync(ct))
            Console.WriteLine($"  WarmUp: source '{source.Name}' — {prefixes.Count} prefixes");
    }

    /// <summary>
    /// #214: Force-refresh all configured sources, bypassing the TTL. Used by the auto-refresh timer.
    /// Returns the set of source names whose content actually changed (for selective peer refresh).
    /// </summary>
    public async Task<HashSet<string>> RefreshAllAsync(CancellationToken ct = default)
    {
        var changed = new HashSet<string>();
        foreach (var source in _config.PrefixSources)
        {
            try
            {
                var before = _cache.TryGetValue(source.Name, out var entry) ? entry : null;
                await LoadCachedAsync(source, ct, forceRefresh: true);
                if (_cache.TryGetValue(source.Name, out var after) && after.List is not null)
                {
                    if (before is null || before.List is null || !before.List.SequenceEqual(after.List))
                        changed.Add(source.Name);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Auto-refresh: failed to reload source '{Name}'.", source.Name);
            }
        }
        return changed;
    }

    private async Task<IReadOnlyList<(uint Prefix, byte Length)>> LoadCachedAsync(PrefixSourceConfig source, CancellationToken ct, bool forceRefresh = false)
    {
        if (!forceRefresh && TryGetFresh(source.Name, out var fresh))
            return fresh;

        var gate = _locks.GetOrAdd(source.Name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && TryGetFresh(source.Name, out var rechecked))
                return rechecked;

            // #214: read stale validators for conditional request.
            string? etag = null;
            DateTimeOffset? lastModified = null;
            if (_cache.TryGetValue(source.Name, out var stale) && !stale.Negative)
            {
                etag = stale.ETag;
                lastModified = stale.LastModified;
            }

            SourceLoadResult result;
            try
            {
                var provider = _factory.Get(source.Kind);
                result = await provider.LoadAsync(source, etag, lastModified, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                if (_cache.TryGetValue(source.Name, out var staleCopy) && !staleCopy.Negative)
                {
                    _logger.LogWarning("Source '{Name}' load failed; serving cached copy ({Count} prefixes).",
                        source.Name, staleCopy.List.Count);
                    return staleCopy.List;
                }
                _cache[source.Name] = new CacheEntry([], _timeProvider.GetUtcNow().UtcDateTime, true);
                throw;
            }

            // #214: 304 Not Modified — keep existing data, just refresh the timestamp + validators.
            if (result.NotModified)
            {
                if (_cache.TryGetValue(source.Name, out var existing) && !existing.Negative)
                {
                    _cache[source.Name] = existing with
                    {
                        CachedAt = _timeProvider.GetUtcNow().UtcDateTime,
                        ETag = result.ETag ?? existing.ETag,
                        LastModified = result.LastModified ?? existing.LastModified
                    };
                    return existing.List;
                }
                // No prior data (first load got 304?) — treat as empty.
                _cache[source.Name] = new CacheEntry([], _timeProvider.GetUtcNow().UtcDateTime, false,
                    result.ETag, result.LastModified);
                return [];
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            _cache[source.Name] = new CacheEntry(result.Prefixes, now, false, result.ETag, result.LastModified);
            return result.Prefixes;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryGetFresh(string name, out IReadOnlyList<(uint Prefix, byte Length)> list)
    {
        list = null!;
        if (!_cache.TryGetValue(name, out var entry)) return false;

        var ttl = entry.Negative ? _negativeTtl : _cacheTtl;
        if (_timeProvider.GetUtcNow().UtcDateTime - entry.CachedAt < ttl)
        {
            list = entry.List;
            return true;
        }
        return false;
    }

    /// <summary>#214: Cache entry with ETag/Last-Modified for conditional re-fetches.</summary>
    private sealed record CacheEntry(
        IReadOnlyList<(uint Prefix, byte Length)> List,
        DateTime CachedAt,
        bool Negative,
        string? ETag = null,
        DateTimeOffset? LastModified = null);
}
