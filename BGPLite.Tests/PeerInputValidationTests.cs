using BGPLite.Configuration;
using BGPLite.Api;

namespace BGPLite.Tests;

/// <summary>
/// #255: the management API accepted whatever a client sent as a peer address or AS number. The
/// address went to the store verbatim, so any spelling other than the canonical dotted quad produced
/// a peer that <c>BgpServer</c> — which keys sessions by <c>remoteEndpoint.Address.ToString()</c> —
/// could never bind to: configured, visible in the UI, and silently never up.
/// <para>
/// The AS side gained urgency from #300: the BGP path now rejects an OPEN carrying AS 0 with Bad
/// Peer AS (RFC 7607), so a peer created with <c>asn: 0</c> was accepted by the API and then
/// guaranteed never to establish. Validating at the API is what closes that asymmetry.
/// </para>
/// </summary>
public class PeerInputValidationTests
{
    // ---- addresses ----

    /// <summary>
    /// Forms IPAddress.TryParse accepts but rewrites. Storing the input rather than the parse result
    /// is the phantom-peer bug; the three-part form is the sharpest case, since 1.2.3 resolves to a
    /// different host than a reader expects.
    /// </summary>
    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("01.02.03.04", "1.2.3.4")]
    [InlineData("0x1.2.3.4", "1.2.3.4")]
    [InlineData("1.2.3", "1.2.0.3")]
    [InlineData("192.168.000.001", "192.168.0.1")]
    public void NormalizePeerIp_CanonicalizesAcceptedForms(string input, string expected)
    {
        Assert.Equal(expected, ManagementApi.NormalizePeerIp(input));
    }

    /// <summary>
    /// Rejected outright: unparseable input, absent input (which reached the store as null and
    /// surfaced as a 500 from a NOT NULL violation), and IPv6 — BGPLite is IPv4-unicast only, so an
    /// IPv6 peer row could never match a session either.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.2.3.4 ")]
    [InlineData(" 1.2.3.4")]
    [InlineData("banana")]
    [InlineData("1.2.3.4.5")]
    public void NormalizePeerIp_RejectsUnusableInput(string? input)
    {
        Assert.Null(ManagementApi.NormalizePeerIp(input));
    }

    /// <summary>
    /// The property that actually matters: whatever spelling a client sends, what gets stored is the
    /// form BgpServer will look the session up by.
    /// </summary>
    [Theory]
    [InlineData("01.02.03.04")]
    [InlineData("0x1.2.3.4")]
    [InlineData("192.168.000.001")]
    public void NormalizePeerIp_OutputMatchesWhatTheBgpPathWouldStore(string input)
    {
        var normalized = ManagementApi.NormalizePeerIp(input);
        Assert.NotNull(normalized);
        // BgpServer stores remoteEndpoint.Address.ToString(); the round trip must be a fixed point.
        Assert.Equal(normalized, System.Net.IPAddress.Parse(normalized!).ToString());
    }

    // ---- AS numbers ----

    /// <summary>
    /// Exactly four values are unusable as a peer's AS: 0 (RFC 7607, and the OPEN path rejects it
    /// since #300), AS_TRANS (RFC 6793 — a placeholder, never a real peer), and the two Last ASNs
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
    /// #255 proposed excluding "4200000000+", which is the RFC 6996 private 32-bit range; RFC 7300
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
    /// #266 item 4: subscription names must resolve against the configured lists — an unknown
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
