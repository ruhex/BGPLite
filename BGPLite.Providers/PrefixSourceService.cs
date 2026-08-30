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
    // #214 convergence: invoked whenever a load detects an actual content change, regardless of which
    // entry point triggered the load (connect-path GetAsync vs. auto-refresh RefreshAsync). Without this,
    // a connect-path load that silently updated the cache would mask the change from a subsequent
    // RefreshAsync (which would see already-new data and report unchanged) — leaving established peers
    // on stale routes. The callback is wired in Program.cs to ISessionManager.RefreshAllEstablishedAsync.
    private readonly Func<string, Task>? _onSourceChanged;
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
        TimeProvider? timeProvider = null,
        Func<string, Task>? onSourceChanged = null)
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
        _onSourceChanged = onSourceChanged;
        _sourcesByName = config.PrefixSources.ToDictionary(s => s.Name);
    }

    public async Task<IReadOnlyList<(uint Prefix, byte Length)>> GetAsync(string name, CancellationToken ct = default)
    {
        if (!_sourcesByName.TryGetValue(name, out var source))
        {
            _logger.LogWarning("Prefix source '{Name}' not found in configuration.", name);
            return [];
        }

        try { return (await LoadCachedAsync(source, ct)).Prefixes; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }  // #324: only CALLER cancellation — the #324 default fetch budget fires as a foreign-token OCE (live ct) and must stay a per-source failure below
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

        try { return (await LoadCachedAsync(source, ct)).Prefixes; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }  // #324: only CALLER cancellation — the #324 default fetch budget fires as a foreign-token OCE (live ct) and must stay a per-source failure below
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
            try { prefixes = (await LoadCachedAsync(source, ct)).Prefixes; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }  // #324: only CALLER cancellation — the #324 default fetch budget fires as a foreign-token OCE (live ct) and must stay a per-source failure below
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
            _logger.LogInformation("WarmUp: source '{Name}' — {Count} prefixes", source.Name, prefixes.Count);
    }

    /// <summary>
    /// #214: Force-refresh a single source, bypassing the TTL. Returns whether the content actually
    /// changed. Used by the auto-refresh timer, which polls each source on its own interval (jittered
    /// between sources) — so the timer owns timing AND the peer push (LoopAsync aggregates all changed
    /// sources into ONE RefreshAllEstablishedAsync call), this method owns only the atomic load+compare.
    /// Therefore it does NOT fire the onSourceChanged callback — that would cause a double push (once
    /// per changed source here, once aggregated in LoopAsync). The connect-path (GetAsync) DOES fire
    /// the callback, because it is not part of the auto-refresh aggregation.
    /// </summary>
    public async Task<bool> RefreshAsync(string sourceName, CancellationToken ct = default)
    {
        if (!_sourcesByName.TryGetValue(sourceName, out var source))
        {
            _logger.LogWarning("Auto-refresh: source '{Name}' not found in configuration.", sourceName);
            return false;
        }
        try
        {
            var (_, changed) = await LoadCachedAsync(source, ct, forceRefresh: true, triggerCallback: false);
            return changed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }  // #324: only CALLER cancellation — the #324 default fetch budget fires as a foreign-token OCE (live ct) and must stay a per-source failure below
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-refresh: failed to reload source '{Name}'.", source.Name);
            return false;
        }
    }

    /// <summary>
    /// #214: Whether the source supports conditional requests (ETag/Last-Modified). The auto-refresh
    /// timer uses this to pick the poll interval: conditional sources poll at <c>IntervalSeconds</c>
    /// (304s are cheap), non-conditional at the longer <c>NoEtagIntervalSeconds</c>.
    /// </summary>
    public bool SourceSupportsConditional(string sourceName)
    {
        if (!_sourcesByName.TryGetValue(sourceName, out var source))
            return true; // conservative: treat unknown as conditional (short interval)
        try { return _factory.Get(source.Kind).SupportsConditionalRequests; }
        catch { return true; } // unknown Kind — conservative default
    }

    /// <summary>
    /// Loads <paramref name="source"/> through the cache, returning the prefix list plus whether the
    /// content actually changed on this load (#214). <paramref name="forceRefresh"/> bypasses the TTL
    /// (used by the auto-refresh timer). <c>Changed</c> is computed INSIDE the per-source gate —
    /// atomically with the cache write — so a concurrent <c>GetAsync</c> cannot insert a newer list
    /// between the before/after snapshots (the prior TOCTOU that masked real changes). When a change
    /// is detected (by any entry point), <c>_onSourceChanged</c> is invoked AFTER releasing the gate
    /// so the callback (peer refresh) can't deadlock on the per-source lock.
    /// </summary>
    private async Task<(IReadOnlyList<(uint Prefix, byte Length)> Prefixes, bool Changed)> LoadCachedAsync(
        PrefixSourceConfig source, CancellationToken ct, bool forceRefresh = false, bool triggerCallback = true)
    {
        if (!forceRefresh && TryGetFresh(source.Name, out var fresh))
            return (fresh, Changed: false);

        var gate = _locks.GetOrAdd(source.Name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        bool changed;
        IReadOnlyList<(uint Prefix, byte Length)> loaded;
        try
        {
            if (!forceRefresh && TryGetFresh(source.Name, out var rechecked))
                return (rechecked, Changed: false);

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
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }  // #324: only CALLER cancellation — the #324 default fetch budget fires as a foreign-token OCE (live ct) and must stay a per-source failure below
            catch
            {
                if (_cache.TryGetValue(source.Name, out var staleCopy) && !staleCopy.Negative)
                {
                    _logger.LogWarning("Source '{Name}' load failed; serving cached copy ({Count} prefixes).",
                        source.Name, staleCopy.List.Count);
                    // Serving stale: no content change relative to what's already cached.
                    return (staleCopy.List, Changed: false);
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
                    return (existing.List, Changed: false);
                }
                // No prior data (first load got 304?) — treat as empty, no change.
                _cache[source.Name] = new CacheEntry([], _timeProvider.GetUtcNow().UtcDateTime, false,
                    result.ETag, result.LastModified);
                return ([], Changed: false);
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            // Compute Changed atomically with the cache write: compare the freshly loaded list with the
            // list that was in the cache before this load (captured above as `stale`, re-read here to
            // be safe under the gate — only this task holds the gate, so `stale` is still authoritative).
            // Order-INDEPENDENT comparison: RIPEstat (and some HTTP sources) may return the same prefix
            // set in a different order between requests — a SequenceEqual there would report a phantom
            // change and trigger an unnecessary BGP re-announcement (#214: "no unnecessary BGP churn",
            // AsnPrefixProvider docs describe the design as content-based, not positional).
            var previousList = (stale is not null && !stale.Negative) ? stale.List : null;
            changed = previousList is null || !SamePrefixes(previousList, result.Prefixes);
            _cache[source.Name] = new CacheEntry(result.Prefixes, now, false, result.ETag, result.LastModified);
            loaded = result.Prefixes;
        }
        finally
        {
            gate.Release();
        }

        // #214 convergence: fire the change callback AFTER releasing the gate, so a connect-path load
        // (GetAsync) that updates the cache also notifies established peers — not just the auto-refresh
        // path. Without this, established peers stay on stale routes until the source changes AGAIN.
        // Skipped from RefreshAsync (triggerCallback=false): the auto-refresh timer aggregates all
        // changed sources into ONE peer push in LoopAsync, so firing the callback here would double-push.
        if (changed && triggerCallback && _onSourceChanged is not null)
        {
            try { await _onSourceChanged(source.Name); }
            catch (Exception ex) { _logger.LogWarning(ex, "OnSourceChanged callback failed for '{Name}'.", source.Name); }
        }

        return (loaded, changed);
    }

    /// <summary>
    /// Order-independent prefix-list equality (#214): two lists represent the same route set if they
    /// contain the same (Prefix, Length) tuples regardless of order. RIPEstat and some HTTP sources
    /// do not guarantee a stable ordering across requests — a positional SequenceEqual there would
    /// report a phantom change and trigger an unnecessary BGP re-announcement. Sorting both lists
    /// (O(n log n)) keeps duplicates significant (a repeated prefix stays a difference) and avoids
    /// the HashSet allocation path on the common unchanged case via the fast count/SequenceEqual
    /// shortcut when the source already returned a sorted list.
    /// </summary>
    private static bool SamePrefixes(IReadOnlyList<(uint Prefix, byte Length)> a, IReadOnlyList<(uint Prefix, byte Length)> b)
    {
        if (a.Count != b.Count) return false;
        // Fast path: if both happen to be in the same order (common — file sources, cached HTTP),
        // SequenceEqual is O(n) with no allocation.
        if (a.SequenceEqual(b)) return true;
        // Slow path: order differs — compare as sorted sequences.
        var sa = a.OrderBy(x => x.Prefix).ThenBy(x => x.Length).ToArray();
        var sb = b.OrderBy(x => x.Prefix).ThenBy(x => x.Length).ToArray();
        return sa.SequenceEqual(sb);
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
