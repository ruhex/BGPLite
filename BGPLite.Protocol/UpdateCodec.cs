using System.Collections.Generic;

namespace BGPLite.Protocol;

/// <summary>
/// BGP UPDATE path-attribute codec + inbound validators, extracted from <c>BgpSession</c> (#93).
/// <para>
/// The outbound side (<see cref="BuildUpdateAttributes"/> / <see cref="GetCachedUpdateAttributes"/> /
/// <see cref="WithLargeCommunityAttribute"/>) builds the path-attribute list for an outbound UPDATE
/// in RFC 4271 order (ORIGIN, AS_PATH, NEXT_HOP, COMMUNITY, AS4_PATH), with a per-send cache keyed
/// by community set (#87). The inbound side (<see cref="ValidateMandatoryAttributes"/> /
/// <see cref="MergeAsPathWithAs4Path"/> / <see cref="ValidateAggregatorReconstruction"/>) validates
/// and reconstructs received attributes per RFC 6793. <see cref="GetMalformedFourOctetAsnCapabilityData"/>
/// builds the malformed-capability TLV for an OPEN NOTIFICATION.
/// </para>
/// <para>
/// All methods are pure — every input is a parameter, no instance state. They were previously
/// <c>internal static</c> on <c>BgpSession</c> (reachable from tests via <c>InternalsVisibleTo</c>);
/// moving them here as <c>public</c> removes that test-backdoor and places them in the Protocol
/// layer alongside <see cref="AttributeHelper"/> and <see cref="BgpMessageWriter"/>.
/// </para>
/// </summary>
public static class UpdateCodec
{
    /// <summary>
    /// Builds the AS_PATH (and optional AS4_PATH) attribute(s) for the local ASN. On a 4-byte
    /// session the path is a single 4-octet AS_PATH. On a 2-byte session a 4-byte local ASN is
    /// tunneled via AS_TRANS in AS_PATH + the true ASN in AS4_PATH (RFC 6793 §6/F.5).
    /// </summary>
    public static List<PathAttribute> BuildAsPathAttributes(uint localAsn, bool localFourByteAsn)
    {
        var attrs = new List<PathAttribute>(2);
        if (localFourByteAsn)
        {
            attrs.Add(AttributeHelper.WriteAsPath([localAsn], fourByteAsn: true));
        }
        else
        {
            var asPathAsn = localAsn > ushort.MaxValue ? BgpConstants.AsPath.AsTrans : localAsn;
            attrs.Add(AttributeHelper.WriteAsPath([asPathAsn], fourByteAsn: false));

            if (localAsn > ushort.MaxValue)
                attrs.Add(AttributeHelper.WriteAs4Path([localAsn]));
        }
        return attrs;
    }

    /// <summary>
    /// Builds outbound UPDATE path attributes in RFC order: ORIGIN, AS_PATH, NEXT_HOP,
    /// COMMUNITY, AS4_PATH.
    /// </summary>
    public static List<PathAttribute> BuildUpdateAttributes(uint localAsn, bool localFourByteAsn, uint nextHop, uint[] communities)
    {
        var attrs = new List<PathAttribute>(5)
        {
            AttributeHelper.WriteOrigin(BgpOrigin.Igp),
        };

        var asPathAttrs = BuildAsPathAttributes(localAsn, localFourByteAsn);
        attrs.Add(asPathAttrs[0]);
        attrs.Add(AttributeHelper.WriteNextHop(nextHop));

        if (communities.Length > 0)
            attrs.Add(AttributeHelper.WriteCommunities(communities));

        if (asPathAttrs.Count > 1)
            attrs.Add(asPathAttrs[1]);

        return attrs;
    }

    /// <summary>
    /// Creates a per-send cache of built UPDATE path attributes, keyed by community set. The
    /// cache is scoped to a single send invocation: the ASN/nextHop inputs are constant for that
    /// whole send, so identical community sets yield byte-identical <see cref="PathAttribute"/>
    /// lists that can be reused across the N 100-NLRI batches (#87).
    /// </summary>
    public static Dictionary<uint[], List<PathAttribute>> CreateUpdateAttributeCache() =>
        new(CommunitySetComparer.Instance);

