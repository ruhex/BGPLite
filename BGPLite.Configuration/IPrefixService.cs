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
    /// <summary>
    /// Fetches a per-peer user-supplied URL prefix-list source (epic #143 / issue #147). Unlike
    /// <see cref="GetSourcePrefixesAsync"/> (named, config-keyed, cache-through), this loads an
    /// arbitrary URL directly via the http provider — the URL is peer-supplied, not in
    /// <c>AppConfig.PrefixSources</c>, so it is not name-resolvable and not cached. SSRF defense
    /// (#144) is inherited from the http named client's <c>ConnectCallback</c>.
    /// </summary>
    Task<IReadOnlyList<(uint Prefix, byte Length)>> GetUserSourcePrefixesAsync(string name, string url, string? community, CancellationToken ct = default);
    Task WarmUpAsync(CancellationToken ct = default);
}
