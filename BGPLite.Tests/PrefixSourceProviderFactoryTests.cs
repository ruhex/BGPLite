using BGPLite.Configuration;
using BGPLite.Providers;

namespace BGPLite.Tests;

public class PrefixSourceProviderFactoryTests
{
    private sealed class StubProvider : IPrefixSourceProvider
    {
        public string Kind { get; }
        public StubProvider(string kind) => Kind = kind;
        public Task<SourceLoadResult> LoadAsync(PrefixSourceConfig source, string? etag = null, DateTimeOffset? lastModified = null, CancellationToken ct = default)
            => Task.FromResult(SourceLoadResult.Ok([]));
    }

    [Fact]
    public void DispatchesByKind()
    {
        var factory = new PrefixSourceProviderFactory(new IPrefixSourceProvider[]
        {
            new StubProvider("file"),
            new StubProvider("http")
        });

        Assert.Equal("file", factory.Get("file").Kind);
        Assert.Equal("http", factory.Get("http").Kind);
    }

    [Fact]
    public void UnknownKindThrows()
    {
        var factory = new PrefixSourceProviderFactory(Array.Empty<IPrefixSourceProvider>());
        Assert.Throws<InvalidOperationException>(() => factory.Get("nope"));
    }
}