    /// <summary>
    /// Returns the UPDATE path attributes for <paramref name="communities"/>, building them on
    /// first request for a community set and returning the cached list thereafter. The cached
    /// <see cref="PathAttribute"/> payloads are immutable, so the same list is safely shared by
    /// every UPDATE emitted for that community set.
    /// </summary>
    public static List<PathAttribute> GetCachedUpdateAttributes(
        uint localAsn, bool localFourByteAsn, uint nextHop, uint[] communities,
        Dictionary<uint[], List<PathAttribute>> cache)
    {
        if (cache.TryGetValue(communities, out var cached))
            return cached;

        var attrs = BuildUpdateAttributes(localAsn, localFourByteAsn, nextHop, communities);
        cache[communities] = attrs;
        return attrs;
    }

    /// <summary>
    /// Returns the path attributes for an UPDATE carrying the given Large Community set: the
    /// cached base attributes (ORIGIN/AS_PATH/NEXT_HOP/COMMUNITY/AS4_PATH) untouched when
    /// <paramref name="largeCommunities"/> is empty, otherwise a shallow copy with a
    /// LARGE_COMMUNITY attribute appended. The cached base list is never mutated, so other
    /// batches in the same send that share regular communities but carry a different (or empty)
    /// large-community set still observe the correct base. Appended last, which keeps the
    /// emitted attributes in ascending type-code order (32 sorts after AS4_PATH 17).
    /// </summary>
    public static List<PathAttribute> WithLargeCommunityAttribute(
        List<PathAttribute> baseAttrs, (uint Global, uint Local1, uint Local2)[] largeCommunities)
    {
        if (largeCommunities.Length == 0)
            return baseAttrs;

        var withLarge = new List<PathAttribute>(baseAttrs.Count + 1);
        withLarge.AddRange(baseAttrs);
        withLarge.Add(AttributeHelper.WriteLargeCommunities(largeCommunities));
        return withLarge;
    }

    /// <summary>
    /// Validates that a route announcement carried the mandatory well-known attributes
    /// (ORIGIN, AS_PATH, NEXT_HOP). Throws <see cref="BgpNotificationException"/> on a missing attribute.
    /// </summary>
    public static void ValidateMandatoryAttributes(bool originSeen, bool asPathSeen, bool nextHopSeen)
    {
        if (!originSeen)
            throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.MissingWellKnownAttribute, "Missing mandatory ORIGIN attribute");
        if (!asPathSeen)
            throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.MissingWellKnownAttribute, "Missing mandatory AS_PATH attribute");
        if (!nextHopSeen)
            throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.MissingWellKnownAttribute, "Missing mandatory NEXT_HOP attribute");
    }

    /// <summary>
    /// Reconstructs the true AS path for a 2-byte peer using RFC 6793 trailing-sequence
    /// reconstruction. The last N ASNs in AS_PATH are replaced with the AS4_PATH values,
    /// where N = min(AS_PATH length, AS4_PATH length).
    /// </summary>
    public static uint[] MergeAsPathWithAs4Path(uint[] asPath, uint[] as4Path)
    {
        if (as4Path.Length == 0)
            return asPath;

        if (as4Path.Length > asPath.Length)
            throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.Unspecific, "AS4_PATH longer than AS_PATH");

        if (as4Path.Length == asPath.Length)
            return as4Path;

        var leadingCount = asPath.Length - as4Path.Length;
        for (var i = 0; i < leadingCount; i++)
        {
            if (asPath[i] == BgpConstants.AsPath.AsTrans)
                throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.Unspecific, "Unresolved AS_TRANS in AS_PATH");
        }

        var merged = new uint[asPath.Length];
        Array.Copy(asPath, 0, merged, 0, leadingCount);
        Array.Copy(as4Path, 0, merged, leadingCount, as4Path.Length);

        return merged;
    }

    /// <summary>
    /// Validates RFC 6793 AGGREGATOR/AS4_AGGREGATOR consistency: AS_TRANS in AGGREGATOR requires
    /// AS4_AGGREGATOR, and a lone AS4_AGGREGATOR without AGGREGATOR is malformed.
    /// </summary>
    public static void ValidateAggregatorReconstruction(uint? aggregatorAsn, uint? as4AggregatorAsn)
    {
        if (aggregatorAsn == BgpConstants.AsPath.AsTrans && as4AggregatorAsn is null)
            throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.Unspecific, "Missing AS4_AGGREGATOR for AGGREGATOR AS_TRANS");

        if (!aggregatorAsn.HasValue && as4AggregatorAsn.HasValue)
            throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.Unspecific, "Missing AGGREGATOR attribute for AS4_AGGREGATOR");
    }

    /// <summary>
    /// Returns the malformed-capability TLV data for an OPEN NOTIFICATION when the received
    /// 4-octet-ASN capability has a wrong length. Scans the OPEN's capabilities for the first
    /// malformed FourOctetAsn entry and returns <c>[code, length, ...data]</c>; empty if none found.
    /// </summary>
    public static byte[] GetMalformedFourOctetAsnCapabilityData(BgpOpenMessage open)
    {
        foreach (var cap in open.Capabilities)
        {
            if (cap.Code == BgpConstants.Capability.FourOctetAsn && cap.Data.Length != 4)
                return [BgpConstants.Capability.FourOctetAsn, (byte)cap.Data.Length,
                    ..cap.Data];
        }

        return [];
    }
}

