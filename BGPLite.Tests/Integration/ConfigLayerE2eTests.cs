using BGPLite.Configuration;

namespace BGPLite.Tests.Integration;

/// <summary>
/// The application's config entry surface end-to-end: the real ConfigLoader (strict YamlDotNet)
/// parses the shipped YAML shape, Validate() rejects each misconfiguration with its startup-time
/// message, and a config loaded FROM A FILE boots the whole stack (the fixture's YAML boot path —
/// the closest in-process equivalent to `dotnet run appsettings.yml`).
/// </summary>
public sealed class ConfigLayerE2eTests
{
    /// <summary>The same shape the fixture template builds, expressed as operator YAML.</summary>
    public const string TemplateYaml = """
        Bgp:
          Asn: 65000
          RouterId: 198.51.100.1
          HoldTime: 0
          MaxAcceptsPerIpPerMinute: 0
        ApiPort: {0}
        ApiListen: "127.0.0.1"
        RipeStat:
          TimeoutSeconds: 10
          RetryAttempts: 0
          RetryDelaySeconds: 0
          AsnLists:
            - Name: apple
              Description: AS714 Apple Inc.
              Asns: [714]
              Community: "65000:20"
            - Name: ru
              Description: Russia
              Country: ru
              Community: "65000:10"
        PrefixSources:
          - Name: extra
            Kind: file
            Path: {1}
            Community: "65000:30"
            Description: E2E file source
        """;

    [Fact]
    public void Load_ValidTemplate_ParsesAllSections()
    {
        var yaml = TemplateYaml
            .Replace("{0}", "15001")
            .Replace("{1}", "/tmp/e2e-nets.txt");
        var config = ConfigLoader.LoadFromText(yaml);

        Assert.Equal(65000u, config.Bgp.Asn);
        Assert.Equal("198.51.100.1", config.Bgp.RouterId);
        Assert.Equal(0, config.Bgp.HoldTime);
        Assert.Equal(15001, config.ApiPort);
        Assert.Equal("127.0.0.1", config.ApiListen);
        Assert.Equal(2, config.RipeStat!.AsnLists.Count);
        Assert.Equal([714], config.RipeStat.AsnLists[0].Asns);
        Assert.Equal("ru", config.RipeStat.AsnLists[1].Country);
        Assert.Single(config.PrefixSources!);
        Assert.Equal("file", config.PrefixSources[0].Kind);
        Assert.Equal("65000:30", config.PrefixSources[0].Community);
        config.Validate(); // the whole parsed shape must pass the startup gate
    }

    [Theory]
    [InlineData("Bgp:\n  Asn: 65000\n  RouterId: 0.0.0.0\n", "RouterId")]
    [InlineData("Bgp:\n  Asn: 65000\n  RouterId: 198.51.100.1\n  HoldTime: 2\n", "HoldTime")]
    [InlineData("Bgp:\n  Asn: 65000\n  RouterId: 198.51.100.1\n  HoldTime: 9\n  KeepAlive: 5\n", "KeepAlive")]
    [InlineData("Bgp:\n  Asn: 65000\n  RouterId: 198.51.100.1\nApiPort: 70000\n", "ApiPort")]
    [InlineData("Bgp:\n  Asn: 0\n  RouterId: 198.51.100.1\n", "Asn")]
    public void Validate_InvalidConfig_FailsLoudWithNamedField(string yaml, string expectedFragment)
    {
        var config = ConfigLoader.LoadFromText(yaml);
        var failure = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains(expectedFragment, failure.Message);
    }

    [Fact]
    public void Load_RejectsNonEmptyPeersList()
    {
        // Static YAML peers are gone: Validate rejects the section outright (fail-loud migration guard).
        var config = ConfigLoader.LoadFromText("Bgp:\n  Asn: 65000\n  RouterId: 198.51.100.1\nPeers:\n  - Address: 10.0.0.1\n");
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    [Fact]
    public void Load_UnknownKey_ThrowsStrictly()
    {
        // The deserializer is strict: a typo'd key must fail at load, not silently no-op.
        Assert.ThrowsAny<Exception>(() =>
            ConfigLoader.LoadFromText("Bgp:\n  Asn: 65000\n  RouterId: 198.51.100.1\n  NotARealField: 1\n"));
    }

    [Fact]
    public void Load_EmptyDocument_Throws()
    {
        Assert.ThrowsAny<Exception>(() => ConfigLoader.LoadFromText(""));
    }

    /// <summary>The YAML boot path: file → ConfigLoader → Validate → full stack serving HTTP + BGP.</summary>
    [Fact]
    public async Task BootFromYaml_ServesApiAndBgp()
    {
        File.WriteAllText(ExtraPath, string.Empty); // the configured file source must exist
        try
        {
            await using var server = await RouteServerFixture.StartFromYamlAsync(
                port => TemplateYaml.Replace("{0}", port.ToString()).Replace("{1}", ExtraPath),
                seed: p => p.RuPrefixes.AddRange(["5.101.220.0/22"]));

            var info = await server.GetJsonAsync("/api/server");
            Assert.Equal(65000, info.GetProperty("asn").GetInt32());

            using var client = new BgpTestClient(65099, routerId: 0x0A000002);
            await client.ConnectAndEstablishAsync(server.BgpPort);
            var dump = await client.CollectDumpAsync();
            Assert.Contains(dump.SelectMany(u => u.Announced), r => r.Prefix == "5.101.220.0/22");
        }
        finally
        {
            try { File.Delete(ExtraPath); } catch { /* best-effort cleanup */ }
        }
    }

    private static string ExtraPath { get; } = Path.Combine(Path.GetTempPath(), "bgplite-yamlboot-" + Guid.NewGuid().ToString("N") + ".txt");
}
