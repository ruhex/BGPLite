namespace BGPLite.Routing;

/// <summary>
/// A route-server route. The path/community collections are exposed as read-only lists because
/// the assembler and merge paths reuse the SAME backing array instances across many Route objects
/// (#238) — a consumer mutating, say, <c>route.Communities[0]</c> would corrupt every sibling
/// route. Callers that need a modified copy build a new array, as the merge paths already do.
/// </summary>
public sealed class Route
{
    public required uint Prefix { get; init; }
    public required byte PrefixLength { get; init; }
    public required uint NextHop { get; init; }
    public IReadOnlyList<uint> AsPath { get; init; } = [];
    public IReadOnlyList<uint> Communities { get; init; } = [];
    /// <summary>BGP Large Communities (RFC 8092): triplets of (Global : Local1 : Local2).</summary>
    public IReadOnlyList<(uint Global, uint Local1, uint Local2)> LargeCommunities { get; init; } = [];

    public (uint Prefix, byte Length) Key => (Prefix, PrefixLength);
}
