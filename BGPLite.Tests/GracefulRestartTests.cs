using BGPLite.Configuration;
using BGPLite.Protocol;

namespace BGPLite.Tests;

public class GracefulRestartTests
{
    [Fact]
    public void Capability_Encodes_Exact_Byte_Layout()
    {
        // R=1, time=0x1FF (511), F=1:
        //   byte0 = 0x80 (R) | 0x01 (high nibble of 0x1FF) = 0x81
        //   byte1 = 0xFF (low byte of 0x1FF)
        //   IPv4(0x00,0x01) + Unicast(0x01) + AF flags 0x80 (F)
        //   IPv6(0x00,0x02) + Unicast(0x01) + AF flags 0x80 (F)
        var cap = BgpCapabilityInfo.GracefulRestart(restartState: true, restartTime: 0x1FF, ipv4Forwarding: true, ipv6Forwarding: true);

        Assert.Equal(BgpConstants.Capability.GracefulRestart, cap.Code);
        Assert.Equal(new byte[] { 0x81, 0xFF, 0x00, 0x01, 0x01, 0x80, 0x00, 0x02, 0x01, 0x80 }, cap.Data);

        // A legacy v4-only payload (one tuple, no IPv6) still parses — v6 forwarding = false.
        var legacy = BgpCapabilityInfo.TryParseGracefulRestart(new byte[] { 0x81, 0xFF, 0x00, 0x01, 0x01, 0x80 });
        Assert.NotNull(legacy);
        Assert.True(legacy!.Value.Ipv4UnicastForwarding);
        Assert.False(legacy.Value.Ipv6UnicastForwarding);
    }

    [Fact]
    public void Capability_RoundTrips_Through_Parser()
    {
        var cap = BgpCapabilityInfo.GracefulRestart(true, 120, true, ipv6Forwarding: true);
        var parsed = BgpCapabilityInfo.TryParseGracefulRestart(cap.Data);

        Assert.NotNull(parsed);
        Assert.True(parsed!.Value.RestartState);
        Assert.Equal((ushort)120, parsed.Value.RestartTime);
        Assert.True(parsed.Value.Ipv4UnicastForwarding);
        Assert.True(parsed.Value.Ipv6UnicastForwarding);
    }

    [Fact]
    public void Capability_FlagsFalse_ParsesFalse()
    {
        var cap = BgpCapabilityInfo.GracefulRestart(false, 60, false);
        var parsed = BgpCapabilityInfo.TryParseGracefulRestart(cap.Data);

        Assert.NotNull(parsed);
        Assert.False(parsed!.Value.RestartState);
        Assert.Equal((ushort)60, parsed.Value.RestartTime);
        Assert.False(parsed.Value.Ipv4UnicastForwarding);
        Assert.Equal(0x00, cap.Data[0]);   // no R, no high time bits
        Assert.Equal(0x00, cap.Data[5]);   // no IPv4 F
        Assert.Equal(0x00, cap.Data[9]);   // no IPv6 F
    }

    [Fact]
    public void Capability_Parser_RejectsMalformed()
    {
        Assert.Null(BgpCapabilityInfo.TryParseGracefulRestart([]));
        Assert.Null(BgpCapabilityInfo.TryParseGracefulRestart(new byte[] { 0x80 })); // only 1 byte
        // RFC 4724 §2: the value is a 2-byte header plus COMPLETE 4-byte tuples — a trailing
        // partial tuple is malformed, not a tail to ignore.
        Assert.Null(BgpCapabilityInfo.TryParseGracefulRestart(new byte[] { 0x80, 0x3C, 0x00, 0x01, 0x01 }));
        Assert.Null(BgpCapabilityInfo.TryParseGracefulRestart(new byte[] { 0x80, 0x3C, 0x00 }));
    }

    [Fact]
    public void Capability_Clamps_RestartTime_To_12Bit_Max()
    {
        // RFC 4724 §2.2: Restart Time is 12 bits (0..4095). Out-of-range MUST clamp, not silently
        // truncate. 5000 & 0xFFF would be 904 (the old buggy behavior) — must instead be 4095.
        var cap = BgpCapabilityInfo.GracefulRestart(true, 5000, true);
        var parsed = BgpCapabilityInfo.TryParseGracefulRestart(cap.Data);

        Assert.NotNull(parsed);
        Assert.Equal((ushort)4095, parsed!.Value.RestartTime);
    }

    [Fact]
    public void Capability_Preserves_Max_12Bit_RestartTime()
    {
        var cap = BgpCapabilityInfo.GracefulRestart(true, 4095, true);
        var parsed = BgpCapabilityInfo.TryParseGracefulRestart(cap.Data);
        Assert.Equal((ushort)4095, parsed!.Value.RestartTime);
    }

    [Fact]
    public void EndOfRib_Is_MinimumLength_Update()
    {
        // End-of-RIB for IPv4 unicast = minimum UPDATE: 19-byte header + 4 (withdrawn_len=0, attr_len=0).
        var msg = new BgpUpdateMessage();
        Assert.Equal(23, BgpMessageWriter.GetBufferSize(msg));

        var buf = new byte[64];
        var n = BgpMessageWriter.WriteMessage(msg, buf);
        Assert.Equal(23, n);

        // Round-trips as an empty UPDATE (no withdrawn, no attrs, no NLRI).
        var read = (BgpUpdateMessage)BgpMessageReader.ReadMessage(buf.AsSpan(0, n));
        Assert.Empty(read.WithdrawnRoutes);
        Assert.Empty(read.PathAttributes);
        Assert.Empty(read.Nlri);
    }

    [Fact]
    public void Open_Encodes_And_Decodes_GracefulRestart()
    {
        var open = new BgpOpenMessage
        {
            Asn = 65001,
            HoldTime = 180,
            RouterId = 0x0A000001,
            Capabilities =
            [
                BgpCapabilityInfo.FourOctetAsn(65001),
                BgpCapabilityInfo.GracefulRestart(true, 120, true, ipv6Forwarding: false)
            ]
        };

        var buf = new byte[128];
        var n = BgpMessageWriter.WriteMessage(open, buf);
        var read = (BgpOpenMessage)BgpMessageReader.ReadMessage(buf.AsSpan(0, n));

        var gr = CapabilityHelper.GetGracefulRestart(read);
        Assert.NotNull(gr);
        Assert.True(gr!.Value.RestartState);
        Assert.Equal((ushort)120, gr.Value.RestartTime);
        Assert.True(gr.Value.Ipv4UnicastForwarding);
    }

    [Fact]
    public void GetGracefulRestart_ReturnsNull_WhenNotAdvertised()
    {
        var open = new BgpOpenMessage
        {
            Asn = 65001,
            HoldTime = 180,
            RouterId = 0x0A000001,
            Capabilities = [BgpCapabilityInfo.FourOctetAsn(65001)]
        };
        Assert.Null(CapabilityHelper.GetGracefulRestart(open));
    }

    [Fact]
    public void BgpConfig_Defaults()
    {
        var cfg = new BgpConfig();
        Assert.True(cfg.GracefulRestart);
        Assert.Equal(120, cfg.RestartTime);
        Assert.True(cfg.GracefulRestartForwardingState);
    }
}
