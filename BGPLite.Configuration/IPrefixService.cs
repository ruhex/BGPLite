namespace BGPLite.Configuration;

/// <summary>
/// Origin-AS / source prefix lookup contract. Lives in this neutral lower layer so that the
/// concrete <c>PrefixService</c> implementation (BGPLite.Providers, a data layer) can implement it
/// without depending upward on BGPLite.Server — Server (the consumer) depends on this contract,
/// giving the dependency direction Server→Configuration←Providers as peers (#88).
/// </summary>
public interface IPrefixService
{
    Task<IReadOnlyList<(uint Prefix, byte Length)>> GetPrefixesAsync(uint asn, CancellationToken ct = default);
    Task<List<(uint Prefix, byte Length, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default);
    Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default);
    Task<List<(uint Prefix, byte Length, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<(uint Prefix, byte Length)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default);
    Task WarmUpAsync(CancellationToken ct = default);
}
