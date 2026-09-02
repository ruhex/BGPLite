using System.Numerics;
using BGPLite.Protocol;

namespace BGPLite.Routing;

/// <summary>
/// Default <see cref="IPrefixAggregator"/>. Merges adjacent/overlapping prefixes into the
/// minimal equivalent set whose address range is EXACTLY the union of the inputs — never a
/// single address more. Dual-stack (#14 phase 3): IPv4 (/0..32) and IPv6 (/0..128) are
/// aggregated with the same exact-union semantics but never merged together — IPv4 and
/// IPv6 routes stay in separate groups even with identical tags (ADR 0001 §6). Injectable
/// as a strategy; inject <see cref="NoOpPrefixAggregator"/> to disable summarization.
/// </summary>
/// <remarks>
/// Algorithm: (1) mask host bits and form inclusive <c>[start, end]</c> intervals,
/// (2) sort and merge overlapping/adjacent intervals into a disjoint union, (3) emit the
/// minimal exact CIDR cover of each merged interval (range→CIDR). Because step 3 only
/// produces a /N block when its full span is present, it can never announce addresses
/// that were not in the input.
/// </remarks>
public sealed class ExactUnionPrefixAggregator : IPrefixAggregator
{
    public IReadOnlyList<Route> Aggregate(IEnumerable<Route> routes)
    {
        // #82: avoid the defensive ToList when the caller already owns a List<Route>.
        // The sole caller (RouteAssembler → SendRoutesAsync) passes a List<Route>, so the
        // `as List<Route>` fast path fires and the ToList allocation is skipped entirely.
        var source = routes as List<Route> ?? routes.ToList();
        if (source.Count == 0)
            return source;

        var result = new List<Route>(source.Count);

        // #82: manual single-pass partition instead of LINQ GroupBy. GroupBy allocates a
        // Lookup + per-group Lists; a Dictionary<AttributeKey, List<Route>> partitions in one
        // pass with the same semantics and less intermediate allocation. The groups preserve
        // encounter order (Dictionary maintains insertion order in .NET), matching GroupBy's
        // documented behavior for same-key elements.
        // Capacity is the expected number of DISTINCT community sets, not route count.
        // A typical send carries 1-5 community sets even with tens of thousands of routes.
        var groups = new Dictionary<AttributeKey, List<Route>>(4);
        // #305: normalization was per route — Distinct().ToArray() twice over, so four allocations
        // for every route on every send. RouteAssembler hands every route built from one source the
        // SAME community array instance, so a 60k-route dump normalizes a handful of distinct
        // instances tens of thousands of times. The normalizer memoizes by instance for the duration
        // of this call; it is a struct with lazily-created dictionaries, so a send whose routes carry
        // no communities allocates nothing for it at all.
        var normalizer = default(KeyNormalizer);
        foreach (var route in source)
        {
            var key = normalizer.KeyFor(route);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new List<Route>();
                groups[key] = group;
            }
            group.Add(route);
        }

        // Group by the attributes that survive to the wire. The outgoing path rewrites
        // AS_PATH (to the local ASN) and NEXT_HOP, so only Communities/LargeCommunities
        // distinguish otherwise-mergeable prefixes; prefixes carrying different communities
        // stay in separate groups so community information is never mixed during merging.
        foreach (var (key, group) in groups)
        {
            var template = group[0];
            var isIpv4 = key.IsIpv4;
            foreach (var (prefix, length) in AggregatePrefixes(group, isIpv4))
            {
                result.Add(new Route
                {
                    Prefix = prefix,
                    IsIpv4 = isIpv4,
                    PrefixLength = length,
                    NextHop = template.NextHop,
                    Communities = template.Communities,
                    LargeCommunities = template.LargeCommunities
                });
            }
        }

