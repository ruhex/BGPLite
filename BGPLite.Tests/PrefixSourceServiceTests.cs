using BGPLite.Configuration;
using BGPLite.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

public class PrefixSourceServiceTests
{
    private sealed class CountingProvider : IPrefixSourceProvider
    {
        public string Kind => "stub";
        public bool SupportsConditionalRequests => true;
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
        public bool SupportsConditionalRequests => true;
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
        public bool SupportsConditionalRequests => true;
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

    /// <summary>
    /// Returns a different prefix list on each call — used to exercise #214 change-detection in
    /// RefreshAsync (the first call populates the cache, subsequent calls return a new list that must
    /// be detected as "changed"). Yields before returning so the call is genuinely async (mirrors real
    /// HttpPrefixProvider/AsnPrefixProvider) — required for the TOCTOU test to actually exercise the
    /// race where a concurrent GetAsync may enter the per-source gate first.
    /// </summary>
    private sealed class SequenceProvider : IPrefixSourceProvider
    {
        public string Kind => "stub";
        public bool SupportsConditionalRequests => true;
        public int Calls { get; private set; }
        private readonly IReadOnlyList<IReadOnlyList<(uint, byte)>> _lists;
        public SequenceProvider(params IReadOnlyList<(uint, byte)>[] lists) => _lists = lists;

        public async Task<SourceLoadResult> LoadAsync(PrefixSourceConfig source, string? etag = null, DateTimeOffset? lastModified = null, CancellationToken ct = default)
        {
            await Task.Yield();
            var list = _lists[Math.Min(Calls, _lists.Count - 1)];
            Calls++;
            return SourceLoadResult.Ok(list);
        }
    }

    /// <summary>Returns NotModified when etag is supplied, else Ok(list) — exercises the 304 branch.</summary>
    private sealed class ConditionalProvider : IPrefixSourceProvider
    {
        public string Kind => "stub";
        public bool SupportsConditionalRequests => true;
        public int Calls { get; private set; }
        private readonly IReadOnlyList<(uint, byte)> _list;
        public ConditionalProvider(IReadOnlyList<(uint, byte)> list) => _list = list;

        public Task<SourceLoadResult> LoadAsync(PrefixSourceConfig source, string? etag = null, DateTimeOffset? lastModified = null, CancellationToken ct = default)
        {
            Calls++;
            // Second+ call with an etag → 304 Not Modified (data unchanged).
            return Task.FromResult(etag is null
                ? SourceLoadResult.Ok(_list, etag: "\"v1\"")
                : SourceLoadResult.NotModifiedResult(etag: "\"v1\""));
        }
    }

    /// <summary>#214: RefreshAsync reports true when the source content actually changed.</summary>
    [Fact]
    public async Task RefreshAsync_ReportsChanged_WhenContentDiffers()
    {
        var provider = new SequenceProvider([(1u, (byte)24)], [(1u, (byte)24), (2u, (byte)16)]);
        var svc = new PrefixSourceService(
            ConfigWith("ru"),
            new PrefixSourceProviderFactory([provider]),
            NullLogger<PrefixSourceService>.Instance);

        // Prime the cache via the normal load path (first list).
        await svc.GetAsync("ru");
        // Force-refresh: provider returns the second (different) list → changed=true.
        var changed = await svc.RefreshAsync("ru");

        Assert.True(changed);
        Assert.Equal(2, provider.Calls);
    }

    /// <summary>#214: RefreshAsync reports false when the source content is identical (no change).</summary>
    [Fact]
    public async Task RefreshAsync_ReportsUnchanged_WhenContentIdentical()
    {
        var provider = new SequenceProvider([(1u, (byte)24)], [(1u, (byte)24)]);
        var svc = new PrefixSourceService(
            ConfigWith("ru"),
            new PrefixSourceProviderFactory([provider]),
            NullLogger<PrefixSourceService>.Instance);

        await svc.GetAsync("ru");
        var changed = await svc.RefreshAsync("ru");

        Assert.False(changed);
    }

    /// <summary>#214: a 304 Not Modified response is reported as unchanged (data identical by definition).</summary>
    [Fact]
    public async Task RefreshAsync_ReportsUnchanged_OnNotModified304()
    {
        var provider = new ConditionalProvider([(1u, (byte)24)]);
        var svc = new PrefixSourceService(
            ConfigWith("ru"),
            new PrefixSourceProviderFactory([provider]),
            NullLogger<PrefixSourceService>.Instance);

        await svc.GetAsync("ru");       // first load: Ok(list, etag)
        var changed = await svc.RefreshAsync("ru");  // second load: 304 Not Modified

        Assert.False(changed, "a 304 response means the content is unchanged");
        Assert.Equal(2, provider.Calls);
    }

