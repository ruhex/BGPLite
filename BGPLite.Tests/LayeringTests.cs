using System.Reflection;
using BGPLite.Providers;

namespace BGPLite.Tests;

// Regression guard for #88: BGPLite.Providers is a lower/data layer and must NOT depend upward on
// BGPLite.Server. The IPrefixService contract was moved from BGPLite.Server to BGPLite.Configuration
// so the concrete PrefixService (in Providers) implements it without referencing Server. These tests
// fail if the Server ProjectReference is re-added to Providers or if the contract moves back to Server.
public class LayeringTests
{
    [Fact]
    public void Providers_assembly_does_not_reference_server()
    {
        var providers = typeof(PrefixService).Assembly;
        var referenced = providers.GetReferencedAssemblies()
            .Select(a => a.Name);
        Assert.DoesNotContain("BGPLite.Server", referenced);
    }

    [Fact]
    public void PrefixService_implements_IPrefixService_from_configuration()
    {
        var contract = typeof(PrefixService)
            .GetInterfaces()
            .SingleOrDefault(i => i.Name == "IPrefixService");

        Assert.NotNull(contract);
        Assert.Equal("BGPLite.Configuration", contract!.Namespace);
    }
}
