namespace BGPLite.Protocol;

/// <summary>
/// One AS_PATH/AS4_PATH segment. AS_SET counts as one for RFC 4271 §9.1.2.2 path length;
/// AS_SEQUENCE contributes every member. The member array retains the wire order.
/// </summary>
public readonly record struct AsPathSegment(byte Type, uint[] Asns)
{
    /// <summary>Number of AS numbers for route selection: one for a set, membership count for a sequence.</summary>
    public int PathLength => Type == BgpConstants.AsPath.AsSet ? 1 : Asns.Length;

    /// <summary>Flattens retained segments for AS-loop membership checks.</summary>
    public static uint[] Flatten(IEnumerable<AsPathSegment> segments) =>
        segments.SelectMany(segment => segment.Asns).ToArray();
}
