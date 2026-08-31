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
    public static List<PathAttribute> BuildUpdateAttributes(uint localAsn, bool localFourByteAsn, uint nextHop, IReadOnlyList<uint> communities)
    {
        var attrs = new List<PathAttribute>(5)
        {
            AttributeHelper.WriteOrigin(BgpOrigin.Igp),
        };

        var asPathAttrs = BuildAsPathAttributes(localAsn, localFourByteAsn);
        attrs.Add(asPathAttrs[0]);
        attrs.Add(AttributeHelper.WriteNextHop(nextHop));

        if (communities.Count > 0)
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
    public static Dictionary<IReadOnlyList<uint>, List<PathAttribute>> CreateUpdateAttributeCache() =>
        new(CommunitySetComparer.Instance);

    /// <summary>
    /// Returns the UPDATE path attributes for <paramref name="communities"/>, building them on
    /// first request for a community set and returning the cached list thereafter. The cached
    /// <see cref="PathAttribute"/> payloads are immutable, so the same list is safely shared by
    /// every UPDATE emitted for that community set.
    /// </summary>
    public static List<PathAttribute> GetCachedUpdateAttributes(
        uint localAsn, bool localFourByteAsn, uint nextHop, IReadOnlyList<uint> communities,
        Dictionary<IReadOnlyList<uint>, List<PathAttribute>> cache)
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
        List<PathAttribute> baseAttrs, IReadOnlyList<(uint Global, uint Local1, uint Local2)> largeCommunities)
    {
        if (largeCommunities.Count == 0)
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

        // RFC 6793 §4.2.3: "If the number of AS numbers in the AS_PATH attribute is less than
        // the number of AS numbers in the AS4_PATH attribute, then the AS4_PATH attribute SHALL
        // be ignored, and the AS_PATH attribute SHALL be taken as the AS path information."
        // (Happens when an intermediate aggregator truncated the AS_PATH — not a malformed UPDATE.)
        if (as4Path.Length > asPath.Length)
            return asPath;

        if (as4Path.Length == asPath.Length)
            return as4Path;

        var leadingCount = asPath.Length - as4Path.Length;
        for (var i = 0; i < leadingCount; i++)
        {
            if (asPath[i] == BgpConstants.AsPath.AsTrans)
                throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.MalformedAsPath, "Unresolved AS_TRANS in AS_PATH");
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
            throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.OptionalAttributeError, "Missing AS4_AGGREGATOR for AGGREGATOR AS_TRANS");

        if (!aggregatorAsn.HasValue && as4AggregatorAsn.HasValue)
            throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.OptionalAttributeError, "Missing AGGREGATOR attribute for AS4_AGGREGATOR");
    }

    /// <summary>
    /// Returns the malformed-capability TLV data for an OPEN NOTIFICATION when the received
    /// 4-octet-ASN capability has a wrong length. Scans the OPEN's capabilities for the first
    /// malformed FourOctetAsn entry and returns <c>[code, length, ...data]</c>; empty if none found.
    /// </summary>
    /// <summary>
    /// The parsed inbound attribute set of an announcing UPDATE — everything the routing layer
    /// needs to build a route (#270). Produced by <see cref="ParseRouteAttributes"/>.
    /// </summary>
    public sealed record RouteAttributes(
        uint[] AsPath,
        uint NextHop,
        uint[] Communities,
        (uint Global, uint Local1, uint Local2)[] LargeCommunities);

    /// <summary>
    /// Parses and validates the path attributes of an announcing UPDATE per RFC 4271 §6.3 /
    /// RFC 6793 / RFC 8092: per-attribute length and value checks (ORIGIN ∈ {0,1,2}, NEXT_HOP
    /// 4-octet, COMMUNITY % 4, Large Communities % 12, AS_PATH/AS4_PATH segment rules), the
    /// mandatory well-known set (ORIGIN/AS_PATH/NEXT_HOP), AS4_PATH trailing-sequence
    /// reconstruction, and AGGREGATOR/AS4_AGGREGATOR consistency. On a 4-octet session
    /// (<paramref name="fourByteAsnSession"/>) AS4_PATH/AS4_AGGREGATOR are skipped per RFC 6793 §4.1.
    /// Throws <see cref="BgpNotificationException"/> carrying the RFC subcode (2/3/4/5/6/8/9/11) so the
    /// caller can apply treat-as-withdraw (RFC 7606) — the exact pipeline previously inlined in
    /// BgpSession.HandleUpdateAsync, moved verbatim (#270).
    /// </summary>
    public static RouteAttributes ParseRouteAttributes(BgpUpdateMessage update, bool fourByteAsnSession, uint? localRouterId = null)
    {
        try
        {
            var originSeen = false;
            var asPathSeen = false;
            var nextHopSeen = false;
            uint nextHop = 0;
            uint[] asPath = [];
            uint[] communities = [];
            (uint Global, uint Local1, uint Local2)[] largeCommunities = [];
            uint[] as4Path = [];
            uint? aggregatorAsn = null;
            uint? as4AggregatorAsn = null;

            // RFC 7606 §3 (revising RFC 4271 §6.3): "If any other attribute (whether recognized or
            // unrecognized) appears more than once in an UPDATE message, then all the occurrences of
            // the attribute other than the first one SHALL be discarded and the UPDATE message will
            // continue to be processed." The switch below assigns unconditionally, so without this
            // guard the LAST occurrence won — an UPDATE carrying two NEXT_HOPs installed the second
            // one, so anything reading the first (a collector, a looking glass, an operator's packet
            // capture) disagreed with what actually landed in the route table (#287).
            //
            // The MP_REACH_NLRI/MP_UNREACH_NLRI half of that paragraph — duplicate → NOTIFICATION,
            // session reset — is deliberately not implemented: BGPLite is IPv4-unicast only and never
            // parses those attributes. It belongs with MP-BGP support (#14).
            //
            // Clear() is not redundant despite `localsinit` zeroing stackalloc by default: adding
            // [SkipLocalsInit] (or <SkipLocalsInit> in the csproj) is an ordinary perf tweak for a
            // codec like this one, and it would turn the guard below into garbage — random type
            // codes reading as "already seen", so the FIRST occurrence of a mandatory attribute
            // gets skipped and valid UPDATEs are withdrawn. 256 bytes of memset against a silent,
            // traffic-dependent failure is not a trade worth making.
            Span<bool> seenTypes = stackalloc bool[256];
            seenTypes.Clear();
            foreach (var attr in update.PathAttributes)
            {
                if (seenTypes[attr.TypeCode])
                    continue;
                seenTypes[attr.TypeCode] = true;

                // Order matters: the duplicate guard runs FIRST, so a discarded later occurrence is
                // never shape-checked. RFC 7606 §3 says those occurrences are discarded, not
                // "discarded but still validated" — checking them would reject an UPDATE over an
                // attribute that has no effect on the result (#287 + #290).
                //
                // RFC 4271 §6.3 (#322): an attribute with the Optional bit clear that this codec
                // does not recognize has unknown well-known semantics — it MUST be rejected with
                // Unrecognized Well-known Attribute (subcode 2), never silently ignored. Only
                // unrecognized OPTIONAL attributes may be discarded (RFC 7606 §2), and those fall
                // through untouched here. The throw routes through the caller's treat-as-withdraw,
                // not a session reset. Known-but-unread attributes (LOCAL_PREF,
                // ATOMIC_AGGREGATE; MED is optional) pass IsKnownAttribute and stay accepted —
                // over-rejecting those was the #290 lesson.
                if (!attr.Optional && !AttributeHelper.IsKnownAttribute(attr.TypeCode))
                    throw new BgpNotificationException(
                        BgpConstants.Error.UpdateMessageError,
                        BgpConstants.SubError.UnrecognizedWellKnownAttribute,
                        $"Unrecognized well-known path attribute type {attr.TypeCode}");
                ValidateAttributeShape(attr, fourByteAsnSession);

                switch (attr.TypeCode)
                {
                    case BgpConstants.Attribute.Origin:
                        // Length is guaranteed to be exactly 1 by ValidateAttributeShape; only the
                        // value remains to check (RFC 7606 §7.1: "malformed if its length is not 1
                        // or if it has an undefined value").
                        AttributeHelper.ReadOrigin(attr);
                        originSeen = true;
                        break;
                    case BgpConstants.Attribute.AsPath:
                        asPath = AttributeHelper.ReadAsPath(attr, fourByteAsnSession);
                        asPathSeen = true;
                        break;
                    case BgpConstants.Attribute.As4Path when !fourByteAsnSession:
                        as4Path = AttributeHelper.ReadAs4Path(attr);
                        break;
                    case BgpConstants.Attribute.NextHop:
                        // Length is guaranteed to be exactly 4 by ValidateAttributeShape (RFC 7606
                        // §7.3: "malformed if its length is not 4").
                        nextHop = AttributeHelper.ReadNextHop(attr);
                        nextHopSeen = true;
                        break;
                    case BgpConstants.Attribute.Community:
                        communities = AttributeHelper.ReadCommunities(attr);
                        break;
                    case BgpConstants.Attribute.LargeCommunity:
                        largeCommunities = AttributeHelper.ReadLargeCommunities(attr);
                        break;
                    case BgpConstants.Attribute.Aggregator:
                        aggregatorAsn = AttributeHelper.ReadAggregatorAsn(attr, fourByteAsnSession);
                        break;
                    case BgpConstants.Attribute.As4Aggregator when !fourByteAsnSession:
                        as4AggregatorAsn = AttributeHelper.ReadAs4AggregatorAsn(attr);
                        break;
                }
            }

            ValidateMandatoryAttributes(originSeen, asPathSeen, nextHopSeen);
            // RFC 4271 §6.3/§6.8: a semantically incorrect NEXT_HOP MUST be rejected with subcode 8
            // (Invalid NEXT_HOP Attribute) — "a valid unicast host address", never multicast, never
            // the receiving speaker's own address. Routed through the caller's treat-as-withdraw
            // path per RFC 7606 §7.3 (#292 item 1).
            if (nextHopSeen)
                ValidateNextHopSemantics(nextHop, localRouterId);
            asPath = MergeAsPathWithAs4Path(asPath, as4Path);
            ValidateAggregatorReconstruction(aggregatorAsn, as4AggregatorAsn);

            return new RouteAttributes(asPath, nextHop, communities, largeCommunities);
        }
        catch (BgpParseException ex)
        {
            // #235: preserve the RFC 4271 §6.3 subcode the codec recorded (e.g. Malformed AS_PATH
            // from ReadAsPath, Optional Attribute Error from AGGREGATOR/Large Communities) instead
            // of flattening it to Unspecific.
            throw new BgpNotificationException(BgpConstants.Error.UpdateMessageError, ex.SubErrorCode ?? BgpConstants.SubError.Unspecific, ex.Message);
        }
    }

    /// <summary>
    /// Validates that a received NEXT_HOP is a semantically valid unicast host address
    /// (RFC 4271 §6.3, subcode 8 "Invalid NEXT_HOP Attribute"; §6.8: "the IP address ... defined
    /// as a valid unicast host address ... MUST NOT be ... the IP address of the receiving
    /// speaker ... multicast ... addresses are never advertised"). Rejects: unspecified (0.0.0.0),
    /// loopback (127/8), multicast (224/4), reserved (240/4 — includes the broadcast address),
    /// and the local router-id when known. Throws <see cref="BgpNotificationException"/> so the
    /// caller applies treat-as-withdraw (RFC 7606 §7.3) — the route never reaches the table with
    /// a next hop that could blackhole or loop traffic (#292 item 1).
    /// </summary>
    public static void ValidateNextHopSemantics(uint nextHop, uint? localRouterId)
    {
        if (nextHop == 0)
            throw InvalidNextHop(nextHop, "the unspecified address 0.0.0.0");
        if ((nextHop >> 24) == 127)
            throw InvalidNextHop(nextHop, "a loopback address (127/8)");
        // 224/4 (multicast) and 240/4 (reserved, incl. 255.255.255.255) both live in the top
        // eighth of the address space: first nibble >= 0xE.
        if ((nextHop >> 28) >= 0xE)
            throw InvalidNextHop(nextHop, "a multicast or reserved address (224/4, 240/4)");
        if (localRouterId.HasValue && nextHop == localRouterId.Value)
            throw InvalidNextHop(nextHop, "the local speaker's own address (RFC 4271 §6.8)");
    }

    private static BgpNotificationException InvalidNextHop(uint nextHop, string why) => new(
        BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.InvalidNextHopAttribute,
        $"Invalid NEXT_HOP attribute: {BgpConstants.UintToIPAddress(nextHop)} is {why}");

    /// <summary>
    /// The RFC-mandated shape of a recognized path attribute: the required Optional/Transitive flag
    /// bits and, where the type code fixes it, the exact value length. Returns <c>null</c> for a
    /// type code this codec does not parse — RFC 7606 §3 leaves unrecognized attributes to the
    /// optional/transitive propagation rules rather than validating them.
    /// <para>
    /// Scoped deliberately to the attributes <see cref="ParseRouteAttributes"/> actually consumes.
    /// MED/LOCAL_PREF/ATOMIC_AGGREGATE are "known" to <see cref="AttributeHelper.IsKnownAttribute"/>
    /// but are never read here, and validating attributes the codec ignores would only create new
    /// ways to reject an UPDATE a conformant implementation accepts.
    /// </para>
    /// </summary>
    private static (bool Optional, bool Transitive, int? FixedLength)? ExpectedAttributeShape(
        byte typeCode, bool fourByteAsnSession) => typeCode switch
        {
            // Well-known mandatory (RFC 4271 §4.3 / §5.1): optional=0, transitive=1.
            BgpConstants.Attribute.Origin => (false, true, 1),
            BgpConstants.Attribute.AsPath => (false, true, (int?)null),
            BgpConstants.Attribute.NextHop => (false, true, 4),
            // Optional transitive (RFC 4271 §5.1.7, RFC 1997, RFC 6793 §3, RFC 8092 §2).
            BgpConstants.Attribute.Aggregator => (true, true, (int?)null),
            BgpConstants.Attribute.Community => (true, true, (int?)null),
            // AS4_PATH / AS4_AGGREGATOR exist only to tunnel 4-octet ASNs across a 2-octet session
            // (RFC 6793 §3). On a 4-octet session the switch below ignores them entirely
            // (`case ... when !fourByteAsnSession`), so validating their shape there would withdraw an
            // UPDATE's routes over an attribute this codec does not even read — the same over-rejection
            // the type list above avoids by excluding MED/LOCAL_PREF/ATOMIC_AGGREGATE (#290 review).
            BgpConstants.Attribute.As4Path when !fourByteAsnSession => (true, true, (int?)null),
            BgpConstants.Attribute.As4Aggregator when !fourByteAsnSession => (true, true, (int?)null),
            BgpConstants.Attribute.LargeCommunity => (true, true, (int?)null),
            _ => null,
        };

    /// <summary>
    /// Validates a recognized attribute's flags and fixed length against its type code (#290).
    /// Throws <see cref="BgpNotificationException"/> with Attribute Flags Error (4) or Attribute
    /// Length Error (5) so the caller's treat-as-withdraw path applies (RFC 7606 §4).
    /// </summary>
    private static void ValidateAttributeShape(PathAttribute attr, bool fourByteAsnSession)
    {
        if (ExpectedAttributeShape(attr.TypeCode, fourByteAsnSession) is not { } expected)
            return;

        // RFC 7606 §3: "If the value of either the Optional or Transitive bits in the Attribute
        // Flags is in conflict with their specified values, then the attribute MUST be treated as
        // malformed and the 'treat-as-withdraw' approach used."
        //
        // ONLY those two bits. The Partial bit is deliberately not checked: RFC 7606 narrows the
        // RFC 4271 §5 "MUST be 0 for well-known attributes" rule to Optional/Transitive, and
        // rejecting on Partial would drop routes that conformant implementations accept. Bit 0x08
        // (reserved) is already rejected at the wire level by BgpMessageReader.ParseAttribute (#272).
        if (attr.Optional != expected.Optional || attr.Transitive != expected.Transitive)
            throw new BgpNotificationException(
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.AttributeFlagsError,
                $"Attribute type {attr.TypeCode} has flags 0x{attr.Flags:X2} conflicting with its type code " +
                $"(expected optional={expected.Optional}, transitive={expected.Transitive})");

        // RFC 4271 §6.3: "If any recognized attribute has an Attribute Length that conflicts with
        // the expected length (based on the attribute type code), then the Error Subcode MUST be
        // set to Attribute Length Error." RFC 7606 §7.1/§7.3 fix ORIGIN at 1 octet and NEXT_HOP at
        // 4, both handled as treat-as-withdraw.
        if (expected.FixedLength is { } fixedLength && attr.Data.Length != fixedLength)
            throw new BgpNotificationException(
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.AttributeLengthError,
                $"Attribute type {attr.TypeCode} has length {attr.Data.Length}, which conflicts with its type code (expected {fixedLength})");
    }

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

