using BGPLite.Configuration;

namespace BGPLite.Tests;

public class ConfigurationTests
{
    [Fact]
    public void LoadFromText_ParsesValidYaml()
    {
        var yaml = """
            Bgp:
              Asn: 65444
              RouterId: 10.0.0.1
              KeepAlive: 60
              HoldTime: 180

            Peers:
              - Address: 10.0.0.2
                RemoteAsn: 65001
                Description: "upstream"
            """;

        var config = ConfigLoader.LoadFromText(yaml);

        Assert.Equal(65444u, config.Bgp.Asn);
        Assert.Equal("10.0.0.1", config.Bgp.RouterId);
        Assert.Equal(60, config.Bgp.KeepAlive);
        Assert.Equal(180, config.Bgp.HoldTime);

        Assert.Single(config.Peers);
        Assert.Equal("10.0.0.2", config.Peers[0].Address);
        Assert.Equal(65001u, config.Peers[0].RemoteAsn);
        Assert.Equal("upstream", config.Peers[0].Description);
    }

    [Fact]
    public void LoadFromText_ParsesTrustedProxies()
    {
        var yaml = """
            Bgp:
              Asn: 65444
              RouterId: 10.0.0.1

            TrustedProxies:
              - "127.0.0.0/8"
              - "10.0.0.0/8"
            """;

        var config = ConfigLoader.LoadFromText(yaml);

        Assert.Equal(new[] { "127.0.0.0/8", "10.0.0.0/8" }, config.TrustedProxies);
    }

    [Fact]
    public void LoadFromText_ParsesApiRateLimit()
    {
        var yaml = """
            Bgp:
              Asn: 65444
              RouterId: 10.0.0.1
            ApiRateLimit:
              Enabled: true
              TokenLimit: 60
              TokensPerPeriod: 60
              PeriodSeconds: 60
            """;

        var config = ConfigLoader.LoadFromText(yaml);

        Assert.NotNull(config.ApiRateLimit);
        Assert.True(config.ApiRateLimit!.Enabled);
        Assert.Equal(60, config.ApiRateLimit.TokenLimit);
        Assert.Equal(60, config.ApiRateLimit.TokensPerPeriod);
        Assert.Equal(60, config.ApiRateLimit.PeriodSeconds);
    }

    [Fact]
    public void LoadFromText_MultiplePeers()
    {
        var yaml = """
            Bgp:
              Asn: 65444
              RouterId: 10.0.0.1

            Peers:
              - Address: 10.0.0.2
                RemoteAsn: 65001
              - Address: 10.0.0.3
                RemoteAsn: 65002
            """;

        var config = ConfigLoader.LoadFromText(yaml);
        Assert.Equal(2, config.Peers.Count);
    }

    [Fact]
    public void LoadFromText_DefaultValues()
    {
        var yaml = """
            Bgp:
              Asn: 65444
              RouterId: 10.0.0.1
            """;

        var config = ConfigLoader.LoadFromText(yaml);

        Assert.Equal(60, config.Bgp.KeepAlive);
        Assert.Equal(180, config.Bgp.HoldTime);
        Assert.Empty(config.Peers);
    }

    [Fact]
    public void LoadFromText_ParsesPrefixSources_File()
    {
        var yaml = """
            Bgp:
              Asn: 65444
              RouterId: 10.0.0.1
            PrefixSources:
              - Kind: file
                Name: ru
                Path: nets.txt
                Community: "65000:100"
            DefaultPrefixSource: ru
            """;

        var config = ConfigLoader.LoadFromText(yaml);

        var src = Assert.Single(config.PrefixSources);
        Assert.Equal("file", src.Kind);
        Assert.Equal("ru", src.Name);
        Assert.Equal("nets.txt", src.Path);
        Assert.Equal("65000:100", src.Community);
        Assert.Equal("ru", config.DefaultPrefixSource);
    }

    [Fact]
    public void LoadFromText_ParsesPrefixSources_Http()
    {
        var yaml = """
            Bgp:
              Asn: 65444
              RouterId: 10.0.0.1
            PrefixSources:
              - Kind: http
                Name: cf
                Url: "https://raw.githubusercontent.com/o/r/main/cf.txt"
            """;

        var config = ConfigLoader.LoadFromText(yaml);

        var src = Assert.Single(config.PrefixSources);
        Assert.Equal("http", src.Kind);
        Assert.Equal("cf", src.Name);
        Assert.Equal("https://raw.githubusercontent.com/o/r/main/cf.txt", src.Url);
    }

    [Fact]
    public void LoadFromText_ParsesPrefixSources_HttpOptions()
    {
        var yaml = """
            Bgp:
              Asn: 65444
              RouterId: 10.0.0.1
            PrefixSources:
              - Kind: http
                Name: data
                Url: "https://data.org/list.txt"
                Timeout: 60
                Headers:
                  Authorization: "Bearer token"
                  X-API-Key: "key123"
            """;

        var config = ConfigLoader.LoadFromText(yaml);

        var src = Assert.Single(config.PrefixSources);
        Assert.Equal(60, src.Timeout);
        Assert.NotNull(src.Headers);
        Assert.Equal("Bearer token", src.Headers!["Authorization"]);
        Assert.Equal("key123", src.Headers!["X-API-Key"]);
    }

    [Fact]
    public void LoadFromText_PrefixSources_DefaultKindIsFile()
    {
        var yaml = """
            Bgp:
              Asn: 65444
              RouterId: 10.0.0.1
            PrefixSources:
              - Name: x
                Path: x.txt
            """;

        var config = ConfigLoader.LoadFromText(yaml);

        Assert.Equal("file", config.PrefixSources[0].Kind);
    }

    [Fact]
    public void LoadFromText_NoPrefixSourcesByDefault()
    {
        var yaml = """
            Bgp:
              Asn: 65444
              RouterId: 10.0.0.1
            """;

        var config = ConfigLoader.LoadFromText(yaml);

        Assert.Empty(config.PrefixSources);
        Assert.Null(config.DefaultPrefixSource);
    }

    [Fact]
    public void LoadFromText_RipeStatDefaults_WhenSectionAbsent()
    {
        var yaml = """
            Bgp:
              Asn: 65444
              RouterId: 10.0.0.1
            """;

        var config = ConfigLoader.LoadFromText(yaml);

        Assert.Null(config.RipeStat);
        // The provider falls back to these defaults when the section is absent.
        Assert.Equal(180, RipeStatConfig.DefaultTimeoutSeconds);
        Assert.Equal(180, new RipeStatConfig().TimeoutSeconds);
        Assert.Equal(2, new RipeStatConfig().RetryAttempts);
    }

    [Fact]
    public void LoadFromText_ParsesRipeStatOptions()
    {
        var yaml = """
            Bgp:
              Asn: 65444
              RouterId: 10.0.0.1
            RipeStat:
              TimeoutSeconds: 300
              RetryAttempts: 4
              RetryDelaySeconds: 5
              AsnLists:
                - Name: tier1
                  Description: "Tier-1 transit"
                  Asns: [3356, 1299]
            """;

        var config = ConfigLoader.LoadFromText(yaml);

        Assert.NotNull(config.RipeStat);
        Assert.Equal(300, config.RipeStat!.TimeoutSeconds);
        Assert.Equal(4, config.RipeStat.RetryAttempts);
        Assert.Equal(5, config.RipeStat.RetryDelaySeconds);
        var list = Assert.Single(config.RipeStat.AsnLists);
        Assert.Equal("tier1", list.Name);
        Assert.Equal(new uint[] { 3356, 1299 }, list.Asns);
    }
}
