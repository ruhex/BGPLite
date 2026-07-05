using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BGPLite.Tests;

/// <summary>
/// Unit coverage for the per-peer user-source send path added in #147 (epic #143). Exercises
/// <see cref="BgpSession.AddUserSourceRoutesAsync"/> directly with fakes — no live BgpSession/socket —
/// mirroring the static-helper convention used by the other BgpSession tests.
/// </summary>
public class BgpSessionUserSourceTests
{
    private static readonly uint[] Comms = [CommunityCodec.Parse("65000:42")];

    /// <summary>Captures the resolved <see cref="CommunitySource"/> and returns a fixed community set.</summary>
    private sealed class StubResolver : ICommunityResolver
    {
        public CommunitySource? Last { get; private set; }
        public uint[] Resolve(CommunitySource source)
        {
            Last = source;
            return Comms;
        }
    }

    /// <summary>A minimal <see cref="IPrefixService"/> that only implements the user-source path.</summary>
    private sealed class StubPrefixService : IPrefixService
    {
        public IReadOnlyList<(uint Prefix, byte Length)> Result { get; set; } = [];
        public Exception? Throw { get; set; }
        public (string Name, string Url, string? Community)? Last { get; private set; }

        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetUserSourcePrefixesAsync(
            string name, string url, string? community, CancellationToken ct = default)
        {
            Last = (name, url, community);
            if (Throw is not null) throw Throw;
            return Task.FromResult(Result);
        }

        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetPrefixesAsync(uint asn, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task<List<(uint Prefix, byte Length, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns, CancellationToken ct = default)
            => Task.FromResult(new List<(uint Prefix, byte Length, uint Asn)>());
        public Task<int> GetPrefixCountAsync(uint asn, CancellationToken ct = default) => Task.FromResult(0);
        public Task<List<(uint Prefix, byte Length, uint Asn)>> GetRuPrefixesAsync(CancellationToken ct = default)
            => Task.FromResult(new List<(uint Prefix, byte Length, uint Asn)>());
        public Task<IReadOnlyList<(uint Prefix, byte Length)>> GetSourcePrefixesAsync(string name, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(uint Prefix, byte Length)>>([]);
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Success_AddsRoutesWithResolvedCommunity()
    {
        var svc = new StubPrefixService { Result = [(0xC0A80000u, (byte)24)] };
        var resolver = new StubResolver();
        var routes = new List<Route>();

        await BgpSession.AddUserSourceRoutesAsync(
            routes, new CustomSourceView("msft", "https://example.com/x.txt", "65000:42"),
            0x01020304u, svc, resolver, NullLogger.Instance, "peer", CancellationToken.None);

        var route = Assert.Single(routes);
        Assert.Equal(0xC0A80000u, route.Prefix);
        Assert.Equal((byte)24, route.PrefixLength);
        Assert.Equal(0x01020304u, route.NextHop);
        Assert.Empty(route.AsPath);                  // URL lists have no per-prefix origin AS (like CustomPrefixes)
        Assert.Equal(Comms, route.Communities);
        // The resolver saw a UserSource kind, and the prefix service got the source's name/url/community.
        Assert.Equal(CommunitySourceKind.UserSource, resolver.Last!.Kind);
        Assert.Equal(("msft", "https://example.com/x.txt", "65000:42"), svc.Last);
    }

    [Fact]
    public async Task ProviderThrows_OtherSourceUnaffected()
    {
        // Per-source try/catch: one failing URL must not drop another source's prefixes.
        var routes = new List<Route>();
        var throwSvc = new StubPrefixService { Throw = new InvalidOperationException("boom") };
        var okSvc = new StubPrefixService { Result = [(0x0A000000u, (byte)8)] };

        await BgpSession.AddUserSourceRoutesAsync(
            routes, new CustomSourceView("bad", "https://x/b", null), 1, throwSvc, new StubResolver(), NullLogger.Instance, "peer", CancellationToken.None);
        await BgpSession.AddUserSourceRoutesAsync(
            routes, new CustomSourceView("good", "https://x/g", null), 1, okSvc, new StubResolver(), NullLogger.Instance, "peer", CancellationToken.None);

        Assert.Single(routes);                       // only the second source's prefix made it
    }

    [Fact]
    public async Task OperationCanceled_Propagates()
    {
        // Regression for #114: cancellation must NOT be swallowed by the per-source catch.
        var svc = new StubPrefixService { Throw = new OperationCanceledException() };
        var routes = new List<Route>();

        await Assert.ThrowsAsync<OperationCanceledException>(() => BgpSession.AddUserSourceRoutesAsync(
            routes, new CustomSourceView("c", "https://x/c", null), 1, svc, new StubResolver(),
            NullLogger.Instance, "peer", CancellationToken.None));
    }

    [Fact]
    public async Task ProviderReturnsEmpty_AddsNoRoutes()
    {
        var svc = new StubPrefixService { Result = [] };
        var routes = new List<Route>();

        await BgpSession.AddUserSourceRoutesAsync(
            routes, new CustomSourceView("empty", "https://x/e", null), 1, svc, new StubResolver(),
            NullLogger.Instance, "peer", CancellationToken.None);

        Assert.Empty(routes);
    }
}
