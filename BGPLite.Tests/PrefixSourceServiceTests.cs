using BGPLite.Configuration;
using BGPLite.Providers;
using BGPLite.Server;
using Microsoft.Extensions.Logging.Abstractions;
using BGPLite.Protocol;

namespace BGPLite.Tests;

public class PrefixSourceServiceTests
{
    private sealed class CountingProvider : IPrefixSourceProvider
    {
        public string Kind => "stub";
        public bool SupportsConditionalRequests => true;
        public int Calls { get; private set; }
        private readonly IReadOnlyList<IpPrefix> _list;
        public CountingProvider(IReadOnlyList<IpPrefix> list) => _list = list;

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
    public async Task PrefixSources_YamlNull_ConsumersTolerateTheDocumentedEmpty()
    {
        // #477: "PrefixSources:" (YAML null) deserializes as a null list — Validate and
        // ConfigValidationTests bless it as "no sources", so the startup consumers must treat it
        // as empty instead of crashing with NRE/ANE (ConfigCommunityResolver previously threw
        // ArgumentNullException at DI resolution → startup failure on a documented-valid config).
        var config = ConfigWith();

        var resolver = new ConfigCommunityResolver(config, config.Bgp, logger: null);
        var service = new PrefixSourceService(
            config, new PrefixSourceProviderFactory([]), NullLogger<PrefixSourceService>.Instance);

        Assert.Empty(await service.LoadAllAsync());
    }

    [Fact]
    public async Task GetAsync_CachesWithinTtl()
    {
        var provider = new CountingProvider([new(1u, 24)]);
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
            new PrefixSourceProviderFactory([new CountingProvider([new(1u, 24)])]),
            NullLogger<PrefixSourceService>.Instance);

        Assert.Empty(await svc.GetAsync("nope"));
    }

    [Fact]
    public async Task LoadDefaultAsync_ResolvesByName_AndReportsFirstLoadAsChanged()
    {
        var provider = new CountingProvider([new IpPrefix(1u, 24), new IpPrefix(2u, 16)]);
        var yaml = "Bgp:\n  Asn: 65444\n  RouterId: 10.0.0.1\n" +
                   "PrefixSources:\n  - Name: ru\n    Kind: stub\nDefaultPrefixSource: ru\n";
        var svc = new PrefixSourceService(
            ConfigLoader.LoadFromText(yaml),
            new PrefixSourceProviderFactory([provider]),
            NullLogger<PrefixSourceService>.Instance);

        var (prefixes, changed) = await svc.LoadDefaultAsync();
        Assert.Equal(2, prefixes.Count);
        Assert.True(changed); // first-ever load counts as a content change (#214)
    }

