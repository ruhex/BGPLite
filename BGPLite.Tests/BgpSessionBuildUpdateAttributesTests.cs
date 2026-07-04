using BGPLite.Protocol;
using BGPLite.Server;

namespace BGPLite.Tests;

/// <summary>
/// Covers the per-send UPDATE path-attribute cache introduced for #87: identical community
/// sets must reuse one built <see cref="List{T}"/> of <see cref="PathAttribute"/> across batches,
/// and the cached payload must serialize to the same bytes as a fresh build (no wire change).
/// </summary>
public class BgpSessionBuildUpdateAttributesTests
{
    private const uint Asn = 200000u;
    private const uint NextHop = 0x0A000001u; // 10.0.0.1

    [Fact]
    public void GetCachedUpdateAttributes_EqualCommunitySet_ReturnsSameInstance()
    {
        // Distinct array instances holding the same community values — the cache must treat them
        // as one key and hand back the already-built list (reference-equal).
        var cache = BgpSession.CreateUpdateAttributeCache();
        var communitiesA = new uint[] { 1234u, 5678u };
        var communitiesB = new uint[] { 1234u, 5678u };

        var first = BgpSession.GetCachedUpdateAttributes(Asn, localFourByteAsn: true, NextHop, communitiesA, cache);
        var second = BgpSession.GetCachedUpdateAttributes(Asn, localFourByteAsn: true, NextHop, communitiesB, cache);

        Assert.Same(first, second);
        Assert.Single(cache);
    }

    [Fact]
    public void GetCachedUpdateAttributes_DistinctCommunitySets_BuildsOnceEach()
    {
        var cache = BgpSession.CreateUpdateAttributeCache();

        var withComms = BgpSession.GetCachedUpdateAttributes(Asn, localFourByteAsn: true, NextHop, [1234u], cache);
        var noComms = BgpSession.GetCachedUpdateAttributes(Asn, localFourByteAsn: true, NextHop, [], cache);
        var withCommsAgain = BgpSession.GetCachedUpdateAttributes(Asn, localFourByteAsn: true, NextHop, [1234u], cache);

        Assert.NotSame(withComms, noComms);
        Assert.Same(withComms, withCommsAgain);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void GetCachedUpdateAttributes_CachedMatchesFreshBuild_OnTheWire()
    {
        // The cached list is reused across batches; assert it serializes byte-identically to a
        // fresh BuildUpdateAttributes call so caching introduces no behavior change (#87).
        var cache = BgpSession.CreateUpdateAttributeCache();
        var communities = new uint[] { 1234u, 5678u };

        var cached = BgpSession.GetCachedUpdateAttributes(Asn, localFourByteAsn: false, NextHop, communities, cache);
        var fresh = BgpSession.BuildUpdateAttributes(Asn, localFourByteAsn: false, NextHop, communities);

        Assert.Equal(SerializeAttributes(fresh), SerializeAttributes(cached));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetCachedUpdateAttributes_ReusedAcrossManyBatches_StaysStable(bool localFourByteAsn)
    {
        // Models the real send path: many sequential lookups for the same community set, as the
        // 100-NLRI batches of one send would issue. Every result must be the same instance and
        // keep serializing to the original bytes.
        var cache = BgpSession.CreateUpdateAttributeCache();
        var communities = new uint[] { 999u };

        var first = BgpSession.GetCachedUpdateAttributes(Asn, localFourByteAsn, NextHop, communities, cache);
        var firstBytes = SerializeAttributes(first);

        for (var i = 0; i < 50; i++)
        {
            var again = BgpSession.GetCachedUpdateAttributes(Asn, localFourByteAsn, NextHop, new uint[] { 999u }, cache);
            Assert.Same(first, again);
            Assert.Equal(firstBytes, SerializeAttributes(again));
        }

        Assert.Single(cache);
    }

    private static byte[] SerializeAttributes(List<PathAttribute> attrs)
    {
        // Wrap in a minimal UPDATE and serialize via the real writer — this is exactly the bytes
        // that go on the wire for the attribute section.
        var msg = new BgpUpdateMessage { PathAttributes = attrs };
        var buffer = new byte[BgpMessageWriter.GetBufferSize(msg)];
        BgpMessageWriter.WriteMessage(msg, buffer);
        return buffer;
    }
}
