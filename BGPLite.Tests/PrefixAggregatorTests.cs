using BGPLite.Routing;
using BGPLite.Server;

namespace BGPLite.Tests;

public class PrefixAggregatorTests
{
    private const uint NextHop = 0x01020304;
    private readonly IPrefixAggregator _aggregator = new ExactUnionPrefixAggregator();

    private static Route R(uint prefix, byte length, uint[]? communities = null,
        (uint Global, uint Local1, uint Local2)[]? largeCommunities = null) =>
        new()
        {
            Prefix = prefix,
            PrefixLength = length,
            NextHop = NextHop,
            Communities = communities ?? [],
            LargeCommunities = largeCommunities ?? []
        };

    private static List<(UInt128 Prefix, byte Length)> Pfx(IReadOnlyList<Route> routes) =>
        routes.Select(r => (r.Prefix, r.PrefixLength)).ToList();

    /// <summary>IPv6 route builder. <paramref name="prefix"/> carries the network address in the
    /// full 128 bits (<see cref="V6"/> composes one from leading hextets).</summary>
    private static Route R6(UInt128 prefix, byte length, uint[]? communities = null) => new()
    {
        Prefix = prefix,
        IsIpv4 = false,
        PrefixLength = length,
        NextHop = NextHop,
        Communities = communities ?? []
    };

    /// <summary>Composes an IPv6 network address from its leading hextets (the rest are zero):
    /// <c>V6(0x2001, 0x0DB8, 1)</c> is 2001:db8:1::.</summary>
    private static UInt128 V6(ushort g0, ushort g1 = 0, ushort g2 = 0) =>
        ((UInt128)g0 << 112) | ((UInt128)g1 << 96) | ((UInt128)g2 << 80);

    /// <summary>Family-aware counterpart of <see cref="UnionRanges"/>: the sorted, merged
    /// [start,end] intervals of an IPv6 prefix set (address space capped at 2^128-1).</summary>
    private static List<(UInt128 Start, UInt128 End)> UnionRanges6(IEnumerable<(UInt128 Prefix, byte Length)> prefixes)
    {
        var intervals = new List<(UInt128 Start, UInt128 End)>();
        foreach (var (prefix, length) in prefixes)
        {
            if (length > 128) continue;
            var mask = length == 0 ? UInt128.Zero : UInt128.MaxValue << (128 - length);
            var start = prefix & mask;
            // /0 spans the whole space: 2^128 itself overflows, so the end is stated directly.
            var end = length == 0 ? UInt128.MaxValue : start + ((UInt128)1 << (128 - length)) - 1;
            intervals.Add((start, end));
        }
        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(UInt128 Start, UInt128 End)>();
        foreach (var (s, e) in intervals)
        {
            if (merged.Count > 0 && (s <= merged[^1].End ||
                                     (merged[^1].End != UInt128.MaxValue && s == merged[^1].End + 1)))
            {
                var newEnd = merged[^1].End > e ? merged[^1].End : e;
                merged[^1] = (merged[^1].Start, newEnd);
            }
            else
                merged.Add((s, e));
        }
        return merged;
    }

    /// <summary>Independent reference implementation: the sorted, merged [start,end] intervals
    /// of a prefix set. Used to cross-check that aggregation adds no address and drops none.</summary>
    private static List<(UInt128 Start, UInt128 End)> UnionRanges(IEnumerable<(UInt128 Prefix, byte Length)> prefixes)
    {
        var intervals = new List<(UInt128 Start, UInt128 End)>();
        foreach (var (prefix, length) in prefixes)
        {
            if (length > 32) continue;
            var mask = length == 0 ? 0u : (UInt128)(0xFFFFFFFFu << (32 - length));
            var start = prefix & mask;
            var size = length == 0 ? ((UInt128)1 << 32) : ((UInt128)1 << (32 - length));
            intervals.Add((start, start + size - 1));
        }
        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(UInt128 Start, UInt128 End)>();
        foreach (var (s, e) in intervals)
        {
            if (merged.Count > 0 && s <= merged[^1].End + 1)
            {
                var newEnd = merged[^1].End > e ? merged[^1].End : e;
                merged[^1] = (merged[^1].Start, newEnd);
            }
            else
                merged.Add((s, e));
        }
        return merged;
    }

