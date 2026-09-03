using BGPLite.Configuration;
using BGPLite.Providers;
using BGPLite.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// #452: the RU/default projection (<c>PrefixService._ruCache</c>) is a second cache layer with
/// its own 1h TTL above the source-level cache. A committed content change to the DEFAULT source
/// must be visible to the next route rebuild — including (and especially) on the auto-refresh
/// path, where <see cref="IPrefixSourceService.RefreshAsync"/> is deliberately callback-free
/// (#214): without an invalidation signal the fleet rebuild re-reads the stale fast path and
/// re-advertises the old RU set for up to the RU TTL.
/// </summary>
public sealed class PrefixServiceRuInvalidationTests
{
    /// <summary>Source provider whose content the test mutates between loads.</summary>
    private sealed class MutableProvider(IReadOnlyList<IpPrefix> initial) : IPrefixSourceProvider
    {
        public string Kind => "http";
        public bool SupportsConditionalRequests => false;
        public IReadOnlyList<IpPrefix> Content { get; set; } = initial;

        public Task<SourceLoadResult> LoadAsync(PrefixSourceConfig source, string? etag = null, DateTimeOffset? lastModified = null, CancellationToken ct = default) =>
            Task.FromResult(SourceLoadResult.Ok(Content));
    }

    private static readonly IpPrefix InitialPrefix = new(0x0A000000u, 8);   // 10.0.0.0/8
    private static readonly IpPrefix UpdatedPrefix = new(0x0A000100u, 24);  // 10.0.1.0/24

    private static (PrefixService Service, PrefixSourceService Sources, MutableProvider Provider, Func<int> Pushes) Build()
    {
        var provider = new MutableProvider([InitialPrefix]);
        var yaml = """
                   Bgp:
                     Asn: 65444
                     RouterId: 10.0.0.1
                   DefaultPrefixSource: ru
                   PrefixSources:
                     - Name: ru
                       Kind: http
                       Url: https://example.com/ru.txt
                     - Name: other
                       Kind: http
                       Url: https://example.com/other.txt
                   """;
        var config = ConfigLoader.LoadFromText(yaml);
        var sources = new PrefixSourceService(
            config,
            new PrefixSourceProviderFactory([provider]),
            NullLogger<PrefixSourceService>.Instance);
        var pushes = 0;
        var service = new PrefixService(
            config,
            new RipeStatPrefixCache(
                new RipeStatProvider(new ThrowingFactory(), NullLogger<RipeStatProvider>.Instance),
                NullLogger<RipeStatPrefixCache>.Instance),
            sources,
            null!, // HttpPrefixProvider is only on the per-peer user-source path (#263)
            onSourceChanged: _ =>
            {
                Interlocked.Increment(ref pushes);
                return Task.CompletedTask;
            });
        return (service, sources, provider, () => Volatile.Read(ref pushes));
    }

    private sealed class ThrowingFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException("not on the RU path");
    }

    [Fact]
    public async Task DefaultSourceChange_AutoRefreshPath_RebuildsRuProjectionImmediately()
    {
        var (service, sources, provider, pushes) = Build();

        // 1. Warm the RU projection — cold load caches the initial content for the full RU TTL
        //    (and legitimately fires the #416 convergence push once — record the baseline).
        var first = await service.GetRuPrefixesAsync();
        Assert.Single(first);
        Assert.Equal(InitialPrefix.Address, first[0].Prefix);
        Assert.Equal(InitialPrefix.Length, first[0].Length);
        var baselinePushes = pushes();

        // 2. The source content changes and the auto-refresh timer force-refreshes the source
        //    (RefreshAsync — the callback-free path; the fleet push happens afterwards).
        provider.Content = [UpdatedPrefix];
        Assert.True(await sources.RefreshAsync("ru"));

        // 3. The rebuild that the auto-refresh push triggers must advertise the NEW content —
        //    pre-#452 this returned the stale projection until the RU TTL elapsed.
        var rebuilt = await service.GetRuPrefixesAsync();
        Assert.Single(rebuilt);
        Assert.Equal(UpdatedPrefix.Address, rebuilt[0].Prefix);
        Assert.Equal(UpdatedPrefix.Length, rebuilt[0].Length);

        // 4. The auto-refresh path stays callback-free (#214 — no double push): the only push so
        //    far is the warm-up's own; the invalidation signal must not turn RefreshAsync into
        //    a pusher.
        Assert.Equal(baselinePushes, pushes());
    }

    [Fact]
    public async Task DefaultSourceChange_ConnectPathStillReportsChanged_ExactlyOnce()
    {
        var (service, sources, provider, pushes) = Build();

        // Warm the projection, then change the source. (The cold load fires its own #416 push —
        // that is the baseline; the assertions below check for no ADDITIONAL push.)
        await service.GetRuPrefixesAsync();
        var baselinePushes = pushes();
        provider.Content = [UpdatedPrefix];

        // Auto-refresh commits the new content (invalidation fires here).
        Assert.True(await sources.RefreshAsync("ru"));

        // A subsequent connect-path load reads the ALREADY-NEW source cache: changed=false —
        // the invalidation must not fabricate a phantom change (no duplicate fleet push).
        var (_, changed) = await sources.LoadDefaultAsync();
        Assert.False(changed);
        Assert.Equal(baselinePushes, pushes());

        // And the projection serves the new content.
        var rebuilt = await service.GetRuPrefixesAsync();
        Assert.Equal(UpdatedPrefix.Address, rebuilt[0].Prefix);
    }

    [Fact]
    public async Task NonDefaultSourceChange_DoesNotInvalidateRuProjection()
    {
        var (service, sources, provider, pushes) = Build();
        await service.GetRuPrefixesAsync();
        var baselinePushes = pushes();

        // A change to a DIFFERENT source (same PrefixSourceService instance) is invisible to the
        // RU projection — the committed-change signal must not tear a projection it doesn't feed.
        provider.Content = [UpdatedPrefix];
        Assert.True(await sources.RefreshAsync("other"));

        var cached = await service.GetRuPrefixesAsync();
        Assert.Equal(InitialPrefix.Address, cached[0].Prefix);
        Assert.Equal(baselinePushes, pushes());
    }
}
