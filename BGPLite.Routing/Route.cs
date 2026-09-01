namespace BGPLite.Routing;

/// <summary>
/// A route-server route. The path/community collections are exposed as read-only lists because
/// the assembler and merge paths reuse the SAME backing array instances across many Route objects
/// (#238) — a consumer mutating, say, <c>route.Communities[0]</c> would corrupt every sibling
/// route. Callers that need a modified copy build a new array, as the merge paths already do.
/// </summary>
public sealed class Route
{
    // #15 phase 1: 128-bit prefix — IPv4 in the low 32 bits with IsIpv4 = true (the implicit
    // uint → UInt128 conversion in object initializers lands exactly there), IPv6 in the full
    // 128 bits with IsIpv4 = false. NextHop stays IPv4 (uint) until the MP_REACH phase.
    public required UInt128 Prefix { get; init; }
    public bool IsIpv4 { get; init; } = true;
    public required byte PrefixLength { get; init; }
    public required uint NextHop { get; init; }
    public IReadOnlyList<uint> AsPath { get; init; } = [];
    public IReadOnlyList<uint> Communities { get; init; } = [];
    /// <summary>BGP Large Communities (RFC 8092): triplets of (Global : Local1 : Local2).</summary>
    public IReadOnlyList<(uint Global, uint Local1, uint Local2)> LargeCommunities { get; init; } = [];

    public (UInt128 Prefix, byte Length, bool IsIpv4) Key => (Prefix, PrefixLength, IsIpv4);
}
