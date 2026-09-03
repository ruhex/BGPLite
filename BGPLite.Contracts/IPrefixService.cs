namespace BGPLite.Contracts;

/// <summary>
/// Origin-AS / source prefix lookup contract. Lives in this neutral lower layer so that the
/// concrete <c>PrefixService</c> implementation (BGPLite.Providers, a data layer) can implement it
/// without depending upward on BGPLite.Server — Server (the consumer) depends on this contract,
/// giving the dependency direction Server→Configuration←Providers as peers (#88).
/// <para>
/// Prefixes are family-tagged tuples <c>(UInt128 Prefix, byte Length, bool IsIpv4)</c> (#14
/// phase 4) — the same layout as <c>Route.Key</c>. IPv4 addresses live in the LOW 32 bits of
/// <c>Prefix</c> with <c>IsIpv4 = true</c>; IPv6 uses the full 128 bits. (The tuple carries the
/// family instead of the Protocol layer's <c>IpPrefix</c> type because Contracts is a
/// dependency-free leaf.)
/// </para>
/// </summary>
public interface IPrefixService
{
    Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetPrefixesAsync(uint asn, CancellationToken ct = default);
    Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default);
    Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default);
    Task<List<(UInt128 Prefix, byte Length, bool IsIpv4, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default);
    /// <summary>
    /// Fetches a per-peer user-supplied URL prefix-list source (epic #143 / issue #147). Unlike
    /// <see cref="GetSourcePrefixesAsync"/> (named, config-keyed, cache-through), this loads an
    /// arbitrary URL directly via the http provider — the URL is peer-supplied, not in
    /// <c>AppConfig.PrefixSources</c>, so it is not name-resolvable and not cached. SSRF defense
    /// (#144) is inherited from the http named client's <c>ConnectCallback</c>.
    /// </summary>
    Task<IReadOnlyList<(UInt128 Prefix, byte Length, bool IsIpv4)>> GetUserSourcePrefixesAsync(string name, string url, string? community, CancellationToken ct = default);
    Task WarmUpAsync(CancellationToken ct = default);
}
