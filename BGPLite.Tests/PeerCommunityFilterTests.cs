using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Routing;

namespace BGPLite.Tests;

public class PeerCommunityFilterTests
{
    private const uint LocalAsn = 65001;
    private static readonly PeerConfig EbgpPeer = new() { Address = "192.0.2.1", RemoteAsn = 65002 };
    private static readonly PeerConfig IbgpPeer = new() { Address = "192.0.2.2", RemoteAsn = LocalAsn };
    private const string SharedIp = "192.0.2.10";

    private static Route RouteWith(params uint[] communities) => new()
    {
        Prefix = 0xC0A80000,
        PrefixLength = 24,
        NextHop = 0x01020304,
        Communities = communities
    };

    private static PeerCommunityFilter NewFilter(Func<string, uint?, CancellationToken, Task<HashSet<uint>>>? resolver = null) =>
        new(LocalAsn, resolver ?? ((_, _, _) => Task.FromResult(new HashSet<uint>())));

    // Exercises the resolve-once-per-send contract used by the advertise hot path: resolve the
    // peer's allow-set once, then make the per-route decision against the pre-resolved set.
    private static async Task<bool> Outgoing(PeerCommunityFilter filter, Route route, PeerConfig peer)
        => filter.AcceptOutgoing(route, peer, await filter.ResolveOutgoingAllowSetAsync(peer));

    [Theory]
    [InlineData(BgpConstants.Community.NoExport)]
    [InlineData(BgpConstants.Community.NoAdvertise)]
    [InlineData(BgpConstants.Community.NoExportSubconfed)]
    public async Task WellKnownCommunity_BlocksOutgoing_OnEbgp(uint community)
    {
        var filter = NewFilter();

        Assert.False(await Outgoing(filter, RouteWith(community), EbgpPeer));
    }

    [Fact]
    public async Task SameIp_DifferentAsn_UsesPeerSpecificCommunities()
    {
        var filter = NewFilter((ip, asn, _) =>
        {
            if (ip != SharedIp || !asn.HasValue) return Task.FromResult(new HashSet<uint>());
            return Task.FromResult(asn.Value switch
            {
                64512 => new HashSet<uint> { 0x0000FF01 },
                64513 => new HashSet<uint> { 0x0000FF02 },
                _ => new HashSet<uint>()
            });
        });

        var peerA = new PeerConfig { Address = SharedIp, RemoteAsn = 64512 };
        var peerB = new PeerConfig { Address = SharedIp, RemoteAsn = 64513 };

        Assert.True(await Outgoing(filter, RouteWith(0x0000FF01), peerA));
        Assert.False(await Outgoing(filter, RouteWith(0x0000FF01), peerB));
        Assert.True(await Outgoing(filter, RouteWith(0x0000FF02), peerB));
        Assert.False(await Outgoing(filter, RouteWith(0x0000FF02), peerA));
    }

    [Fact]
    public async Task NullRemoteAsn_FallsBackToIpOnlyCommunities()
    {
        var filter = NewFilter((ip, asn, _) => Task.FromResult(
            asn.HasValue
                ? new HashSet<uint> { 0x0000FF01 }
                : new HashSet<uint> { 0x0000FF03 }));

        var peerNullAsn = new PeerConfig { Address = SharedIp, RemoteAsn = null };

        Assert.True(await Outgoing(filter, RouteWith(0x0000FF03), peerNullAsn));
        Assert.False(await Outgoing(filter, RouteWith(0x0000FF01), peerNullAsn));
    }

    [Theory]
    [InlineData(BgpConstants.Community.NoExport)]
    [InlineData(BgpConstants.Community.NoAdvertise)]
    [InlineData(BgpConstants.Community.NoExportSubconfed)]
    public async Task WellKnownCommunity_BlocksOutgoing_EvenWhenAllowed_OnEbgp(uint community)
    {
        var allowed = new HashSet<uint> { community };
        var filter = NewFilter((_, _, _) => Task.FromResult(allowed));

        Assert.False(await Outgoing(filter, RouteWith(community), EbgpPeer));
    }

