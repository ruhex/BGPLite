namespace BGPLite.Server;

public interface IPrefixService
{
    Task<IReadOnlyList<(uint Prefix, byte Length)>> GetPrefixesAsync(uint asn, CancellationToken ct = default);
    Task<List<(uint Prefix, byte Length, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default);
    Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default);
    Task<List<(uint Prefix, byte Length, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<(uint Prefix, byte Length)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default);
    Task WarmUpAsync(CancellationToken ct = default);
}
