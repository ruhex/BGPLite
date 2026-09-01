using System.Buffers.Binary;

namespace BGPLite.Protocol;

public static class CapabilityHelper
{
    public static uint? GetRemoteAsn(BgpOpenMessage open)
    {
        foreach (var cap in open.Capabilities)
        {
            if (cap.Code == BgpConstants.Capability.FourOctetAsn && cap.Data.Length >= 4)
                return BinaryPrimitives.ReadUInt32BigEndian(cap.Data);
        }
        return null;
    }

    public static bool SupportsRouteRefresh(BgpOpenMessage open)
    {
        foreach (var cap in open.Capabilities)
        {
            if (cap.Code == BgpConstants.Capability.RouteRefresh)
                return true;
        }
        return false;
    }

    /// <summary>Detects the MP-BGP IPv6/Unicast capability (AFI=2/SAFI=1) in the peer's OPEN
    /// (#15 phase 2): the peer can send IPv6 routes via MP_REACH_NLRI.</summary>
    public static bool SupportsMultiprotocolIpv6Unicast(BgpOpenMessage open)
    {
        foreach (var cap in open.Capabilities)
        {
            if (cap.Code == BgpConstants.Capability.Multiprotocol &&
                cap.Data.Length >= 4 &&
                BinaryPrimitives.ReadUInt16BigEndian(cap.Data) == BgpConstants.Afi.IPv6 &&
                cap.Data[3] == BgpConstants.Safi.Unicast)
                return true;
        }
        return false;
    }

    public static bool SupportsMultiprotocolIpv4Unicast(BgpOpenMessage open)
    {
        foreach (var cap in open.Capabilities)
        {
            if (cap.Code == BgpConstants.Capability.Multiprotocol &&
                cap.Data.Length >= 4 &&
                BinaryPrimitives.ReadUInt16BigEndian(cap.Data) == BgpConstants.Afi.IPv4 &&
                cap.Data[3] == BgpConstants.Safi.Unicast)
                return true;
        }
        return false;
    }

    public static uint GetEffectiveAsn(BgpOpenMessage open)
    {
        return GetRemoteAsn(open) ?? open.Asn;
    }

    /// <summary>Returns the peer's Graceful Restart parameters, or null if not advertised / malformed.</summary>
    public static (bool RestartState, ushort RestartTime, bool Ipv4UnicastForwarding)? GetGracefulRestart(BgpOpenMessage open)
    {
        foreach (var cap in open.Capabilities)
        {
            if (cap.Code == BgpConstants.Capability.GracefulRestart)
            {
                var parsed = BgpCapabilityInfo.TryParseGracefulRestart(cap.Data);
                if (parsed.HasValue) return parsed;
            }
        }
        return null;
    }
}