    /// <summary>
    /// #214 TOCTOU regression: the changed-detection must be computed inside the per-source gate, so a
    /// concurrent GetAsync (TTL-expired connect path) that itself loads the new list cannot mask the
    /// change by making before==after. SequenceProvider returns the new list on the 2nd call regardless
    /// of which caller wins the gate — and RefreshAsync must still report changed=true.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_DetectsChange_DespiteConcurrentGetAsync()
    {
        var provider = new SequenceProvider([(1u, (byte)24)], [(1u, (byte)24), (2u, (byte)16)]);
        var svc = new PrefixSourceService(
            ConfigWith("ru"),
            new PrefixSourceProviderFactory([provider]),
            NullLogger<PrefixSourceService>.Instance,
            cacheTtl: TimeSpan.Zero); // TTL=0 forces GetAsync to always refetch (races RefreshAsync)

        await svc.GetAsync("ru"); // prime with first list

        // Concurrent: GetAsync (TTL=0 → refetches, may grab the gate first) and RefreshAsync.
        var getTask = Task.Run(() => svc.GetAsync("ru"));
        var refreshTask = svc.RefreshAsync("ru");
        await Task.WhenAll(getTask, refreshTask);

        // Regardless of which task loaded the new list first, RefreshAsync must report the change.
        Assert.True(await refreshTask, "RefreshAsync must detect the change even if GetAsync raced ahead");
    }

    /// <summary>#214: SourceSupportsConditional reflects the provider's SupportsConditionalRequests.</summary>
    [Fact]
    public void SourceSupportsConditional_ReflectsProvider()
    {
        var etagProvider = new CountingProvider([(1u, (byte)24)]);
        var noEtagProvider = new NoEtagStubProvider();
        var yaml = "Bgp:\n  Asn: 65444\n  RouterId: 10.0.0.1\n" +
                   "PrefixSources:\n  - Name: http-src\n    Kind: http\n  - Name: asn-src\n    Kind: asn\n";
        var svc = new PrefixSourceService(
            ConfigLoader.LoadFromText(yaml),
            new PrefixSourceProviderFactory([etagProvider, noEtagProvider]),
            NullLogger<PrefixSourceService>.Instance);

        Assert.True(svc.SourceSupportsConditional("http-src"));
        Assert.False(svc.SourceSupportsConditional("asn-src"));
    }

    /// <summary>A stub provider that does NOT support conditional requests (Kind="asn"-like).</summary>
    private sealed class NoEtagStubProvider : IPrefixSourceProvider
    {
        public string Kind => "asn";
        public bool SupportsConditionalRequests => false;
        public Task<SourceLoadResult> LoadAsync(PrefixSourceConfig source, string? etag = null, DateTimeOffset? lastModified = null, CancellationToken ct = default)
            => Task.FromResult(SourceLoadResult.Ok([(1u, (byte)24)]));
    }

    /// <summary>
    /// #214 convergence: when a connect-path load (GetAsync) detects a content change, the
    /// onSourceChanged callback fires so established peers get the update too — not just the
    /// auto-refresh path. Without this, a silent cache update via GetAsync masks the change from a
    /// subsequent RefreshAsync, leaving established peers on stale routes.
    /// </summary>
    [Fact]
    public async Task GetAsync_OnChange_InvokesCallback()
    {
        // v1 (first load) → v2 (changed) → v2 again (no change). Models: source changes once, then
        // stays stable; a subsequent TTL-expired GetAsync must NOT re-fire the callback.
        var provider = new SequenceProvider(
            [(1u, (byte)24)],                       // call 1: v1
            [(1u, (byte)24), (2u, (byte)16)],        // call 2: v2 (changed)
            [(1u, (byte)24), (2u, (byte)16)]);       // call 3: v2 (same — no change)
        var changed = new List<string>();
        var svc = new PrefixSourceService(
            ConfigWith("ru"),
            new PrefixSourceProviderFactory([provider]),
            NullLogger<PrefixSourceService>.Instance,
            cacheTtl: TimeSpan.Zero,
            onSourceChanged: name => { changed.Add(name); return Task.CompletedTask; });

        await svc.GetAsync("ru"); // v1 → cache empty → change → callback
        await svc.GetAsync("ru"); // v2 → differs from v1 → change → callback
        await svc.GetAsync("ru"); // v2 → identical → NO change → no callback

        Assert.Equal(2, changed.Count(n => n == "ru"));
    }
}
