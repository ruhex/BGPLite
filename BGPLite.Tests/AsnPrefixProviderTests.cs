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
}