    [Theory]
    [InlineData(BgpConstants.Community.NoExport)]
    [InlineData(BgpConstants.Community.NoAdvertise)]
    [InlineData(BgpConstants.Community.NoExportSubconfed)]
    public async Task WellKnownCommunity_BlocksOutgoing_EvenWhenMixedWithAllowed_OnEbgp(uint community)
    {
        var allowed = new HashSet<uint> { 0x0000FF01 };
        var filter = NewFilter((_, _, _) => Task.FromResult(allowed));

        Assert.False(await Outgoing(filter, RouteWith(0x0000FF01, community), EbgpPeer));
    }

    [Theory]
    [InlineData(BgpConstants.Community.NoExport)]
    [InlineData(BgpConstants.Community.NoExportSubconfed)]
    public async Task ExportCommunities_AreAllowed_OnIbgp(uint community)
    {
        var filter = NewFilter();

        Assert.True(await Outgoing(filter, RouteWith(community), IbgpPeer));
    }

    [Fact]
    public async Task NoAdvertise_BlocksOnIbgp()
    {
        var filter = NewFilter();

        Assert.False(await Outgoing(filter, RouteWith(BgpConstants.Community.NoAdvertise), IbgpPeer));
    }

    [Fact]
    public async Task ConfiguredCommunity_StillPasses_WhenNoWellKnownCommunity()
    {
        var allowed = new HashSet<uint> { 0x0000FF01 };
        var filter = NewFilter((_, _, _) => Task.FromResult(allowed));

        Assert.True(await Outgoing(filter, RouteWith(0x0000FF01), EbgpPeer));
    }

    [Fact]
    public async Task RouteWithoutCommunity_StillPasses_WhenNoFilterConfigured()
    {
        // No allowlist = no filter: everything passes, tagged or not (D20 fast path).
        var filter = NewFilter();

        Assert.True(await Outgoing(filter, RouteWith(), EbgpPeer));
    }

    [Fact]
    public async Task RouteWithoutCommunity_IsRejected_WhenAllowlistActive()
    {
        // #389 (D20): a community-less route carries no consent tag — under an ACTIVE allowlist
        // it is denied, not unconditionally admitted. RED pre-fix: it passed the filter.
        var allowed = new HashSet<uint> { 0x0000FF01 };
        var filter = NewFilter((_, _, _) => Task.FromResult(allowed));

        Assert.False(await Outgoing(filter, RouteWith(), EbgpPeer));
        Assert.False(await Outgoing(filter, RouteWith(), IbgpPeer));
    }

    [Fact]
    public async Task RouteWithDisallowedCommunity_IsRejected()
    {
        var allowed = new HashSet<uint> { 0x0000FF01 };
        var filter = NewFilter((_, _, _) => Task.FromResult(allowed));

        Assert.False(await Outgoing(filter, RouteWith(0x0000FF02), EbgpPeer));
    }

    [Fact]
    public async Task Resolver_IsInvokedOncePerSend_NotPerRoute()
    {
        // #79: the community allow-set (a database roundtrip in production) must be resolved
        // ONCE per send, then reused for every per-route AcceptOutgoing decision — not once per
        // advertised route (~20k queries per refresh before the fix).
        var invocations = 0;
        var filter = NewFilter((_, _, _) =>
        {
            Interlocked.Increment(ref invocations);
            return Task.FromResult(new HashSet<uint> { 0x0000FF01 });
        });

        // One resolution for the whole send.
        var allowSet = await filter.ResolveOutgoingAllowSetAsync(EbgpPeer);
        Assert.Equal(1, invocations);

        // Many per-route decisions against the pre-resolved set must NOT re-invoke the resolver.
        for (var i = 0; i < 1000; i++)
            filter.AcceptOutgoing(RouteWith(0x0000FF01), EbgpPeer, allowSet);

        Assert.Equal(1, invocations);
    }
}
