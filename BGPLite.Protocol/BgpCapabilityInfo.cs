using System.Buffers.Binary;

namespace BGPLite.Protocol;

public sealed class BgpCapabilityInfo
{
    public byte Code { get; init; }
    public byte[] Data { get; init; } = [];

    public static BgpCapabilityInfo FourOctetAsn(uint asn)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(data, asn);
        return new BgpCapabilityInfo { Code = BgpConstants.Capability.FourOctetAsn, Data = data };
    }

    public static BgpCapabilityInfo RouteRefresh() => new()
    {
        Code = BgpConstants.Capability.RouteRefresh
    };

    public static BgpCapabilityInfo MultiprotocolIpv4Unicast() => new()
    {
        Code = BgpConstants.Capability.Multiprotocol,
        Data = [(byte)(BgpConstants.Afi.IPv4 >> 8), (byte)BgpConstants.Afi.IPv4, 0x00, BgpConstants.Safi.Unicast]
    };

    /// <summary>MP-BGP IPv6/Unicast capability (RFC 4760 §8, code 1): AFI=2/SAFI=1 (#15 phase 2).
    /// Signals that this speaker can receive IPv6 routes via MP_REACH_NLRI.</summary>
    public static BgpCapabilityInfo MultiprotocolIpv6Unicast() => new()
    {
        Code = BgpConstants.Capability.Multiprotocol,
        Data = [(byte)(BgpConstants.Afi.IPv6 >> 8), (byte)BgpConstants.Afi.IPv6, 0x00, BgpConstants.Safi.Unicast]
    };

    /// <summary>
    /// Graceful Restart capability (RFC 4724, code 64) for IPv4/Unicast. Value layout:
    /// byte 0 = Restart Flags (bit 7 = R) | high 4 bits of Restart Time,
    /// byte 1 = low 8 bits of Restart Time, then per-AF [AFI(2), SAFI(1), AF Flags(bit 7 = F)].
    /// </summary>
    public static BgpCapabilityInfo GracefulRestart(bool restartState, ushort restartTime, bool forwardingState)
    {
        // RFC 4724 §2.2: Restart Time is a 12-bit field (0..4095). Clamp defensively so no caller
        // can silently truncate an out-of-range value into a wrong on-wire timer.
        var time = Math.Min(restartTime, (ushort)4095);
        var data = new byte[6];
        data[0] = (byte)((restartState ? BgpConstants.GracefulRestartFlag.RestartState : 0x00) | ((time >> 8) & 0x0F));
        data[1] = (byte)(time & 0xFF);
        data[2] = (byte)(BgpConstants.Afi.IPv4 >> 8);
        data[3] = (byte)BgpConstants.Afi.IPv4;
        data[4] = BgpConstants.Safi.Unicast;
        data[5] = (byte)(forwardingState ? BgpConstants.GracefulRestartFlag.ForwardingState : 0x00);
        return new BgpCapabilityInfo { Code = BgpConstants.Capability.GracefulRestart, Data = data };
    }

    /// <summary>Parses a Graceful Restart capability value. Returns null if malformed.</summary>
    public static (bool RestartState, ushort RestartTime, bool Ipv4UnicastForwarding)? TryParseGracefulRestart(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return null;
        var restartState = (data[0] & BgpConstants.GracefulRestartFlag.RestartState) != 0;
        var restartTime = (ushort)(((data[0] & 0x0F) << 8) | data[1]);
        var ipv4UnicastForwarding = false;
        var i = 2;
        while (i + 4 <= data.Length)
        {
            var afi = (ushort)((data[i] << 8) | data[i + 1]);
            if (afi == BgpConstants.Afi.IPv4 && data[i + 2] == BgpConstants.Safi.Unicast)
                ipv4UnicastForwarding |= (data[i + 3] & BgpConstants.GracefulRestartFlag.ForwardingState) != 0;
            i += 4;
        }
        return (restartState, restartTime, ipv4UnicastForwarding);
    }

    public uint ReadAsn() => BinaryPrimitives.ReadUInt32BigEndian(Data);
}