    [Fact]
    public void Empty_ReturnsEmpty()
    {
        var result = _aggregator.Aggregate([]);
        Assert.Empty(result);
    }

    [Fact]
    public void Single_Unchanged()
    {
        var result = _aggregator.Aggregate([R(0xC0A80000, 24)]);
        Assert.Equal([(0xC0A80000u, (byte)24)], Pfx(result));
    }

    [Fact]
    public void NestedPrefixes_CollapseToWidest()
    {
        // 149.154.160.0/22 + /23 + /24  →  /22  (the /23 and /24 are fully contained).
        var result = _aggregator.Aggregate([
            R(0x959AA000, 22),
            R(0x959AA000, 23),
            R(0x959AA000, 24),
        ]);

        Assert.Equal([(0x959AA000u, (byte)22)], Pfx(result));
        Assert.Equal(UnionRanges([(0x959AA000, 22), (0x959AA000, 23), (0x959AA000, 24)]),
                     UnionRanges(Pfx(result)));
    }

    [Fact]
    public void AlignedHalves_MergeToSupernet()
    {
        // 192.168.0.0/24 + 192.168.1.0/24  →  192.168.0.0/23
        var result = _aggregator.Aggregate([R(0xC0A80000, 24), R(0xC0A80100, 24)]);
        Assert.Equal([(0xC0A80000u, (byte)23)], Pfx(result));
    }

    [Fact]
    public void NonAlignedAdjacent_StaysSeparate_NoExtraIp()
    {
        // 192.168.1.0/24 + 192.168.2.0/24 straddle a /23 boundary → cannot merge.
        var result = _aggregator.Aggregate([R(0xC0A80100, 24), R(0xC0A80200, 24)]);
        Assert.Equal([(0xC0A80100u, (byte)24), (0xC0A80200u, (byte)24)], Pfx(result));
        Assert.Equal(UnionRanges([(0xC0A80100, 24), (0xC0A80200, 24)]), UnionRanges(Pfx(result)));
    }

    [Fact]
    public void FourContiguous_MergeToSlash22()
    {
        var result = _aggregator.Aggregate([
            R(0x0A000000, 24), R(0x0A000100, 24), R(0x0A000200, 24), R(0x0A000300, 24),
        ]);
        Assert.Equal([(0x0A000000u, (byte)22)], Pfx(result));
    }

    [Fact]
    public void OverlapSpanningTwoSupernets_MergesOnlyWhatAligns()
    {
        // 10.0.1.0/24 + 10.0.2.0/24 + 10.0.3.0/24 → 10.0.1.0/24 + 10.0.2.0/23
        var result = _aggregator.Aggregate([
            R(0x0A000100, 24), R(0x0A000200, 24), R(0x0A000300, 24),
        ]);
        Assert.Equal([(0x0A000100u, (byte)24), (0x0A000200u, (byte)23)], Pfx(result));
        Assert.Equal(UnionRanges([(0x0A000100, 24), (0x0A000200, 24), (0x0A000300, 24)]),
                     UnionRanges(Pfx(result)));
    }

    [Fact]
    public void DefaultRoute_Handled()
    {
        // 0.0.0.0/0 alone stays /0.
        Assert.Equal([(0u, (byte)0)], Pfx(_aggregator.Aggregate([R(0, 0)])));

        // 0.0.0.0/1 + 128.0.0.0/1 → 0.0.0.0/0
        var result = _aggregator.Aggregate([R(0, 1), R(0x80000000, 1)]);
        Assert.Equal([(0u, (byte)0)], Pfx(result));
    }

    [Fact]
    public void HostBits_AreMasked()
    {
        // 192.168.0.5/24 has host bits set → normalized to 192.168.0.0/24, then merges
        // with 192.168.1.0/24 into 192.168.0.0/23 with no extra address.
        var result = _aggregator.Aggregate([R(0xC0A80005, 24), R(0xC0A80100, 24)]);
        Assert.Equal([(0xC0A80000u, (byte)23)], Pfx(result));
    }

