using System.Collections.Concurrent;
using BGPLite.Configuration;
using BGPLite.Server;

namespace BGPLite.Providers;

public sealed class PrefixService : IPrefixService
{
    private readonly RipeStatProvider? _ripeStat;
    private readonly IPrefixSourceService _prefixSources;
    private readonly AppConfig _config;
    private readonly ConcurrentDictionary<uint, (IReadOnlyList<(uint Prefix, byte Length)> Data, DateTime CachedAt)> _cache = new();
    private readonly TimeSpan _cacheTtl;

    public PrefixService(AppConfig config, RipeStatProvider? ripeStat, IPrefixSourceService prefixSources, TimeSpan? cacheTtl = null)
    {
        _config = config;
        _ripeStat = ripeStat;
        _prefixSources = prefixSources;
        _cacheTtl = cacheTtl ?? TimeSpan.FromHours(1);
    }

    public async Task<IReadOnlyList<(uint Prefix, byte Length)>> GetPrefixesAsync(uint asn, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(asn, out var cached) && DateTime.UtcNow - cached.CachedAt < _cacheTtl)
            return cached.Data;

        if (_ripeStat is null) return [];

        var prefixes = await _ripeStat.GetPrefixesAsync(asn, ct);
        _cache[asn] = (prefixes, DateTime.UtcNow);
        return prefixes;
    }

    public async Task<List<(uint Prefix, byte Length, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default)
    {
        var result = new List<(uint Prefix, byte Length, uint Asn)>();
        foreach (var asn in asns)
        {
            try
            {
                var prefixes = await GetPrefixesAsync(asn, ct);
                foreach (var (prefix, length) in prefixes)
                    result.Add((prefix, length, asn));
            }
            catch
            {
                // skip failed ASN, continue with others
            }
        }
        return result;
    }

    public async Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default)
    {
        var prefixes = await GetPrefixesAsync(asn, ct);
        return prefixes.Count;
    }

    /// <summary>The RU/default prefix set — backed by the configured default prefix source.
    /// The projection is cached for one TTL so repeated calls (multiple sessions / refreshes)
    /// don't re-allocate the same ~11k-entry list.</summary>
    private List<(uint Prefix, byte Length, uint Asn)>? _ruProjected;
    private DateTime _ruCachedAt;

    public async Task<List<(uint Prefix, byte Length, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default)
    {
        if (_ruProjected is not null && DateTime.UtcNow - _ruCachedAt < _cacheTtl)
            return _ruProjected;

        var prefixes = await _prefixSources.GetDefaultAsync(ct);
        _ruProjected = prefixes.Select(p => (p.Prefix, p.Length, 0u)).ToList();
        _ruCachedAt = DateTime.UtcNow;
        return _ruProjected;
    }

    /// <summary>Prefixes of a configured source by name (cache-through).</summary>
    public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default) =>
        _prefixSources.GetAsync(name, ct);

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        var lists = _config.RipeStat?.AsnLists ?? [];

        var allAsns = lists.SelectMany(l => l.Asns).Distinct().ToList();
        foreach (var asn in allAsns)
        {
            try
            {
                await GetPrefixesAsync(asn, ct);
                Console.WriteLine($"  WarmUp: AS{asn} cached");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  WarmUp: AS{asn} failed — {ex.Message}");
            }
        }

        // Pre-load all configured prefix sources (file/HTTP/...) into the in-memory cache.
        await _prefixSources.WarmUpAsync(ct);
    }
}
