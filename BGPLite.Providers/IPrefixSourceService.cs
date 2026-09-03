using BGPLite.Configuration;
using BGPLite.Protocol;

namespace BGPLite.Providers;

/// <summary>
/// Orchestrates configured <see cref="PrefixSourceConfig"/> entries: loads them through the
/// provider factory, caches results in memory with a TTL, and resolves the designated default
/// source used as the RU/fallback prefix set for unconfigured peers.
/// </summary>
public interface IPrefixSourceService
{
    /// <summary>All configured sources with their cached prefix lists.</summary>
    Task<IReadOnlyList<(PrefixSourceConfig Source, IReadOnlyList<IpPrefix> Prefixes)>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>One source by name (cache-through). Empty list if missing or failed.</summary>
    Task<IReadOnlyList<IpPrefix>> GetAsync(string name, CancellationToken ct = default);

    /// <summary>The source named by <c>AppConfig.DefaultPrefixSource</c>. Empty list if unset/missing.</summary>
    Task<IReadOnlyList<IpPrefix>> GetDefaultAsync(CancellationToken ct = default);

    /// <summary>
    /// #416: callback-free counterpart of <see cref="GetDefaultAsync"/> — loads the default source
    /// and reports whether the content actually changed, but NEVER fires the
    /// <c>onSourceChanged</c> convergence callback. Callers that invoke it while holding a lock
    /// (the RU gate in <c>PrefixService.GetRuPrefixesAsync</c>) must own the push themselves and
    /// fire it only AFTER releasing the lock: an inline callback that re-enters the RU path from
    /// another session's build deadlocked on the caller-held gate (the gate holder awaited the
    /// push, the push awaited the gate).
    /// </summary>
    Task<(IReadOnlyList<IpPrefix> Prefixes, bool Changed)> LoadDefaultAsync(CancellationToken ct = default);

    /// <summary>Prime the in-memory cache for all sources.</summary>
    Task WarmUpAsync(CancellationToken ct = default);

    /// <summary>
    /// #214: Force-refresh a single source (bypass TTL), returning whether the content actually
    /// changed. Used by the auto-refresh timer, which polls each source on its own jittered interval.
    /// </summary>
    Task<bool> RefreshAsync(string sourceName, CancellationToken ct = default);

    /// <summary>
    /// #214: Whether the source supports conditional requests (ETag/Last-Modified). Drives the poll
    /// interval selection in the auto-refresh timer.
    /// </summary>
    bool SourceSupportsConditional(string sourceName);
}
