using BGPLite.Configuration;
using BGPLite.Api;

namespace BGPLite.Tests;

/// <summary>
/// the management API accepted whatever a client sent as a peer address or AS number. The
/// address went to the store verbatim, so any spelling other than the canonical dotted quad produced
/// a peer that <c>BgpServer</c> — which keys sessions by <c>remoteEndpoint.Address.ToString()</c> —
/// could never bind to: configured, visible in the UI, and silently never up.
/// <para>
/// The AS side gained urgency: the BGP path rejects an OPEN carrying AS 0 with Bad
/// Peer AS (RFC 7607), so a peer created with <c>asn: 0</c> was accepted by the API and then
/// guaranteed never to establish. Validating at the API is what closes that asymmetry.
/// </para>
/// </summary>
public class PeerInputValidationTests
{
    // ---- addresses ----

    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("::ffff:192.0.2.1", "192.0.2.1")]
    public void NormalizePeerIp_AcceptsCanonicalAndIpv6Forms(string input, string expected)
    {
        Assert.Equal(expected, ManagementApi.NormalizePeerIp(input));
    }

    /// <summary>
    /// Ambiguous inet_aton forms must be rejected instead of being rewritten into another host.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.2.3.4 ")]
    [InlineData(" 1.2.3.4")]
    [InlineData("banana")]
    [InlineData("1.2.3.4.5")]
    [InlineData("01.02.03.04")]
    [InlineData("010.1.1.1")]
    [InlineData("0x1.2.3.4")]
    [InlineData("0xC0A80000")]
    [InlineData("1.2.3")]
    [InlineData("192.168.000.001")]
    [InlineData("+1.2.3.4")]
    [InlineData("::ffff:010.1.1.1")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("127.0.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:224.0.0.1")]
    public void NormalizePeerIp_RejectsUnusableOrNonCanonicalInput(string? input)
    {
        Assert.Null(ManagementApi.NormalizePeerIp(input));
    }

    // ---- AS numbers ----

    /// <summary>
    /// Exactly four values are unusable as a peer's AS: 0 (RFC 7607, and the OPEN path rejects it),
    /// AS_TRANS (RFC 6793 — a placeholder, never a real peer), and the two Last ASNs
    /// reserved by RFC 7300.
    /// </summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(23456u)]
    [InlineData(65535u)]
    [InlineData(4294967295u)]
    public void IsConfigurablePeerAsn_RejectsUnusableValues(uint asn)
    {
        Assert.False(ManagementApi.IsConfigurablePeerAsn(asn));
    }

    /// <summary>
    /// The private ranges must stay usable — they are what a user of a route server peers with.
    /// Excluding "4200000000+" would have excluded the RFC 6996 private 32-bit range; RFC 7300
    /// reserves only the two endpoints, so that exclusion would have locked out real peers.
    /// </summary>
    [Theory]
    [InlineData(1u)]
    [InlineData(65001u)]
    [InlineData(64512u)]      // RFC 6996 private 16-bit, first
    [InlineData(65534u)]      // RFC 6996 private 16-bit, last
    [InlineData(4200000000u)] // RFC 6996 private 32-bit, first
    [InlineData(4294967294u)] // RFC 6996 private 32-bit, last
    [InlineData(200000u)]
    public void IsConfigurablePeerAsn_AcceptsEverythingElseIncludingPrivateRanges(uint asn)
    {
        Assert.True(ManagementApi.IsConfigurablePeerAsn(asn));
    }

    /// <summary>The boundaries either side of each reserved value are usable — an off-by-one here
    /// would silently lock out a legitimate AS.</summary>
    [Theory]
    [InlineData(23455u)]
    [InlineData(23457u)]
    [InlineData(65534u)]
    [InlineData(65536u)]
    [InlineData(4294967294u)]
    public void IsConfigurablePeerAsn_ReservedValuesDoNotBleedIntoNeighbours(uint asn)
    {
        Assert.True(ManagementApi.IsConfigurablePeerAsn(asn));
    }

    /// <summary>
    /// Subscription names must resolve against the configured lists — an unknown
    /// name was stored and silently served zero prefixes forever. Both config surfaces count as
    /// known: RipeStat.AsnLists and PrefixSources.
    /// </summary>
    [Fact]
    public void FindUnknownSubscriptionNames_FlagsTypos_KnowsBothSurfaces()
    {
        var config = new AppConfig
        {
            Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" },
            RipeStat = new RipeStatConfig { AsnLists = [new AsnList { Name = "ru" }] },
            PrefixSources = [new PrefixSourceConfig { Name = "cloud", Kind = "http", Url = "https://example.com/x" }],
        };

        Assert.Empty(ManagementApi.FindUnknownSubscriptionNames(["ru", "cloud"], config));
        Assert.Empty(ManagementApi.FindUnknownSubscriptionNames([], config));

        var unknown = ManagementApi.FindUnknownSubscriptionNames(["ru", "ru-backup", "Cloud"], config);
        Assert.Equal(["ru-backup", "Cloud"], unknown);   // case-sensitive + typo both flagged
    }
}
