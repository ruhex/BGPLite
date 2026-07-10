using BGPLite.Configuration;
using BGPLite.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

public class PrefixSourceServiceTests
{
    private sealed class CountingProvider : IPrefixSourceProvider
    {
        public string Kind => "stub";
        public int Calls { get; private set; }
        private readonly IReadOnlyList<(uint, byte)> _list;
        public CountingProvider(IReadOnlyList<(uint, byte)> list) => _list = list;

        public Task<SourceLoadResult> LoadAsync(PrefixSourceConfig source, string? etag = null, DateTimeOffset? lastModified = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(SourceLoadResult.Ok(_list));
        }
    }

    private sealed class ThrowingProvider : IPrefixSourceProvider
    {
        public string Kind => "stub";
        public Task<SourceLoadResult> LoadAsync(PrefixSourceConfig source, string? etag = null, DateTimeOffset? lastModified = null, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }

    private static AppConfig ConfigWith(params string[] names)
    {
        var yaml = "Bgp:\n  Asn: 65444\n  RouterId: 10.0.0.1\nPrefixSources:\n";
        foreach (var name in names)
            yaml += $"  - Name: {name}\n    Kind: stub\n";
        return ConfigLoader.LoadFromText(yaml);
    }

    [Fact]
    public async Task GetAsync_CachesWithinTtl()
    {
        var provider = new CountingProvider([(1u, (byte)24)]);
        var svc = new PrefixSourceService(
            ConfigWith("ru"),
            new PrefixSourceProviderFactory([provider]),
            NullLogger<PrefixSourceService>.Instance);

        await svc.GetAsync("ru");
        await svc.GetAsync("ru");

        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task GetAsync_UnknownNameReturnsEmpty()
    {
        var svc = new PrefixSourceService(
            ConfigWith("ru"),
            new PrefixSourceProviderFactory([new CountingProvider([(1u, (byte)24)])]),
            NullLogger<PrefixSourceService>.Instance);

        Assert.Empty(await svc.GetAsync("nope"));
    }

    [Fact]
    public async Task GetDefaultAsync_ResolvesByName()
    {
        var provider = new CountingProvider([(1u, (byte)24), (2u, (byte)16)]);
        var yaml = "Bgp:\n  Asn: 65444\n  RouterId: 10.0.0.1\n" +
                   "PrefixSources:\n  - Name: ru\n    Kind: stub\nDefaultPrefixSource: ru\n";
        var svc = new PrefixSourceService(
            ConfigLoader.LoadFromText(yaml),
            new PrefixSourceProviderFactory([provider]),
            NullLogger<PrefixSourceService>.Instance);

        var result = await svc.GetDefaultAsync();
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetDefaultAsync_UnsetReturnsEmpty()
    {
        var svc = new PrefixSourceService(
            ConfigWith("ru"),
            new PrefixSourceProviderFactory([new CountingProvider([(1u, (byte)24)])]),
            NullLogger<PrefixSourceService>.Instance);

        Assert.Empty(await svc.GetDefaultAsync());
    }

    [Fact]
    public async Task LoadAllAsync_SkipsFailedSource()
    {
        var svc = new PrefixSourceService(
            ConfigWith("a", "b"),
            new PrefixSourceProviderFactory([new ThrowingProvider()]),
            NullLogger<PrefixSourceService>.Instance);

        var all = await svc.LoadAllAsync();

        Assert.Equal(2, all.Count);
        Assert.All(all, x => Assert.Empty(x.Prefixes));
    }

    [Fact]
    public async Task WarmUpAsync_PrimesAllSources()
    {
        var provider = new CountingProvider([(1u, (byte)24)]);
        var svc = new PrefixSourceService(
            ConfigWith("a", "b"),
            new PrefixSourceProviderFactory([provider]),
            NullLogger<PrefixSourceService>.Instance);

        await svc.WarmUpAsync();

        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task GetAsync_RefetchesAfterTtlExpiry()
    {
        var provider = new CountingProvider([(1u, (byte)24)]);
        var svc = new PrefixSourceService(
            ConfigWith("ru"),
            new PrefixSourceProviderFactory([provider]),
            NullLogger<PrefixSourceService>.Instance,
            cacheTtl: TimeSpan.Zero);

        await svc.GetAsync("ru");
        await svc.GetAsync("ru");

        Assert.Equal(2, provider.Calls);
    }

    /// <summary>Succeeds on the first load, then throws — used to exercise stale-serve.</summary>
    private sealed class ToggleProvider : IPrefixSourceProvider
    {
        public string Kind => "stub";
        private int _calls;
        private readonly IReadOnlyList<(uint, byte)> _first;
        public ToggleProvider(IReadOnlyList<(uint, byte)> first) => _first = first;

        public Task<SourceLoadResult> LoadAsync(PrefixSourceConfig source, string? etag = null, DateTimeOffset? lastModified = null, CancellationToken ct = default)
        {
            _calls++;
            return _calls == 1
                ? Task.FromResult(SourceLoadResult.Ok(_first))
                : throw new InvalidOperationException("boom");
        }
    }

    [Fact]
    public async Task GetAsync_ServesStaleOnFailure()
    {
        var provider = new ToggleProvider([(1u, (byte)24), (2u, (byte)16)]);
        var svc = new PrefixSourceService(
            ConfigWith("ru"),
            new PrefixSourceProviderFactory([provider]),
            NullLogger<PrefixSourceService>.Instance,
            cacheTtl: TimeSpan.Zero);

        var first = await svc.GetAsync("ru");
        Assert.Equal(2, first.Count);

        // TTL=0 forces a refetch; the provider now throws, so the cached (stale) copy is served.
        var second = await svc.GetAsync("ru");
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public async Task GetAsync_FailedProviderReturnsEmpty()
    {
        var svc = new PrefixSourceService(
            ConfigWith("ru"),
            new PrefixSourceProviderFactory([new ThrowingProvider()]),
            NullLogger<PrefixSourceService>.Instance);

        Assert.Empty(await svc.GetAsync("ru"));
    }

    [Fact]
    public void Constructor_RejectsDuplicateSourceNames()
    {
        var yaml = "Bgp:\n  Asn: 65444\n  RouterId: 10.0.0.1\n" +
                   "PrefixSources:\n  - Name: ru\n    Kind: stub\n  - Name: ru\n    Kind: stub\n";

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new PrefixSourceService(
                ConfigLoader.LoadFromText(yaml),
                new PrefixSourceProviderFactory([new CountingProvider([(1u, (byte)24)])]),
                NullLogger<PrefixSourceService>.Instance));

        Assert.Contains("ru", ex.Message);
    }
}
