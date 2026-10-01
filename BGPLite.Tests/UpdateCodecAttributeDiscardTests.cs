using BGPLite.Protocol;
using Xunit;

namespace BGPLite.Tests;

/// <summary>
/// RFC 7606 / RFC 6793 attribute-discard coverage for the two paths that were still taking
/// treat-as-withdraw, plus the RFC 6793 §4.2.3 rule that gates AS4_PATH reconstruction on the
/// AGGREGATOR attribute.
/// <para>
/// Every case here is a <c>ParseRouteAttributes</c> call, i.e. the whole inbound attribute
/// pipeline. Before the fix the first three threw <c>BgpNotificationException(3, 11)</c> or
/// <c>(3, 9)</c>, which the session routes to treat-as-withdraw and drops every NLRI in the
/// UPDATE — routes a conformant peer is entitled to have installed.
/// </para>
/// </summary>
public sealed class UpdateCodecAttributeDiscardTests
{
    private const uint AsTrans = BgpConstants.AsPath.AsTrans;   // 23456

    private static BgpUpdateMessage Update(params PathAttribute[] extra) => new()
    {
        PathAttributes =
        [
            AttributeHelper.WriteOrigin(BgpOrigin.Igp),
            AttributeHelper.WriteAsPath([65010u, 65020u, 65030u], fourByteAsn: false),
            AttributeHelper.WriteNextHop(0x0A000001),
            .. extra
        ],
        Nlri = [new IpPrefix(0x0A000000, 24)]
    };

    private static PathAttribute Raw(byte typeCode, params byte[] data)
        => new() { Flags = 0xC0, TypeCode = typeCode, Data = data };

    /// <summary>Well-formed 6-byte AGGREGATOR carrying <paramref name="asn"/> (2-octet form).</summary>
    private static PathAttribute Aggregator6(uint asn)
        => Raw(BgpConstants.Attribute.Aggregator,
            (byte)(asn >> 8), (byte)asn, 0x0A, 0x00, 0x00, 0x01);

    /// <summary>Well-formed 8-byte AS4_AGGREGATOR.</summary>
    private static PathAttribute As4Aggregator8(uint asn)
        => Raw(BgpConstants.Attribute.As4Aggregator,
            (byte)(asn >> 24), (byte)(asn >> 16), (byte)(asn >> 8), (byte)asn, 0x0A, 0x00, 0x00, 0x01);

    // ---------------------------------------------------------------- AS4_PATH

    public static TheoryData<byte[]> MalformedAs4PathPayloads() => new()
    {
        // zero-length path segment
        new byte[] { 0x02, 0x00 },
        // segment length longer than the attribute (truncated)
        new byte[] { 0x02, 0x04, 0x00, 0x03, 0x0D, 0x40 },
        // stray trailing octet after a complete segment
        new byte[] { 0x02, 0x04, 0x00, 0x03, 0x0D, 0x40, 0xFF },
        // AS 0 (RFC 7607)
        new byte[] { 0x02, 0x04, 0x00, 0x00, 0x00, 0x00 },
        // AS_CONFED_SEQUENCE segment (RFC 5065 type 3) — RFC 6793 §6 says discard these segments
        new byte[] { 0x02, 0x04, 0x03, 0x00, 0x03, 0x0D, 0x40 },
    };

    [Theory]
    [MemberData(nameof(MalformedAs4PathPayloads))]
    public void MalformedAs4Path_IsDiscarded_RoutesKept(byte[] payload)
    {
        // RFC 6793 §6: "the 'attribute discard' approach is chosen to handle a malformed AS4_PATH
        // attribute … MUST discard the attribute and continue processing the UPDATE message".
        // Pre-fix: BgpParseException(Malformed AS_PATH) → 3/11 → treat-as-withdraw → all NLRI gone.
        var update = Update(Raw(BgpConstants.Attribute.As4Path, payload));

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: false);

