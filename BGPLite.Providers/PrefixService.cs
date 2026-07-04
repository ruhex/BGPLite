using System.Collections.Concurrent;
using BGPLite.Configuration;

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

    /// <summary>Bounds how many ASNs are resolved against RIPEstat concurrently on a cold cache
    /// (warm traffic is cache-flat). Keeps cold-start fan-out from tripping RIPEstat rate limits.</summary>
    private const int MaxDegreeOfParallelism = 8;

    public async Task<List<(uint Prefix, byte Length, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default)
    {
        // Materialize once: we enumerate for fan-out and again for ordered assembly.
        var asnList = asns as IList<uint> ?? asns.ToList();
        if (asnList.Count == 0) return [];

        // Resolve each DISTINCT ASN concurrently (bounded) — latency is max, not sum, on cold
        // RIPEstat misses. Duplicates are coalesced for the fan-out so they cannot race the cold
        // per-ASN cache and double-fetch (CodeRabbit #130); output multiplicity is preserved below.
        // Each ASN keeps its own try/catch so one failure (incl. cancellation) can't drop the others.
        using var gate = new SemaphoreSlim(MaxDegreeOfParallelism, MaxDegreeOfParallelism);
        var resolvedByAsn = new Dictionary<uint, Task<IReadOnlyList<(uint Prefix, byte Length)>>>();
        foreach (var asn in asnList.Distinct())
            resolvedByAsn[asn] = ResolveAsnAsync(asn, gate, ct);

        await Task.WhenAll(resolvedByAsn.Values);

        // Reassemble in ORIGINAL input order (and multiplicity) — byte-for-byte identical to the
        // prior sequential output, including for duplicate ASNs.
        var result = new List<(uint Prefix, byte Length, uint Asn)>();
        foreach (var asn in asnList)
            foreach (var (prefix, length) in resolvedByAsn[asn].Result)
                result.Add((prefix, length, asn));
        return result;
    }

    private async Task<IReadOnlyList<(uint Prefix, byte Length)>> ResolveAsnAsync(uint asn, SemaphoreSlim gate, CancellationToken ct)
    {
        try
        {
            await gate.WaitAsync(ct);
            try
            {
                return await GetPrefixesAsync(asn, ct);
            }
            finally
            {
                gate.Release();
            }
        }
        catch
        {
            // skip failed ASN (incl. cancellation while queued), continue with the others
            return [];
        }
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
