using BGPLite.Configuration;

namespace BGPLite.Providers;

/// <summary>
/// Orchestrates configured <see cref="PrefixSourceConfig"/> entries: loads them through the
/// provider factory, caches results in memory with a TTL, and resolves the designated default
/// source used as the RU/fallback prefix set for unconfigured peers.
/// </summary>
public interface IPrefixSourceService
{
    /// <summary>All configured sources with their cached prefix lists.</summary>
    Task<IReadOnlyList<(PrefixSourceConfig Source, IReadOnlyList<(uint Prefix, byte Length)> Prefixes)>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>One source by name (cache-through). Empty list if missing or failed.</summary>
    Task<IReadOnlyList<(uint Prefix, byte Length)>> GetAsync(string name, CancellationToken ct = default);

    /// <summary>The source named by <c>AppConfig.DefaultPrefixSource</c>. Empty list if unset/missing.</summary>
    Task<IReadOnlyList<(uint Prefix, byte Length)>> GetDefaultAsync(CancellationToken ct = default);

    /// <summary>Prime the in-memory cache for all sources.</summary>
    Task WarmUpAsync(CancellationToken ct = default);
}
