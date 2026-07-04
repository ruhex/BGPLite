using BGPLite.Configuration;

namespace BGPLite.Tests;

public class ConfigValidationTests
{
    // Factory helpers keep each test mutating exactly one field so the assertion isolates the rule
    // under test. Defaults match appsettings.Example.yml: a known-good baseline.
    private static BgpConfig Bgp(
        uint asn = 65001, string routerId = "10.0.0.1", int keepAlive = 60, int holdTime = 180,
        int openTimeoutSeconds = 30, int maxAcceptsPerIpPerMinute = 60)
        => new()
        {
            Asn = asn,
            RouterId = routerId,
            KeepAlive = keepAlive,
            HoldTime = holdTime,
            OpenTimeoutSeconds = openTimeoutSeconds,
            MaxAcceptsPerIpPerMinute = maxAcceptsPerIpPerMinute
        };

    private static AppConfig Config(BgpConfig? bgp = null, int apiPort = 5001, List<PeerConfig>? peers = null)
        => new() { Bgp = bgp ?? Bgp(), ApiPort = apiPort, Peers = peers ?? [] };

    [Fact]
    public void Validate_AcceptsValidConfig()
    {
        var config = Config();

        var act = () => config.Validate();

        act();
    }

    [Fact]
    public void Validate_AcceptsZeroHoldTime_KeepAliveSkipped()
    {
        // RFC 4271 §4.2: HoldTime=0 disables keepalive processing; KeepAlive is then irrelevant.
        var config = Config(Bgp(holdTime: 0, keepAlive: 0));

        var act = () => config.Validate();

        act();
    }

    [Fact]
    public void Validate_RejectsAsnZero()
    {
        var config = Config(Bgp(asn: 0));

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("Bgp.Asn", ex.Message);
    }

    [Theory]
    [InlineData("0.0.0.0")]   // RFC 4271 §6.8 forbids an all-zero BGP Identifier
    [InlineData("not-an-ip")]
    [InlineData("::1")]        // IPv6 must be rejected
    public void Validate_RejectsBadRouterId(string routerId)
    {
        var config = Config(Bgp(routerId: routerId));

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("Bgp.RouterId", ex.Message);
    }

    [Theory]
    [InlineData(2)]   // below the RFC 4271 §4.2 minimum of 3s
    [InlineData(1)]
    public void Validate_RejectsHoldTimeBelowThree(int holdTime)
    {
        var config = Config(Bgp(holdTime: holdTime, keepAlive: 1));

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("Bgp.HoldTime", ex.Message);
    }

    [Fact]
    public void Validate_RejectsKeepAliveAboveHoldTimeThird()
    {
        // HoldTime=3 → max keepalive = max(3/3, 1) = 1; KeepAlive=2 exceeds it.
        var config = Config(Bgp(holdTime: 3, keepAlive: 2));

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("Bgp.KeepAlive", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void Validate_RejectsBadApiPort(int apiPort)
    {
        var config = Config(apiPort: apiPort);

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("ApiPort", ex.Message);
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("::1")]
    public void Validate_RejectsBadPeerAddress(string address)
    {
        var config = Config(peers: [new PeerConfig { Address = address }]);

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("Peers[0].Address", ex.Message);
    }

    [Fact]
    public void Validate_AcceptsValidPeerAddress()
    {
        var config = Config(peers: [new PeerConfig { Address = "10.0.0.2", RemoteAsn = 65002 }]);

        var act = () => config.Validate();

        act();
    }

    [Fact]
    public void Validate_BgpConfigDirectly_AcceptsValid()
    {
        var bgp = Bgp();

        var act = () => bgp.Validate();

        act();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Validate_RejectsNegativeOpenTimeoutSeconds(int openTimeoutSeconds)
    {
        var config = Config(Bgp(openTimeoutSeconds: openTimeoutSeconds));

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("Bgp.OpenTimeoutSeconds", ex.Message);
    }

    [Fact]
    public void Validate_AcceptsZeroOpenTimeoutSeconds_Disabled()
    {
        // 0 = disabled (legacy behavior) — valid.
        var config = Config(Bgp(openTimeoutSeconds: 0));

        var act = () => config.Validate();

        act();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Validate_RejectsNegativeMaxAcceptsPerIpPerMinute(int maxPerMinute)
    {
        var config = Config(Bgp(maxAcceptsPerIpPerMinute: maxPerMinute));

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("Bgp.MaxAcceptsPerIpPerMinute", ex.Message);
    }

    [Fact]
    public void Validate_AcceptsZeroMaxAcceptsPerIpPerMinute_Disabled()
    {
        // 0 = disabled (legacy behavior) — valid.
        var config = Config(Bgp(maxAcceptsPerIpPerMinute: 0));

        var act = () => config.Validate();

        act();
    }
}
