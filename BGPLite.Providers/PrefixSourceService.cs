using System.Collections.Concurrent;
using BGPLite.Configuration;
using Microsoft.Extensions.Logging;

namespace BGPLite.Providers;

/// <summary>
/// Loads configured <see cref="PrefixSourceConfig"/> entries through the provider factory,
/// keeping results in an in-memory TTL cache. Per-source failures fall back to a stale cached
/// copy (if any) or a short-lived negative entry, never breaking startup. The source named by
/// <see cref="AppConfig.DefaultPrefixSource"/> is exposed as the RU/default set.
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

    // Name → (a prefix list, cached at, is negative). Negative entries (failed loads) use _negativeTtl.
    private readonly ConcurrentDictionary<string, (IReadOnlyList<(uint Prefix, byte Length)> List, DateTime CachedAt, bool Negative)> _cache = new();
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
        // #85: pre-build a name→source lookup (the ctor already iterates for duplicate detection).
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

    private async Task<IReadOnlyList<(uint Prefix, byte Length)>> LoadCachedAsync(PrefixSourceConfig source, CancellationToken ct)
    {
        if (TryGetFresh(source.Name, out var fresh))
            return fresh;

        // Serialize per-key so concurrent callers share a single fetch (no thundering herd).
        var gate = _locks.GetOrAdd(source.Name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (TryGetFresh(source.Name, out var rechecked))
                return rechecked;

            IReadOnlyList<(uint Prefix, byte Length)> prefixes;
            try
            {
                var provider = _factory.Get(source.Kind);
                prefixes = await provider.LoadAsync(source, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Serve the last good copy if we have one (regardless of its age).
                if (_cache.TryGetValue(source.Name, out var stale) && !stale.Negative)
                {
                    _logger.LogWarning("Source '{Name}' load failed; serving cached copy ({Count} prefixes).",
                        source.Name, stale.List.Count);
                    return stale.List;
                }

                // Otherwise remember the failure briefly, so repeated calls don't hammer the provider.
                _cache[source.Name] = ([], _timeProvider.GetUtcNow().UtcDateTime, Negative: true);
                throw;
            }

            _cache[source.Name] = (prefixes, _timeProvider.GetUtcNow().UtcDateTime, Negative: false);
            return prefixes;
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
}