    [Fact]
    public async Task LoadDefaultAsync_UnsetReturnsEmptyNoChange()
    {
        var svc = new PrefixSourceService(
            ConfigWith("ru"),
            new PrefixSourceProviderFactory([new CountingProvider([new(1u, 24)])]),
            NullLogger<PrefixSourceService>.Instance);

        var (prefixes, changed) = await svc.LoadDefaultAsync();
        Assert.Empty(prefixes);
        Assert.False(changed);
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
        var provider = new CountingProvider([new(1u, 24)]);
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
        var provider = new CountingProvider([new(1u, 24)]);
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
        private readonly IReadOnlyList<IpPrefix> _first;
        public ToggleProvider(IReadOnlyList<IpPrefix> first) => _first = first;

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
        var provider = new ToggleProvider([new(1u, 24), new(2u, 16)]);
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
                new PrefixSourceProviderFactory([new CountingProvider([new(1u, 24)])]),
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
        private readonly IReadOnlyList<IReadOnlyList<IpPrefix>> _lists;
        public SequenceProvider(params IReadOnlyList<IpPrefix>[] lists) => _lists = lists;

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
        private readonly IReadOnlyList<IpPrefix> _list;
        public ConditionalProvider(IReadOnlyList<IpPrefix> list) => _list = list;

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
        var provider = new SequenceProvider([new(1u, 24)], [new(1u, 24), new(2u, 16)]);
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
        var provider = new SequenceProvider([new(1u, 24)], [new(1u, 24)]);
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
        var provider = new ConditionalProvider([new(1u, 24)]);
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
    /// #332 (replaces the scheduling-dependent RefreshAsync_DetectsChange_DespiteConcurrentGetAsync):
    /// the GetAsync-wins interleaving of the #214 TOCTOU scenario, forced deterministically instead
    /// of racing a cold thread pool. The correct outcome for THIS ordering is <c>changed=false</c>:
    /// GetAsync's load already detected the change INSIDE the per-source gate, updated the cache,
    /// and fired <c>onSourceChanged</c> — the change is consumed, and RefreshAsync's own in-gate
    /// compare then legitimately sees identical lists. What must NOT happen is the change being
    /// silently lost: exactly one push for it (via GetAsync) is the observable contract.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_AfterConcurrentGetAsyncConsumedChange_ReportsFalse_PushFiresOnce()
    {
        var provider = new SequenceProvider([new(1u, 24)], [new(1u, 24), new(2u, 16)]);
        var pushed = new List<string>();
        var gate = new object();
        var svc = new PrefixSourceService(
            ConfigWith("ru"),
            new PrefixSourceProviderFactory([provider]),
            NullLogger<PrefixSourceService>.Instance,
            cacheTtl: TimeSpan.Zero, // TTL=0: GetAsync always refetches (the connect-path racer)
            onSourceChanged: name => { lock (gate) pushed.Add(name); return Task.CompletedTask; });

        await svc.GetAsync("ru"); // prime: call 1 loads list1; first-ever load counts as changed (#251 push)

        // The "GetAsync wins the gate" leg, sequential and deterministic: call 2 loads list2,
        // detects the change inside the gate, updates the cache, and pushes.
        await svc.GetAsync("ru");

        // RefreshAsync now runs third: call 3 returns list2 again (SequenceProvider clamps to the
        // last list), the in-gate compare sees cache==fresh, and the already-consumed change is
        // correctly reported as unchanged.
        var changed = await svc.RefreshAsync("ru");

        Assert.False(changed, "the change was consumed by the winning GetAsync; nothing new for RefreshAsync");
        Assert.Equal(3, provider.Calls);
        lock (gate)
        {
            Assert.Equal(["ru", "ru"], pushed); // prime push + exactly ONE push for the real change
        }
    }

    /// <summary>#214: SourceSupportsConditional reflects the provider's SupportsConditionalRequests.</summary>
    [Fact]
    public void SourceSupportsConditional_ReflectsProvider()
    {
        var etagProvider = new CountingProvider([new(1u, 24)]);
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
            => Task.FromResult(SourceLoadResult.Ok([new IpPrefix(1u, 24)]));
    }

    /// <summary>
    /// #214 order-independence: RIPEstat and some HTTP sources return the same prefix set in a different
    /// order between requests. SequenceEqual there would report a phantom change and trigger an
    /// unnecessary BGP re-announcement. SamePrefixes must treat them as unchanged.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_OrderIndependentComparison_NoPhantomChange()
    {
        // v1 and v2 contain the SAME prefixes in a DIFFERENT order → must report unchanged.
        var provider = new SequenceProvider(
            [new IpPrefix(1u, 24), new IpPrefix(2u, 16)],   // call 1: [a, b]
            [new IpPrefix(2u, 16), new IpPrefix(1u, 24)]);  // call 2: [b, a] — same set, reversed
        var svc = new PrefixSourceService(
            ConfigWith("ru"),
            new PrefixSourceProviderFactory([provider]),
            NullLogger<PrefixSourceService>.Instance);

        await svc.GetAsync("ru");        // prime with v1
        var changed = await svc.RefreshAsync("ru");  // v2 = same set, different order

        Assert.False(changed, "a re-ordered prefix set must NOT be reported as a change");
    }
    [Fact]
    public async Task GetAsync_OnChange_InvokesCallback()
    {
        // v1 (first load) → v2 (changed) → v2 again (no change). Models: source changes once, then
        // stays stable; a subsequent TTL-expired GetAsync must NOT re-fire the callback.
        var provider = new SequenceProvider(
            [new IpPrefix(1u, 24)],                       // call 1: v1
            [new IpPrefix(1u, 24), new IpPrefix(2u, 16)],        // call 2: v2 (changed)
            [new IpPrefix(1u, 24), new IpPrefix(2u, 16)]);       // call 3: v2 (same — no change)
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