/// <summary>Sequence equality over a route's community array (set-equivalence within a batch).</summary>
public sealed class CommunitySetComparer : IEqualityComparer<uint[]>
{
    public static readonly CommunitySetComparer Instance = new();

    public bool Equals(uint[]? x, uint[]? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null || x.Length != y.Length) return false;
        for (var i = 0; i < x.Length; i++)
            if (x[i] != y[i]) return false;
        return true;
    }

    public int GetHashCode(uint[] obj)
    {
        var hc = new HashCode();
        foreach (var c in obj) hc.Add(c);
        return hc.ToHashCode();
    }
}

/// <summary>Sequence equality over a route's Large Community array (RFC 8092 triplets).</summary>
public sealed class LargeCommunitySetComparer : IEqualityComparer<(uint Global, uint Local1, uint Local2)[]>
{
    public static readonly LargeCommunitySetComparer Instance = new();

    public bool Equals((uint Global, uint Local1, uint Local2)[]? x, (uint Global, uint Local1, uint Local2)[]? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null || x.Length != y.Length) return false;
        for (var i = 0; i < x.Length; i++)
            if (x[i] != y[i]) return false;
        return true;
    }

    public int GetHashCode((uint Global, uint Local1, uint Local2)[] obj)
    {
        var hc = new HashCode();
        foreach (var c in obj) hc.Add(c);
        return hc.ToHashCode();
    }
}

/// <summary>
/// Composite sequence equality over a route's (regular, large) community pair, used to
/// partition a send batch that spans more than one community set.
/// </summary>
public sealed class CommunitySetPairComparer
    : IEqualityComparer<(uint[] Communities, (uint Global, uint Local1, uint Local2)[] LargeCommunities)>
{
    public static readonly CommunitySetPairComparer Instance = new();

    public bool Equals(
        (uint[] Communities, (uint Global, uint Local1, uint Local2)[] LargeCommunities) x,
        (uint[] Communities, (uint Global, uint Local1, uint Local2)[] LargeCommunities) y) =>
        CommunitySetComparer.Instance.Equals(x.Communities, y.Communities) &&
        LargeCommunitySetComparer.Instance.Equals(x.LargeCommunities, y.LargeCommunities);

    public int GetHashCode(
        (uint[] Communities, (uint Global, uint Local1, uint Local2)[] LargeCommunities) obj)
    {
        var hc = new HashCode();
        foreach (var c in obj.Communities) hc.Add(c);
        foreach (var l in obj.LargeCommunities) hc.Add(l);
        return hc.ToHashCode();
    }
}
