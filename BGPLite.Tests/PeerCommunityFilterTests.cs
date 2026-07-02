using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Routing;

namespace BGPLite.Tests;

public class PeerCommunityFilterTests
{
    private const uint LocalAsn = 65001;
    private static readonly PeerConfig EbgpPeer = new() { Address = "192.0.2.1", RemoteAsn = 65002 };
    private static readonly PeerConfig IbgpPeer = new() { Address = "192.0.2.2", RemoteAsn = LocalAsn };

    private static Route RouteWith(params uint[] communities) => new()
    {
        Prefix = 0xC0A80000,
        PrefixLength = 24,
        NextHop = 0x01020304,
        Communities = communities
    };

    private static PeerCommunityFilter NewFilter(HashSet<uint>? allowed = null) =>
        new(LocalAsn, _ => allowed ?? new HashSet<uint>());

    [Theory]
    [InlineData(BgpConstants.Community.NoExport)]
    [InlineData(BgpConstants.Community.NoAdvertise)]
    [InlineData(BgpConstants.Community.NoExportSubconfed)]
    public void WellKnownCommunity_BlocksOutgoing_OnEbgp(uint community)
    {
        var filter = NewFilter();

        Assert.False(filter.AcceptOutgoing(RouteWith(community), EbgpPeer));
    }

    [Theory]
    [InlineData(BgpConstants.Community.NoExport)]
    [InlineData(BgpConstants.Community.NoAdvertise)]
    [InlineData(BgpConstants.Community.NoExportSubconfed)]
    public void WellKnownCommunity_BlocksOutgoing_EvenWhenAllowed_OnEbgp(uint community)
    {
        var allowed = new HashSet<uint> { community };
        var filter = NewFilter(allowed);

        Assert.False(filter.AcceptOutgoing(RouteWith(community), EbgpPeer));
    }

    [Theory]
    [InlineData(BgpConstants.Community.NoExport)]
    [InlineData(BgpConstants.Community.NoAdvertise)]
    [InlineData(BgpConstants.Community.NoExportSubconfed)]
    public void WellKnownCommunity_BlocksOutgoing_EvenWhenMixedWithAllowed_OnEbgp(uint community)
    {
        var allowed = new HashSet<uint> { 0x0000FF01 };
        var filter = NewFilter(allowed);

        Assert.False(filter.AcceptOutgoing(
            RouteWith(0x0000FF01, community), EbgpPeer));
    }

    [Theory]
    [InlineData(BgpConstants.Community.NoExport)]
    [InlineData(BgpConstants.Community.NoExportSubconfed)]
    public void ExportCommunities_AreAllowed_OnIbgp(uint community)
    {
        var filter = NewFilter();

        Assert.True(filter.AcceptOutgoing(RouteWith(community), IbgpPeer));
    }

    [Fact]
    public void NoAdvertise_BlocksOnIbgp()
    {
        var filter = NewFilter();

        Assert.False(filter.AcceptOutgoing(RouteWith(BgpConstants.Community.NoAdvertise), IbgpPeer));
    }

    [Fact]
    public void ConfiguredCommunity_StillPasses_WhenNoWellKnownCommunity()
    {
        var allowed = new HashSet<uint> { 0x0000FF01 };
        var filter = NewFilter(allowed);

        Assert.True(filter.AcceptOutgoing(RouteWith(0x0000FF01), EbgpPeer));
    }

    [Fact]
    public void RouteWithoutCommunity_StillPasses_WhenNoFilterConfigured()
    {
        var filter = NewFilter();

        Assert.True(filter.AcceptOutgoing(RouteWith(), EbgpPeer));
    }

    [Fact]
    public void RouteWithDisallowedCommunity_IsRejected()
    {
        var allowed = new HashSet<uint> { 0x0000FF01 };
        var filter = NewFilter(allowed);

        Assert.False(filter.AcceptOutgoing(RouteWith(0x0000FF02), EbgpPeer));
    }
}
