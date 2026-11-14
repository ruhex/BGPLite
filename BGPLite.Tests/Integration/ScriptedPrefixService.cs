using BGPLite.Contracts;
using BGPLite.Protocol;

namespace BGPLite.Tests.Integration;

/// <summary>
/// Deterministic in-memory IPrefixService standing in for the RIPEstat-backed PrefixService:
/// every route class (ASN catalogs, RU defaults, named prefix sources, per-peer user URL
/// sources) resolves from a scriptable catalog, so E2E tests assert exact prefix sets with no
/// network. Per-ASN failures are explicit (FailedAsns) to exercise the partial-failure
/// behavior that #556 made observable (export 503, complete=false).
/// </summary>
public sealed class ScriptedPrefixService : IPrefixService
{
    /// <summary>ASNs whose resolution fails — mirrored into AsnPrefixResolution.FailedAsns.</summary>
    public HashSet<uint> FailedAsns { get; } = [];

    /// <summary>CIDR strings per origin ASN (the RipeStat.AsnLists / custom-ASN path).</summary>
    public Dictionary<uint, List<string>> AsnPrefixes { get; } = [];

    /// <summary>CIDR strings of the RU default projection (country lists + unconfigured peers).</summary>
    public List<string> RuPrefixes { get; } = [];

    /// <summary>CIDR strings per configured PrefixSource name.</summary>
    public Dictionary<string, List<string>> SourcePrefixes { get; } = [];

    /// <summary>CIDR strings per per-peer user URL source name.</summary>
    public Dictionary<string, List<string>> UserSourcePrefixes { get; } = [];

    public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetPrefixesAsync(
        uint asn, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(UInt128, byte, bool)>>(ParseAll(AsnPrefixes.GetValueOrDefault(asn, [])));

    public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetPrefixesForAsns(
        IEnumerable<uint> asns, CancellationToken ct = default)
    {
        var requested = asns.ToList();
        var result = new List<(UInt128, byte, bool, uint)>();
        foreach (var asn in requested.Where(a => !FailedAsns.Contains(a)))
            foreach (var p in ParseAll(AsnPrefixes.GetValueOrDefault(asn, [])))
                result.Add((p.Prefix, p.Length, p.IsIpv4, asn));
        return Task.FromResult(result);
    }

    public async Task<AsnPrefixResolution> GetPrefixesForAsnsResultAsync(
        IEnumerable<uint> asns, CancellationToken ct = default)
    {
        var requested = asns.ToList();
        var failed = requested.Where(FailedAsns.Contains).ToList();
        var prefixes = await GetPrefixesForAsns(requested.Where(a => !FailedAsns.Contains(a)), ct);
        return new AsnPrefixResolution(prefixes, failed);
    }

    public Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default)
    {
        if (FailedAsns.Contains(asn))
            throw new InvalidOperationException($"Scripted failure for AS{asn}.");
        return Task.FromResult(AsnPrefixes.GetValueOrDefault(asn, []).Count);
    }

    public Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetRuPrefixesAsync(
        CancellationToken ct = default) =>
        Task.FromResult(ParseAll(RuPrefixes).Select(p => (p.Prefix, p.Length, p.IsIpv4, 0u)).ToList());

    public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetSourcePrefixesAsync(
        string name, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(UInt128, byte, bool)>>(ParseAll(SourcePrefixes.GetValueOrDefault(name, [])));

    public Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetUserSourcePrefixesAsync(
        string name, string url, string? community, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(UInt128, byte, bool)>>(ParseAll(UserSourcePrefixes.GetValueOrDefault(name, [])));

    public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;

    private static List<(UInt128 Prefix, byte Length, bool IsIpv4)> ParseAll(IEnumerable<string> cidrs)
    {
        var result = new List<(UInt128, byte, bool)>();
        foreach (var cidr in cidrs)
        {
            if (!PrefixCidr.TryParse(cidr, out var prefix))
                throw new FormatException($"Scripted catalog holds an invalid CIDR: '{cidr}'.");
            result.Add((prefix.Address, prefix.Length, prefix.IsIpv4));
        }
        return result;
    }
}