    [Fact]
    public void UnsortedInput_MergesCorrectly()
    {
        var result = _aggregator.Aggregate([
            R(0x0A000300, 24), R(0x0A000000, 24), R(0x0A000200, 24), R(0x0A000100, 24),
        ]);
        Assert.Equal([(0x0A000000u, (byte)22)], Pfx(result));
    }

    [Fact]
    public void UnionInvariant_NoExtraAndNoMissingIp()
    {
        // A mixed, overlapping, out-of-order set: the aggregated output's address union
        // must equal the input's address union exactly.
        var input = new List<Route>
        {
            R(0x0A000000, 24), R(0x0A000100, 25), R(0x0A000180, 25), // /24 + two halves of next /24
            R(0xC0A80800, 23), R(0xC0A80800, 24),                   // /24 nested in /23
            R(0xAC100000, 16), R(0xAC100500, 24),                   // /24 nested in /16
        };

        var output = _aggregator.Aggregate(input);

        Assert.Equal(UnionRanges(input.Select(r => (r.Prefix, r.PrefixLength))), UnionRanges(Pfx(output)));
        Assert.True(output.Count <= input.Count);
    }

    [Fact]
    public void DifferentCommunities_DoNotMerge()
    {
        // Adjacent aligned /24s but different communities → stay separate.
        var result = _aggregator.Aggregate([
            R(0xC0A80000, 24, [0x12345601u]),
            R(0xC0A80100, 24, [0x12345602u]),
        ]);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Prefix == 0xC0A80000 && r.Communities.SequenceEqual([0x12345601u]));
        Assert.Contains(result, r => r.Prefix == 0xC0A80100 && r.Communities.SequenceEqual([0x12345602u]));
    }

    [Fact]
    public void SameCommunities_MergeAndPreserveCommunity()
    {
        var comm = new uint[] { 0x65, 0x100 };
        var result = _aggregator.Aggregate([
            R(0xC0A80000, 24, (uint[])comm.Clone()),
            R(0xC0A80100, 24, [0x100u, 0x65u]), // same set, different order
        ]);
        Assert.Equal([(0xC0A80000u, (byte)23)], Pfx(result));
        Assert.Single(result);
        Assert.Equal([0x65u, 0x100u], result[0].Communities); // sorted, set semantics
    }

    [Fact]
    public void NoOp_ReturnsInputUnchanged()
    {
        IPrefixAggregator noop = new NoOpPrefixAggregator();
        var input = new List<Route> { R(0xC0A80000, 24), R(0xC0A80100, 24) };
        var result = noop.Aggregate(input);
        Assert.Same(input, result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void SingleRoute_HostBitsAreMasked()
    {
        // 192.168.0.5/24 alone (host bits set) must normalize to 192.168.0.0/24, exactly
        // as the multi-prefix path does (regression: the Count==1 fast path used to skip masking).
        var result = _aggregator.Aggregate([R(0xC0A80005, 24)]);
        Assert.Equal([(0xC0A80000u, (byte)24)], Pfx(result));
    }

    [Fact]
    public void DuplicateCommunities_AreDedupedForGrouping()
    {
        // Same prefix, set-equivalent communities ([0x65] vs [0x65,0x65]) -> one group, one route.
        var result = _aggregator.Aggregate([
            R(0xC0A80000, 24, [0x65u]),
            R(0xC0A80000, 24, [0x65u, 0x65u]),
        ]);
        var route = Assert.Single(result);
        Assert.Equal(0xC0A80000u, route.Prefix);
    }

    // ---- #14 phase 3: IPv6 aggregation (family-aware, /0..128) ----

    [Fact]
    public void Ipv6_NestedPrefixes_CollapseToWidest()
    {
        // 2001:db8::/32 + nested 2001:db8:1::/48 → /32.
        var result = _aggregator.Aggregate([
            R6(V6(0x2001, 0xDB8), 32),
            R6(V6(0x2001, 0xDB8, 1), 48),
        ]);
        Assert.Equal([(V6(0x2001, 0xDB8), (byte)32)], Pfx(result));
        Assert.All(result, r => Assert.False(r.IsIpv4));
    }

    [Fact]
    public void Ipv6_AdjacentPrefixes_MergeToSupernet()
    {
        // 2001:db8::/48 + 2001:db8:1::/48 → 2001:db8::/47. Regression: the 32-bit aggregator
        // dropped every IPv6 prefix longer than /32, so the union lost BOTH routes entirely.
        var result = _aggregator.Aggregate([
            R6(V6(0x2001, 0xDB8), 48),
            R6(V6(0x2001, 0xDB8, 1), 48),
        ]);
        var route = Assert.Single(result);
        Assert.Equal((V6(0x2001, 0xDB8), (byte)47), (route.Prefix, route.PrefixLength));
        Assert.False(route.IsIpv4);
    }

    [Fact]
    public void Ipv6_HostBits_AreMasked()
    {
        var result = _aggregator.Aggregate([R6(V6(0x2001, 0xDB8) + 5, 32)]);
        Assert.Equal([(V6(0x2001, 0xDB8), (byte)32)], Pfx(result));
        Assert.False(result[0].IsIpv4);
    }

    [Fact]
    public void Ipv6_Slash127Pair_MergesToSlash126()
    {
        var result = _aggregator.Aggregate([
            R6(V6(0x2001, 0xDB8), 127),
            R6(V6(0x2001, 0xDB8) + 2, 127),
        ]);
        var route = Assert.Single(result);
        Assert.Equal((V6(0x2001, 0xDB8), (byte)126), (route.Prefix, route.PrefixLength));
    }

    [Fact]
    public void Ipv6_Slash128_HostRoute_StaysUnchanged()
    {
        var result = _aggregator.Aggregate([R6(V6(0x2001, 0xDB8, 1) + 5, 128)]);
        var route = Assert.Single(result);
        Assert.Equal((V6(0x2001, 0xDB8, 1) + 5, (byte)128), (route.Prefix, route.PrefixLength));
    }

    [Fact]
    public void Ipv6_DefaultRoute_Handled()
    {
        // ::/0 alone stays ::/0 with the IPv6 family (regression: it fell into the 32-bit
        // interval math and came back out as 0.0.0.0/0 marked IPv4).
        var single = Assert.Single(_aggregator.Aggregate([R6(0, 0)]));
        Assert.Equal((UInt128.Zero, (byte)0), (single.Prefix, single.PrefixLength));
        Assert.False(single.IsIpv4);

        // The two /1 halves merge back into ::/0.
        var merged = Assert.Single(_aggregator.Aggregate([
            R6(0, 1),
            R6((UInt128)1 << 127, 1),
        ]));
        Assert.Equal((UInt128.Zero, (byte)0), (merged.Prefix, merged.PrefixLength));
        Assert.False(merged.IsIpv4);
    }

    [Fact]
    public void Ipv6_UnionInvariant_NoExtraAndNoMissingIp()
    {
        // ::/0 swallows the whole set: the union must stay the full address space, emitted
        // as exactly one route.
        var input = new List<Route>
        {
            R6(V6(0x2001, 0xDB8), 48),
            R6(V6(0x2001, 0xDB8, 1), 48),                      // adjacent /48s → /47
            R6(V6(0x2001, 0xDB8, 2), 64),
            R6(V6(0x2001, 0xDB8, 2) + ((UInt128)1 << 48), 64), // adjacent /64s → /63
            R6(UInt128.Zero, 0),
        };

        var output = _aggregator.Aggregate(input);

        Assert.Equal(UnionRanges6(input.Select(r => (r.Prefix, r.PrefixLength))),
                     UnionRanges6(Pfx(output)));
        Assert.True(output.Count <= input.Count);
        Assert.All(output, r => Assert.False(r.IsIpv4));
    }

    [Fact]
    public void Ipv4AndIpv6_SameCommunities_NeverMerge()
    {
        // Identical community sets, but IPv4 and IPv6 must never land in one summary
        // (ADR 0001 §6). The two IPv6 /32s are the same network after host-bit masking and
        // legitimately collapse to one; the point is the family split: pre-phase-3 the IPv6
        // routes fell into the 32-bit interval space and the whole set collapsed into
        // 0.0.0.0/0 marked IPv4.
        var comm = new uint[] { 0x65 };
        var result = _aggregator.Aggregate([
            R(0xC0A80000, 24, comm),
            R(0xC0A80100, 24, (uint[])comm.Clone()),
            R6(V6(0x2001, 0xDB8), 32, (uint[])comm.Clone()),
            R6(V6(0x2001, 0xDB8, 1), 32, (uint[])comm.Clone()),
        ]);

        Assert.Equal(2, result.Count);
        var v4 = Assert.Single(result, r => r.IsIpv4);
        Assert.Equal((0xC0A80000u, (byte)23), (v4.Prefix, v4.PrefixLength));
        var v6 = Assert.Single(result, r => !r.IsIpv4);
        Assert.Equal((V6(0x2001, 0xDB8), (byte)32), (v6.Prefix, v6.PrefixLength));
        Assert.Equal([0x65u], v6.Communities);
    }

    // ---- #305: normalization is memoized per backing-array instance ----

    /// <summary>
    /// The memo keys by REFERENCE, so this is the case that would break if identity were mistaken
    /// for equivalence in the other direction: two routes carrying equal communities in DIFFERENT
    /// array instances normalize separately and must still land in one group, because
    /// <c>AttributeKey</c> compares structurally.
    /// </summary>
    [Fact]
    public void EqualCommunitiesInDistinctInstances_StillGroupTogether()
    {
        uint[] first = [0x65u, 0x100u];
        uint[] second = [0x100u, 0x65u];   // same set, different instance, different order
        Assert.NotSame(first, second);

        var result = _aggregator.Aggregate([
            R(0xC0A80000, 24, first),
            R(0xC0A80100, 24, second),
        ]);

        var route = Assert.Single(result);
        Assert.Equal((0xC0A80000u, (byte)23), (route.Prefix, route.PrefixLength));  // merged
        Assert.Equal([0x65u, 0x100u], route.Communities);
    }

    /// <summary>
    /// The shape the memo is built for: one resolved community array shared by every route from a
    /// source (what <c>RouteAssembler.MakeRoute</c> does), alongside a second source. The shared
    /// instance must not collapse the two sources together.
    /// </summary>
    [Fact]
    public void SharedCommunityInstances_GroupPerInstanceContent()
    {
        uint[] fromSourceA = [0xAAu];
        uint[] fromSourceB = [0xBBu];

        var routes = new List<Route>();
        for (var i = 0u; i < 8; i++)
            routes.Add(R(0xC0A80000 + (i * 256), 24, fromSourceA));
        for (var i = 0u; i < 8; i++)
            routes.Add(R(0x0A000000 + (i * 256), 24, fromSourceB));

        var result = _aggregator.Aggregate(routes);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Prefix == 0xC0A80000 && r.PrefixLength == 21 && r.Communities.SequenceEqual([0xAAu]));
        Assert.Contains(result, r => r.Prefix == 0x0A000000 && r.PrefixLength == 21 && r.Communities.SequenceEqual([0xBBu]));
    }

    /// <summary>
    /// Large communities go through their own memo, and the empty case short-circuits before either
    /// dictionary is created — so a set of routes mixing "has large communities" with "has none"
    /// must still separate correctly.
    /// </summary>
    [Fact]
    public void LargeCommunities_MemoizedSeparatelyFromRegularOnes()
    {
        (uint, uint, uint)[] large = [(65000u, 1u, 2u), (65000u, 1u, 1u)];

        var result = _aggregator.Aggregate([
            R(0xC0A80000, 24, [0x65u], large),
            R(0xC0A80100, 24, [0x65u], large),
            R(0xC0A80200, 24, [0x65u]),          // same regular set, no large communities
        ]);

        Assert.Equal(2, result.Count);
        var withLarge = Assert.Single(result, r => r.LargeCommunities.Count == 2);
        Assert.Equal((0xC0A80000u, (byte)23), (withLarge.Prefix, withLarge.PrefixLength));
        // Sorting is for the grouping key only — the emitted route carries the group template's
        // own array, so what goes on the wire keeps the order the source produced.
        Assert.Equal(large, withLarge.LargeCommunities);
        Assert.Single(result, r => r.LargeCommunities.Count == 0 && r.Prefix == 0xC0A80200);
    }

    [Fact]
    public void SendPath_GroupByCommunitySet_NeverMixesCommunities()
    {
        // The COMMUNITY attribute applies to every NLRI in an UPDATE, so the send path must
        // partition by community set — otherwise distinct groups get each other's communities.
        var routes = new List<Route>
        {
            R(0xC0A80100, 24, [0xC1u]),
            R(0xC0A80200, 24, [0xC2u]),
            R(0xC0A80300, 24, [0xC1u]),
        };

        var groups = RouteAssembler.GroupByCommunitySet(routes);

        Assert.Equal(2, groups.Count);
        foreach (var g in groups)
        {
            var first = g[0].Communities;
            Assert.All(g, r => Assert.Equal(first, r.Communities)); // no group mixes sets
        }
        Assert.Contains(groups, g => g.Count == 2 && g[0].Communities.Contains(0xC1u));
        Assert.Contains(groups, g => g.Count == 1 && g[0].Communities.Contains(0xC2u));
    }

    [Fact]
    public void GroupByCommunitySet_SingleCommunitySet_FastPathCollapsesToOneGroup()
    {
        // The common send-batch case: every route carries the same community set. Each entry
        // uses a distinct array instance with identical contents, so the short-circuit must
        // compare by value (not reference) and collapse the batch into a single group while
        // preserving original order — identical to what GroupBy would emit.
        var routes = new List<Route>
        {
            R(0xC0A80100, 24, [0xC1u, 0xC2u]),
            R(0xC0A80200, 24, [0xC1u, 0xC2u]),
            R(0xC0A80300, 24, [0xC1u, 0xC2u]),
        };

        var groups = RouteAssembler.GroupByCommunitySet(routes);

        var group = Assert.Single(groups);
        Assert.Equal(3, group.Count);
        Assert.Equal(0xC0A80100u, group[0].Prefix);
        Assert.Equal(0xC0A80200u, group[1].Prefix);
        Assert.Equal(0xC0A80300u, group[2].Prefix);
        Assert.All(group, r => Assert.Equal([0xC1u, 0xC2u], r.Communities));
    }

    [Fact]
    public void GroupByCommunitySet_EmptyCommunitiesAllShared_FastPathOneGroup()
    {
        // A batch with no communities anywhere is still a single community set (the empty set)
        // and must take the fast path → one group.
        var routes = new List<Route>
        {
            R(0x0A000100, 24),
            R(0x0A000200, 24),
        };

        var groups = RouteAssembler.GroupByCommunitySet(routes);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Count);
        Assert.All(group, r => Assert.Empty(r.Communities));
    }

    [Fact]
    public void GroupByCommunitySet_MixedSets_FallsBackToPartitioning()
    {
        // When the batch spans more than one community set the fast path must defer to the
        // GroupBy partition, preserving first-occurrence group order and per-group identity.
        var routes = new List<Route>
        {
            R(0xC0A80100, 24, [0xC1u]),
            R(0xC0A80200, 24, [0xC2u, 0xC3u]),
            R(0xC0A80300, 24, [0xC1u]),
        };

        var groups = RouteAssembler.GroupByCommunitySet(routes);

        Assert.Equal(2, groups.Count);
        Assert.Equal([0xC1u], groups[0][0].Communities);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal([0xC2u, 0xC3u], groups[1][0].Communities);
        Assert.Single(groups[1]);
    }

    [Fact]
    public void GroupByCommunitySet_EmptyBatch_ReturnsNoGroups()
    {
        Assert.Empty(RouteAssembler.GroupByCommunitySet([]));
    }

    [Fact]
    public void DifferentLargeCommunities_DoNotMerge()
    {
        // Adjacent aligned /24s, identical regular communities, but different Large Community
        // sets → stay separate (a single UPDATE cannot carry two LARGE_COMMUNITY values).
        var result = _aggregator.Aggregate([
            R(0xC0A80000, 24, [0x12345601u], [(2914u, 1u, 1u)]),
            R(0xC0A80100, 24, [0x12345601u], [(2914u, 2u, 2u)]),
        ]);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Prefix == 0xC0A80000 && r.LargeCommunities.SequenceEqual([(2914u, 1u, 1u)]));
        Assert.Contains(result, r => r.Prefix == 0xC0A80100 && r.LargeCommunities.SequenceEqual([(2914u, 2u, 2u)]));
    }

    [Fact]
    public void SameLargeCommunities_MergeAndPreserveLargeCommunity()
    {
        var large = new (uint, uint, uint)[] { (65000u, 100u, 200u), (2914u, 1u, 2u) };
        var result = _aggregator.Aggregate([
            R(0xC0A80000, 24, [0x65u], large),
            R(0xC0A80100, 24, [0x65u], [(2914u, 1u, 2u), (65000u, 100u, 200u)]), // same set, different order
        ]);
        var route = Assert.Single(result);
        Assert.Equal([(0xC0A80000u, (byte)23)], Pfx(result));
        // Set semantics: the key normalizes order, but the propagated value is the template's.
        Assert.Equal(large, route.LargeCommunities);
    }

    [Fact]
    public void Aggregation_PreservesLargeCommunities_OnMergedRoute()
    {
        var result = _aggregator.Aggregate([
            R(0x0A000000, 24, null, [(200000u, 1u, 1u)]),
            R(0x0A000100, 24, null, [(200000u, 1u, 1u)]),
        ]);
        var route = Assert.Single(result);
        Assert.Equal([(0x0A000000u, (byte)23)], Pfx(result));
        Assert.Equal([(200000u, 1u, 1u)], route.LargeCommunities);
    }

    [Fact]
    public void GroupByCommunitySet_SameRegular_DifferentLarge_StaysSeparate()
    {
        // Identical regular communities but distinct Large Community sets must NOT collapse: the
        // send path would otherwise tag one group's prefixes with the other's LARGE_COMMUNITY.
        var routes = new List<Route>
        {
            R(0xC0A80100, 24, [0xC1u], [(1u, 1u, 1u)]),
            R(0xC0A80200, 24, [0xC1u], [(2u, 2u, 2u)]),
            R(0xC0A80300, 24, [0xC1u], [(1u, 1u, 1u)]),
        };

        var groups = RouteAssembler.GroupByCommunitySet(routes);

        Assert.Equal(2, groups.Count);
        foreach (var g in groups)
        {
            var first = g[0].LargeCommunities;
            Assert.All(g, r => Assert.Equal(first, r.LargeCommunities));
        }
        Assert.Contains(groups, g => g.Count == 2 && g[0].LargeCommunities.Contains((1u, 1u, 1u)));
        Assert.Contains(groups, g => g.Count == 1 && g[0].LargeCommunities.Contains((2u, 2u, 2u)));
    }

    [Fact]
    public void GroupByCommunitySet_IdenticalRegularAndLarge_FastPathOneGroup()
    {
        // The common case: same regular and large community set on every route, via distinct
        // array instances → value comparison must collapse the batch into one group.
        var routes = new List<Route>
        {
            R(0xC0A80100, 24, [0xC1u], [(1u, 1u, 1u)]),
            R(0xC0A80200, 24, [0xC1u], [(1u, 1u, 1u)]),
        };

        var group = Assert.Single(RouteAssembler.GroupByCommunitySet(routes));
        Assert.Equal(2, group.Count);
        Assert.All(group, r => Assert.Equal([(1u, 1u, 1u)], r.LargeCommunities));
    }
}
