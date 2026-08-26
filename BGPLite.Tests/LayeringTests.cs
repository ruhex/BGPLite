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
    public void PrefixService_implements_IPrefixService_from_contracts()
    {
        // #230: the contract moved from Configuration (its #88 workaround home) to the dedicated
        // BGPLite.Contracts layer.
        var contract = typeof(PrefixService)
            .GetInterfaces()
            .SingleOrDefault(i => i.Name == "IPrefixService");

        Assert.NotNull(contract);
        Assert.Equal("BGPLite.Contracts", contract!.Namespace);
    }

    [Fact]
    public void Dependency_graph_has_no_forbidden_edges()
    {
        // #230: the layering rules as an executable matrix — Protocol and Contracts are leaves,
        // Routing sits on them, Server on Routing, Api/Providers on Contracts — never upward.
        // Any PR that re-adds one of these edges turns this test red.
        var assemblies = new Dictionary<string, System.Reflection.Assembly>
        {
            ["BGPLite.Protocol"] = typeof(BgpConstants).Assembly,
            ["BGPLite.Contracts"] = typeof(BGPLite.Contracts.IPeerStore).Assembly,
            ["BGPLite.Configuration"] = typeof(BGPLite.Configuration.ConfigLoader).Assembly,
            ["BGPLite.Routing"] = typeof(BGPLite.Routing.Route).Assembly,
            ["BGPLite.Server"] = typeof(BGPLite.Server.BgpServer).Assembly,
            ["BGPLite.Providers"] = typeof(PrefixService).Assembly,
            ["BGPLite.Api"] = typeof(BGPLite.Api.ManagementApi).Assembly,
        };

        var forbidden = new[]
        {
            ("BGPLite.Api", "BGPLite.Server"),       // #230 — the edge the Contracts extraction removed
            ("BGPLite.Routing", "BGPLite.Api"),
            ("BGPLite.Routing", "BGPLite.Server"),
            ("BGPLite.Routing", "BGPLite.Providers"),
            ("BGPLite.Server", "BGPLite.Api"),
            ("BGPLite.Server", "BGPLite.Providers"),
            ("BGPLite.Protocol", "BGPLite.Api"),
            ("BGPLite.Protocol", "BGPLite.Server"),
            ("BGPLite.Protocol", "BGPLite.Routing"),
            ("BGPLite.Protocol", "BGPLite.Providers"),
            ("BGPLite.Protocol", "BGPLite.Contracts"),
            ("BGPLite.Protocol", "BGPLite.Configuration"),
            ("BGPLite.Contracts", "BGPLite.Api"),
            ("BGPLite.Contracts", "BGPLite.Server"),
            ("BGPLite.Contracts", "BGPLite.Routing"),
            ("BGPLite.Contracts", "BGPLite.Providers"),
            ("BGPLite.Contracts", "BGPLite.Protocol"),
            ("BGPLite.Contracts", "BGPLite.Configuration"),
            ("BGPLite.Configuration", "BGPLite.Api"),
            ("BGPLite.Configuration", "BGPLite.Server"),
            ("BGPLite.Configuration", "BGPLite.Routing"),
            ("BGPLite.Configuration", "BGPLite.Providers"),
            ("BGPLite.Configuration", "BGPLite.Contracts"),
        };

        foreach (var (from, to) in forbidden)
        {
            var referenced = assemblies[from].GetReferencedAssemblies().Select(a => a.Name);
            Assert.DoesNotContain(to, referenced);
        }
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
