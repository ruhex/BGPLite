using BGPLite.Routing;
using BGPLite.Server;

namespace BGPLite.Tests;

public class PrefixAggregatorTests
{
    private const uint NextHop = 0x01020304;
    private readonly IPrefixAggregator _aggregator = new ExactUnionPrefixAggregator();

    private static Route R(uint prefix, byte length, uint[]? communities = null) =>
        new() { Prefix = prefix, PrefixLength = length, NextHop = NextHop, Communities = communities ?? [] };

    private static List<(uint Prefix, byte Length)> Pfx(IReadOnlyList<Route> routes) =>
        routes.Select(r => (r.Prefix, r.PrefixLength)).ToList();

    /// <summary>Independent reference implementation: the sorted, merged [start,end] intervals
    /// of a prefix set. Used to cross-check that aggregation adds no address and drops none.</summary>
    private static List<(ulong Start, ulong End)> UnionRanges(IEnumerable<(uint Prefix, byte Length)> prefixes)
    {
        var intervals = new List<(ulong Start, ulong End)>();
        foreach (var (prefix, length) in prefixes)
        {
            if (length > 32) continue;
            var mask = length == 0 ? 0u : (0xFFFFFFFFu << (32 - length));
            var start = (ulong)(prefix & mask);
            var size = length == 0 ? (1UL << 32) : (1UL << (32 - length));
            intervals.Add((start, start + size - 1));
        }
        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(ulong Start, ulong End)>();
        foreach (var (s, e) in intervals)
        {
            if (merged.Count > 0 && s <= merged[^1].End + 1)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, e));
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

        var groups = BgpSession.GroupByCommunitySet(routes);

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

        var groups = BgpSession.GroupByCommunitySet(routes);

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

        var groups = BgpSession.GroupByCommunitySet(routes);

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

        var groups = BgpSession.GroupByCommunitySet(routes);

        Assert.Equal(2, groups.Count);
        Assert.Equal([0xC1u], groups[0][0].Communities);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal([0xC2u, 0xC3u], groups[1][0].Communities);
        Assert.Single(groups[1]);
    }

    [Fact]
    public void GroupByCommunitySet_EmptyBatch_ReturnsNoGroups()
    {
        Assert.Empty(BgpSession.GroupByCommunitySet([]));
    }
}