/// <summary>Sequence equality over a route's community list (set-equivalence within a batch).</summary>
public sealed class CommunitySetComparer : IEqualityComparer<IReadOnlyList<uint>>
{
    public static readonly CommunitySetComparer Instance = new();

    public bool Equals(IReadOnlyList<uint>? x, IReadOnlyList<uint>? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null || x.Count != y.Count) return false;
        for (var i = 0; i < x.Count; i++)
            if (x[i] != y[i]) return false;
        return true;
    }

    public int GetHashCode(IReadOnlyList<uint> obj)
    {
        var hc = new HashCode();
        foreach (var c in obj) hc.Add(c);
        return hc.ToHashCode();
    }
}

/// <summary>Sequence equality over a route's Large Community list (RFC 8092 triplets).</summary>
public sealed class LargeCommunitySetComparer : IEqualityComparer<IReadOnlyList<(uint Global, uint Local1, uint Local2)>>
{
    public static readonly LargeCommunitySetComparer Instance = new();

    public bool Equals(IReadOnlyList<(uint Global, uint Local1, uint Local2)>? x, IReadOnlyList<(uint Global, uint Local1, uint Local2)>? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null || x.Count != y.Count) return false;
        for (var i = 0; i < x.Count; i++)
            if (x[i] != y[i]) return false;
        return true;
    }

    public int GetHashCode(IReadOnlyList<(uint Global, uint Local1, uint Local2)> obj)
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
    : IEqualityComparer<(IReadOnlyList<uint> Communities, IReadOnlyList<(uint Global, uint Local1, uint Local2)> LargeCommunities)>
{
    public static readonly CommunitySetPairComparer Instance = new();

    public bool Equals(
        (IReadOnlyList<uint> Communities, IReadOnlyList<(uint Global, uint Local1, uint Local2)> LargeCommunities) x,
        (IReadOnlyList<uint> Communities, IReadOnlyList<(uint Global, uint Local1, uint Local2)> LargeCommunities) y) =>
        CommunitySetComparer.Instance.Equals(x.Communities, y.Communities) &&
        LargeCommunitySetComparer.Instance.Equals(x.LargeCommunities, y.LargeCommunities);

    public int GetHashCode(
        (IReadOnlyList<uint> Communities, IReadOnlyList<(uint Global, uint Local1, uint Local2)> LargeCommunities) obj)
    {
        var hc = new HashCode();
        foreach (var c in obj.Communities) hc.Add(c);
        foreach (var l in obj.LargeCommunities) hc.Add(l);
        return hc.ToHashCode();
    }
}