        Assert.Contains(BgpConstants.Attribute.As4Path, attrs.DiscardedAttributes);
        // The RFC prescribes falling back to AS_PATH when AS4_PATH cannot be used.
        Assert.Equal([65010u, 65020u, 65030u], attrs.AsPath);
    }

    [Fact]
    public void MalformedAs4Path_StillInstallsEveryNlri()
    {
        // The session-level consequence: treat-as-withdraw would have removed all of these.
        var update = new BgpUpdateMessage
        {
            PathAttributes =
            [
                AttributeHelper.WriteOrigin(BgpOrigin.Igp),
                AttributeHelper.WriteAsPath([65010u, 65020u, 65030u], fourByteAsn: false),
                AttributeHelper.WriteNextHop(0x0A000001),
                Raw(BgpConstants.Attribute.As4Path, 0x02, 0x00)
            ],
            Nlri =
            [
                new IpPrefix(0x0A000000, 24),
                new IpPrefix(0x0B000000, 24),
                new IpPrefix(0x0C000000, 24)
            ]
        };

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: false);

        Assert.Equal(3, update.Nlri.Count);
        Assert.Contains(BgpConstants.Attribute.As4Path, attrs.DiscardedAttributes);
    }

    [Fact]
    public void ValidAs4Path_IsStillMerged()
    {
        // Control: a well-formed AS4_PATH must keep being applied — the discard arm must not
        // swallow the normal RFC 6793 reconstruction.
        var update = Update(AttributeHelper.WriteAs4Path([200000u]));

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: false);

        Assert.Empty(attrs.DiscardedAttributes);
        Assert.Equal([65010u, 65020u, 200000u], attrs.AsPath);
    }

    // ------------------------------------------------- aggregator pairing (§4.2.3)

    [Fact]
    public void AsTransAggregatorWithoutAs4Aggregator_DiscardsAggregator_RoutesKept()
    {
        // RFC 4271 §6.3: a recognized optional attribute failing a check MUST be discarded while the
        // UPDATE keeps being processed. Pre-fix: 3/9 → every NLRI in the UPDATE withdrawn.
        var update = Update(Aggregator6(AsTrans));

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: false);

        Assert.Contains(BgpConstants.Attribute.Aggregator, attrs.DiscardedAttributes);
        Assert.Equal([65010u, 65020u, 65030u], attrs.AsPath);
    }

    [Fact]
    public void As4AggregatorWithoutAggregator_DiscardsAs4Aggregator_RoutesKept()
    {
        var update = Update(As4Aggregator8(200000u));

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: false);

        Assert.Contains(BgpConstants.Attribute.As4Aggregator, attrs.DiscardedAttributes);
        Assert.Equal([65010u, 65020u, 65030u], attrs.AsPath);
    }

    [Fact]
    public void AsTransAggregator_WithAs4Aggregator_IsNotDiscarded()
    {
        // Control for the pairing rule: the conforming combination stays untouched.
        var update = Update(Aggregator6(AsTrans), As4Aggregator8(200000u));

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: false);

        Assert.Empty(attrs.DiscardedAttributes);
    }

    [Fact]
    public void StructurallyMalformedAggregator_StillDiscardsOnlyThatAttribute()
    {
        // Control for the #306 behaviour that must not regress: a wrong-LENGTH AGGREGATOR is a
        // malformed attribute (RFC 7606 §7.7) and is discarded — not escalated to a pairing error,
        // not escalated to treat-as-withdraw.
        var update = Update(
            Raw(BgpConstants.Attribute.Aggregator, 0x5B, 0xA0, 0x00, 0x00, 0x00),   // 5 bytes, wrong
            As4Aggregator8(200000u));

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: false);

        Assert.Contains(BgpConstants.Attribute.Aggregator, attrs.DiscardedAttributes);
        Assert.DoesNotContain(BgpConstants.Attribute.As4Aggregator, attrs.DiscardedAttributes);
    }

    // ------------------------------------ AS4_PATH merge gating (§4.2.3, issue #529)

    [Fact]
    public void As4Path_IsIgnored_WhenAggregatorIsNotAsTrans()
    {
        // RFC 6793 §4.2.3: with both aggregator attributes present and the AGGREGATOR AS NOT
        // AS_TRANS, "the AS4_AGGREGATOR attribute and the AS4_PATH attribute SHALL be ignored …
        // and the AS_PATH attribute SHALL be taken as the AS path information".
        //
        // Pre-fix the merge ran unconditionally, producing [65010, 65020, 200000] — a path the peer
        // never sent. That corrupted path feeds AS-loop detection, so it could flip the verdict in
        // both directions (a false "loop" silently dropping a valid route).
        var update = Update(
            AttributeHelper.WriteAs4Path([200000u]),
            Aggregator6(65000u),
            As4Aggregator8(200000u));

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: false);

        Assert.Equal([65010u, 65020u, 65030u], attrs.AsPath);
        Assert.Empty(attrs.DiscardedAttributes);   // the attributes are valid, just not applicable
    }

    [Fact]
    public void As4Path_IsStillMerged_WhenAggregatorIsAsTrans()
    {
        // Control: AS_TRANS in AGGREGATOR is exactly the case where §4.2.3 does NOT suppress the
        // reconstruction — the merge must keep happening.
        var update = Update(
            AttributeHelper.WriteAs4Path([200000u]),
            Aggregator6(AsTrans),
            As4Aggregator8(200000u));

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: false);

        Assert.Equal([65010u, 65020u, 200000u], attrs.AsPath);
    }

    [Fact]
    public void As4Path_IsStillMerged_WhenNoAggregatorAttributesArePresent()
    {
        // Control: with no AGGREGATOR at all there is no §4.2.3 exemption, so the ordinary
        // trailing-sequence reconstruction still applies.
        var update = Update(AttributeHelper.WriteAs4Path([200000u]));

        var attrs = UpdateCodec.ParseRouteAttributes(update, fourByteAsnSession: false);

        Assert.Equal([65010u, 65020u, 200000u], attrs.AsPath);
    }
}
