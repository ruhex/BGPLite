using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// Tests for RFC 6793 compliance — AS4_PATH handling for 2-byte-only peers.
/// </summary>
public class BgpSessionRfc6793Tests
{
    [Fact]
    public void GroupByCommunitySet_GroupsRoutesWithEmptyCommunitySet()
    {
        // Verify that BgpSession.GroupByCommunitySet works correctly
        // (static method, no session state needed)
        var routes = new List<Route>
        {
            new() { Prefix = 0xC0A80000, PrefixLength = 24, Communities = [] },
            new() { Prefix = 0x0A000000, PrefixLength = 8, Communities = [] }
        };

        var groups = BgpSession.GroupByCommunitySet(routes);
        Assert.Single(groups); // All routes share empty community set
        Assert.Equal(2, groups[0].Count);
    }

    [Fact]
    public void WriteAsPath_2ByteMode_WithLargeAsn_ProducesAsTrans()
    {
        // When local ASN > 65535 and peer is 2-byte-only, AS_PATH should use AS_TRANS
        var localAsn = 200000u;
        var asPathAsn = localAsn > ushort.MaxValue ? BgpConstants.AsPathAsTrans : localAsn;

        Assert.Equal(BgpConstants.AsPathAsTrans, asPathAsn);

        var asPath = AttributeHelper.WriteAsPath([asPathAsn], fourByteAsn: false);
        var readAses = AttributeHelper.ReadAsPath(asPath, fourByteAsn: false);
        Assert.Equal([23456u], readAses);
    }

    [Fact]
    public void WriteAs4Path_WithLargeAsn_ProducesCorrectAttribute()
    {
        // AS4_PATH should carry the true 4-byte ASN
        var localAsn = 200000u;
        var as4Path = AttributeHelper.WriteAs4Path([localAsn]);

        Assert.Equal(BgpConstants.Attribute.As4Path, as4Path.TypeCode);
        // RFC 6793: AS4_PATH is optional transitive (FlagOptional | FlagTransitive)
        Assert.Equal(BgpConstants.Attribute.FlagOptional | BgpConstants.Attribute.FlagTransitive, as4Path.Flags);
        var readAses = AttributeHelper.ReadAs4Path(as4Path);
        Assert.Equal([localAsn], readAses);
    }

    [Fact]
    public void UpdateAttributes_2BytePeer_WithLargeLocalAsn_HasAs4Path()
    {
        // Simulate what SendRouteBatchAsync should produce for a 2-byte-only peer
        // when local ASN > 65535
        var localAsn = 200000u;
        var is2BytePeer = true; // _localFourByteAsn = false → 2-byte-only peer

        var attrs = new List<PathAttribute>
        {
            AttributeHelper.WriteOrigin(BgpOrigin.Igp),
            AttributeHelper.WriteNextHop(0xC0A80101)
        };

        if (is2BytePeer)
        {
            var asPathAsn = localAsn > ushort.MaxValue ? BgpConstants.AsPathAsTrans : localAsn;
            attrs.Insert(1, AttributeHelper.WriteAsPath([asPathAsn], fourByteAsn: false));

            if (localAsn > ushort.MaxValue)
                attrs.Add(AttributeHelper.WriteAs4Path([localAsn]));
        }
        else
        {
            attrs.Insert(1, AttributeHelper.WriteAsPath([localAsn], fourByteAsn: true));
        }

        // Verify AS_PATH contains AS_TRANS
        var asPathAttr = attrs.First(a => a.TypeCode == BgpConstants.Attribute.AsPath);
        var asPathAses = AttributeHelper.ReadAsPath(asPathAttr, fourByteAsn: false);
        Assert.Equal([23456u], asPathAses);

        // Verify AS4_PATH is present with true ASN
        var as4PathAttr = attrs.FirstOrDefault(a => a.TypeCode == BgpConstants.Attribute.As4Path);
        Assert.NotNull(as4PathAttr);
        var as4PathAses = AttributeHelper.ReadAs4Path(as4PathAttr!);
        Assert.Equal([200000u], as4PathAses);
    }

    [Fact]
    public void UpdateAttributes_4BytePeer_NoAs4Path()
    {
        // 4-byte peer should receive only 4-byte AS_PATH, no AS4_PATH
        var localAsn = 200000u;
        var is2BytePeer = false;

        var attrs = new List<PathAttribute>
        {
            AttributeHelper.WriteOrigin(BgpOrigin.Igp),
            AttributeHelper.WriteNextHop(0xC0A80101)
        };

        if (is2BytePeer)
        {
            var asPathAsn = localAsn > ushort.MaxValue ? BgpConstants.AsPathAsTrans : localAsn;
            attrs.Insert(1, AttributeHelper.WriteAsPath([asPathAsn], fourByteAsn: false));

            if (localAsn > ushort.MaxValue)
                attrs.Add(AttributeHelper.WriteAs4Path([localAsn]));
        }
        else
        {
            attrs.Insert(1, AttributeHelper.WriteAsPath([localAsn], fourByteAsn: true));
        }

        // Verify AS_PATH contains true 4-byte ASN
        var asPathAttr = attrs.First(a => a.TypeCode == BgpConstants.Attribute.AsPath);
        var asPathAses = AttributeHelper.ReadAsPath(asPathAttr, fourByteAsn: true);
        Assert.Equal([200000u], asPathAses);

        // Verify AS4_PATH is NOT present
        var as4PathAttr = attrs.FirstOrDefault(a => a.TypeCode == BgpConstants.Attribute.As4Path);
        Assert.Null(as4PathAttr);
    }

    [Fact]
    public void UpdateAttributes_2BytePeer_With2ByteLocalAsn_NoAs4Path()
    {
        // 2-byte peer with 2-byte local ASN: no AS4_PATH needed
        var localAsn = 65001u;
        var is2BytePeer = true;

        var attrs = new List<PathAttribute>
        {
            AttributeHelper.WriteOrigin(BgpOrigin.Igp),
            AttributeHelper.WriteNextHop(0xC0A80101)
        };

        if (is2BytePeer)
        {
            var asPathAsn = localAsn > ushort.MaxValue ? BgpConstants.AsPathAsTrans : localAsn;
            attrs.Insert(1, AttributeHelper.WriteAsPath([asPathAsn], fourByteAsn: false));

            if (localAsn > ushort.MaxValue)
                attrs.Add(AttributeHelper.WriteAs4Path([localAsn]));
        }
        else
        {
            attrs.Insert(1, AttributeHelper.WriteAsPath([localAsn], fourByteAsn: true));
        }

        // Verify AS_PATH contains the 2-byte ASN
        var asPathAttr = attrs.First(a => a.TypeCode == BgpConstants.Attribute.AsPath);
        var asPathAses = AttributeHelper.ReadAsPath(asPathAttr, fourByteAsn: false);
        Assert.Equal([65001u], asPathAses);

        // Verify AS4_PATH is NOT present (local ASN <= 65535)
        var as4PathAttr = attrs.FirstOrDefault(a => a.TypeCode == BgpConstants.Attribute.As4Path);
        Assert.Null(as4PathAttr);
    }
}
