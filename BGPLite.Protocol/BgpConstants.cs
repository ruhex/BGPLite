using System.Net;

namespace BGPLite.Protocol;

public static class BgpConstants
{
    public const int BgpVersion = 4;
    public const int BgpPort = 179;
    public const int MarkerSize = 16;
    public const int MessageHeaderSize = 19; // 16 marker + 2 length + 1 type
    public const int MinMessageSize = 19;
    public const int MaxMessageSize = 4096;
    public const int MinOpenMessageSize = 29;

    public const ushort DefaultKeepAlive = 60;
    public const ushort DefaultHoldTime = 180;
    public const int ConnectRetryDelay = 5;

    public static ReadOnlySpan<byte> Marker =>
        [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
         0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    public static class Error
    {
        public const byte MessageHeaderError = 1;
        public const byte OpenMessageError = 2;
        public const byte UpdateMessageError = 3;
        public const byte HoldTimerExpired = 4;
        public const byte FiniteStateMachineError = 5;
        public const byte Cease = 6;
    }

    public static class SubError
    {
        public const byte Unspecific = 0;
        public const byte UnsupportedVersion = 1;
        public const byte BadPeerAs = 2;
        public const byte MissingWellKnownAttribute = 3;
        public const byte BadBgpIdentifier = 3;
        public const byte UnacceptableHoldTime = 6;
    }

    public static class Attribute
    {
        public const byte Origin = 1;
        public const byte AsPath = 2;
        public const byte NextHop = 3;
        public const byte Med = 4;
        public const byte LocalPref = 5;
        public const byte AtomicAggregate = 6;
        public const byte Aggregator = 7;
        public const byte Community = 8;
        public const byte OriginatorId = 9;
        public const byte ClusterList = 10;
        public const byte ExtendedCommunity = 16;
        public const byte As4Path = 17;       // RFC 6793 §6 — 4-byte AS path for 2-byte peers
        public const byte As4PathAggregator = 18; // RFC 6793 — 4-byte aggregator
        public const byte LargeCommunity = 32;

        public const byte FlagOptional = 0x80;
        public const byte FlagTransitive = 0x40;
        public const byte FlagPartial = 0x20;
        public const byte FlagExtendedLength = 0x10;
    }

    public static class AsPath
    {
        public const byte AsSet = 1;
        public const byte AsSequence = 2;

        /// <summary>AS_TRANS (RFC 6793) — placeholder for 2-byte-only peers when local ASN > 65535.</summary>
        public const uint AsTrans = 23456;
    }

    public static class Capability
    {
        public const byte Multiprotocol = 1;
        public const byte RouteRefresh = 2;
        public const byte FourOctetAsn = 65;
        public const byte GracefulRestart = 64; // RFC 4724
    }

    /// <summary>Flag bits for the Graceful Restart capability (RFC 4724).</summary>
    public static class GracefulRestartFlag
    {
        public const byte RestartState = 0x80;  // R bit — most significant bit of Restart Flags
        public const byte ForwardingState = 0x80; // F bit — most significant bit of per-AF Flags
    }

    public static class Afi
    {
        public const ushort IPv4 = 1;
    }

    public static class Safi
    {
        public const byte Unicast = 1;
    }

    public static uint IPAddressToUint(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
    }

    public static IPAddress UintToIPAddress(uint address) =>
        new([(byte)(address >> 24), (byte)(address >> 16), (byte)(address >> 8), (byte)address]);
}