        return result;
    }

    /// <summary>Exact-union CIDR merge of the prefixes carried by a family-homogeneous group of
    /// routes (the group key includes <c>IsIpv4</c>, so every route here shares a family).</summary>
    private static List<(UInt128 Prefix, byte Length)> AggregatePrefixes(IReadOnlyList<Route> routes, bool isIpv4)
    {
        // 1. Mask host bits and build inclusive [start, end] intervals. UInt128 so an IPv6 /0 fits.
        var intervals = new List<(UInt128 Start, UInt128 End)>(routes.Count);
        for (var i = 0; i < routes.Count; i++)
        {
            var prefix = routes[i].Prefix;
            var length = routes[i].PrefixLength;
            if (!IpPrefix.IsValidLength(length, isIpv4)) continue; // defensive: skip malformed prefixes
            // /0 spans the whole address space: 2^128 itself overflows, so that end is stated
            // directly rather than computed as start + size - 1.
            var start = prefix & IpPrefix.Mask(length, isIpv4);
            var end = length == 0
                ? (isIpv4 ? (UInt128)0xFFFFFFFFu : UInt128.MaxValue)
                : start + ((UInt128)1 << (isIpv4 ? 32 : 128) - length) - 1;
            intervals.Add((start, end));
        }
        if (intervals.Count == 0)
            return [];

        // 2. Sort and merge overlapping/adjacent intervals into a disjoint union.
        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(UInt128 Start, UInt128 End)>(intervals.Count) { intervals[0] };
        for (var i = 1; i < intervals.Count; i++)
        {
            var (start, end) = intervals[i];
            var last = merged[^1];
            // Overlap, or directly adjacent — spelled without last.End + 1 because an interval
            // ending at UInt128.MaxValue would wrap the increment to zero.
            var adjacent = last.End != UInt128.MaxValue && start == last.End + 1;
            if (start <= last.End || adjacent)
            {
                if (end > last.End) merged[^1] = (last.Start, end);
            }
            else
            {
                merged.Add((start, end));
            }
        }

        // 3. Emit the minimal exact CIDR cover for each merged interval.
        var result = new List<(UInt128, byte)>();
        foreach (var (start, end) in merged)
            EmitRange(start, end, isIpv4, result);
        return result;
    }

    /// <summary>
    /// Emits the fewest CIDR blocks that exactly cover the inclusive <paramref name="start"/>
    /// .. <paramref name="end"/> range. At each step the block is the largest power-of-two
    /// that is both aligned at <c>start</c> and fits inside the remaining range, so the
    /// emitted blocks tile the range with neither gaps nor overlaps.
    /// </summary>
    private static void EmitRange(UInt128 start, UInt128 end, bool isIpv4, List<(UInt128, byte)> result)
    {
        var maxLength = isIpv4 ? 32 : 128;
        var maxAddress = isIpv4 ? (UInt128)0xFFFFFFFFu : UInt128.MaxValue;
        while (true)
        {
            // The whole address space is one /0 block; stating it directly keeps the span
            // computation below free of the 2^128 overflow.
            if (start == UInt128.Zero && end == maxAddress)
            {
                result.Add((UInt128.Zero, 0));
                return;
            }

            var span = end - start + 1;                                   // < 2^128 here
            // LeadingZeroCount/Log2 return UInt128 (IBinaryInteger); the values fit an int here
            // (span < 2^128 ⇒ lzc ≤ 127, size < 2^128 ⇒ log2 ≤ 127).
            var fits = UInt128.One << (127 - (int)UInt128.LeadingZeroCount(span)); // largest pow2 ≤ span
            var size = start == UInt128.Zero
                ? fits                                                    // 0 is aligned to anything
                : UInt128.Min(start & (~start + 1), fits);                // largest pow2 dividing start
            result.Add((start, (byte)(maxLength - (int)UInt128.Log2(size))));
            if (size == span)
                return;
            start += size;
        }
    }

    /// <summary>Value-equality key over a route's family and communities (communities sorted,
    /// set semantics). The family participates in the key so IPv4 and IPv6 prefixes never merge
    /// into one summary even with identical tags (ADR 0001 §6) — a 32-bit merge of an IPv6
    /// prefix would summarize the wrong address space.</summary>
    private readonly struct AttributeKey : IEquatable<AttributeKey>
    {
        private readonly uint[] _communities;
        private readonly (uint Global, uint Local1, uint Local2)[] _largeCommunities;

        public bool IsIpv4 { get; }

        private AttributeKey(uint[] communities,
            (uint Global, uint Local1, uint Local2)[] largeCommunities, bool isIpv4)
        {
            _communities = communities;
            _largeCommunities = largeCommunities;
            IsIpv4 = isIpv4;
        }

        // #238: Route collections are IReadOnlyList — the key holds privately-owned normalized
        // arrays so it never aliases a route's (shared) backing array. Building them is
        // KeyNormalizer's job.
        internal static AttributeKey Create(
            uint[] communities, (uint Global, uint Local1, uint Local2)[] largeCommunities,
            bool isIpv4) =>
            new(communities, largeCommunities, isIpv4);

        internal static int LargeCommunityComparison(
            (uint Global, uint Local1, uint Local2) a, (uint Global, uint Local1, uint Local2) b)
        {
            var c = a.Global.CompareTo(b.Global);
            if (c != 0) return c;
            c = a.Local1.CompareTo(b.Local1);
            if (c != 0) return c;
            return a.Local2.CompareTo(b.Local2);
        }

        public bool Equals(AttributeKey other)
        {
            if (IsIpv4 != other.IsIpv4) return false;
            if (_communities.Length != other._communities.Length) return false;
            for (var i = 0; i < _communities.Length; i++)
                if (_communities[i] != other._communities[i]) return false;
            if (_largeCommunities.Length != other._largeCommunities.Length) return false;
            for (var i = 0; i < _largeCommunities.Length; i++)
                if (_largeCommunities[i] != other._largeCommunities[i]) return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is AttributeKey other && Equals(other);

        public override int GetHashCode()
        {
            var hc = new HashCode();
            hc.Add(IsIpv4);
            foreach (var c in _communities) hc.Add(c);
            foreach (var l in _largeCommunities) hc.Add(l);
            return hc.ToHashCode();
        }
    }

    /// <summary>
    /// Builds <see cref="AttributeKey"/>s, memoizing each normalized set by the identity of the
    /// backing collection it came from, for the duration of a single <see cref="Aggregate"/> call
    /// (#305).
    /// <para>
    /// The memo is keyed by REFERENCE, not by content: <c>RouteAssembler.MakeRoute</c> passes one
    /// resolved community array to every route built from a source, so identity is exactly the
    /// "same set" signal, and comparing by content would reintroduce the per-route work the memo
    /// exists to remove. Distinct instances holding equal content simply normalize twice and then
    /// compare equal in <see cref="AttributeKey.Equals"/>, which is structural — so grouping is
    /// unchanged either way.
    /// </para>
    /// <para>
    /// A mutable struct with lazily-created dictionaries, held as a local by <see cref="Aggregate"/>:
    /// a send whose routes carry no communities at all — the common case for the seeded shared
    /// table — allocates nothing for it. Not thread-safe, and does not need to be: it never escapes
    /// the one call that created it.
    /// </para>
    /// </summary>
    private struct KeyNormalizer
    {
        private Dictionary<object, uint[]>? _communities;
        private Dictionary<object, (uint Global, uint Local1, uint Local2)[]>? _largeCommunities;

        public AttributeKey KeyFor(Route route) =>
            AttributeKey.Create(Normalize(route.Communities), NormalizeLarge(route.LargeCommunities),
                route.IsIpv4);

        /// <summary>Communities are a set: dedup and sort so set-equivalent routes key together.</summary>
        private uint[] Normalize(IReadOnlyList<uint> communities)
        {
            if (communities.Count == 0)
                return [];

            _communities ??= new Dictionary<object, uint[]>(4, ReferenceEqualityComparer.Instance);
            if (_communities.TryGetValue(communities, out var cached))
                return cached;

            var sorted = communities.Distinct().ToArray();
            Array.Sort(sorted);
            _communities[communities] = sorted;
            return sorted;
        }

        /// <summary>
        /// Large Communities are likewise a set: dedup and order by (Global, Local1, Local2). Value
        /// tuples have no <see cref="IComparable"/>, hence the explicit comparison.
        /// </summary>
        private (uint Global, uint Local1, uint Local2)[] NormalizeLarge(
            IReadOnlyList<(uint Global, uint Local1, uint Local2)> large)
        {
            if (large.Count == 0)
                return [];

            _largeCommunities ??= new Dictionary<object, (uint Global, uint Local1, uint Local2)[]>(
                4, ReferenceEqualityComparer.Instance);
            if (_largeCommunities.TryGetValue(large, out var cached))
                return cached;

            var distinct = large.Distinct().ToArray();
            Array.Sort(distinct, AttributeKey.LargeCommunityComparison);
            _largeCommunities[large] = distinct;
            return distinct;
        }
    }
}
