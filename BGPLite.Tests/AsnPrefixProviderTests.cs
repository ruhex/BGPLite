using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http;
using BGPLite.Configuration;
using BGPLite.Providers;
using Xunit;

namespace BGPLite.Tests;

public class AsnPrefixProviderTests
{
    [Fact]
    public void Kind_IsAsn() => Assert.Equal("asn", new AsnPrefixProvider(null!, null!).Kind);

    [Fact]
    public async Task LoadAsync_MissingAsn_Throws()
    {
        // Asn is checked before RipeStatProvider is touched, so null deps are safe here.
        var provider = new AsnPrefixProvider(null!, null!);
        var source = new PrefixSourceConfig { Name = "cloudflare", Kind = "asn" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.LoadAsync(source));
    }

    /// <summary>
    /// #267 item 5: Kind:asn sources and the RipeStat.AsnLists/custom-ASN path share ONE per-ASN
    /// cache — an ASN configured in both mechanisms is fetched from RIPEstat once. The old
    /// direct-to-wire provider path fetched it twice with independent TTLs.
    /// </summary>
    private sealed class CountingRipeHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"status":"ok","data":{"resource":"65010","prefixes":{"v4":{"originating":["10.1.10.1/32"]},"v6":{"originating":[]}}}}""")
            });
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    [Fact]
    public async Task SameAsn_BothMechanisms_SingleWireFetch()
    {
        var handler = new CountingRipeHandler();
        var cache = new RipeStatPrefixCache(new RipeStatProvider(
            new StubFactory(handler), NullLogger<RipeStatProvider>.Instance,
            new RipeStatConfig { RetryAttempts = 0, RetryDelaySeconds = 0 }));
        var service = new PrefixService(new AppConfig(), cache, null!, null!);
        var provider = new AsnPrefixProvider(cache, NullLogger<AsnPrefixProvider>.Instance);

        // Mechanism A: RipeStat.AsnLists / custom-ASN path.
        var viaService = await service.GetPrefixesAsync(65010);
        // Mechanism B: a PrefixSources entry of Kind: asn for the SAME ASN.
        var viaSource = await provider.LoadAsync(new PrefixSourceConfig { Name = "x", Kind = "asn", Asn = 65010 });

        Assert.Single(viaService);
        Assert.Single(viaSource.Prefixes);
        Assert.Equal(1, handler.Calls);   // RED on the old direct-to-wire provider: 2
    }
}
