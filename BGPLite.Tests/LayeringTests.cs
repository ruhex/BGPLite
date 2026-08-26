using System.Reflection;
using BGPLite.Protocol;
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

    [Fact]
    public void Protocol_assembly_is_a_pure_leaf()
    {
        // #271: BGPLite.Protocol is being extracted into a standalone library. It must stay
        // dependency-free: no BGPLite.* project references and no third-party packages — the
        // compiler emits package references into the assembly reference list, so this catches
        // both. Only BCL (System.* / netstandard / the shared framework) references are allowed.
        var protocol = typeof(BgpConstants).Assembly;
        var referenced = protocol.GetReferencedAssemblies();

        Assert.DoesNotContain(referenced, a => a.Name!.StartsWith("BGPLite", StringComparison.Ordinal));
        Assert.All(referenced, a => Assert.True(
            a.Name!.StartsWith("System", StringComparison.Ordinal)
                || a.Name == "netstandard"
                || a.Name == "Microsoft.NETCore.App",
            $"BGPLite.Protocol must not reference '{a.Name}' — it is being extracted as a standalone library (#271)"));
    }
}
