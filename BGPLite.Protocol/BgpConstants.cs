using System.Net;
using System.Net.Sockets;

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

        // Subcode namespaces are per-error-code: the same numeric value means different things
        // under Open Message Error (2) and Update Message Error (3) — e.g. 3 is Bad BGP Identifier
        // in the former and Missing Well-known Attribute in the latter (RFC 4271 §6.2/§6.3).
        // The dual naming below (BadBgpIdentifier/MissingWellKnownAttribute both = 3) is intentional.

        // Message Header Error subcodes (RFC 4271 §6.1) — used with ErrorCode = MessageHeaderError.
        public const byte ConnectionNotSynchronized = 1;
        public const byte BadMessageLength = 2;
        public const byte BadMessageType = 3;

        // Open Message Error subcodes (RFC 4271 §6.2) — used with ErrorCode = OpenMessageError
        public const byte UnsupportedVersion = 1;
        public const byte BadPeerAs = 2;
        public const byte BadBgpIdentifier = 3;
        public const byte UnsupportedOptionalParameter = 4;
        public const byte UnacceptableHoldTime = 6;
        public const byte UnsupportedCapability = 7;

        // Update Message Error subcodes (RFC 4271 §6.3) — used with ErrorCode = UpdateMessageError
        public const byte MalformedAttributeList = 1;
        public const byte UnrecognizedWellKnownAttribute = 2;
        public const byte MissingWellKnownAttribute = 3;
        public const byte AttributeFlagsError = 4;
        public const byte AttributeLengthError = 5;
        public const byte InvalidOriginAttribute = 6;
        // Subcode 7 (AS Routing Loop) is "[Deprecated - see Appendix A]" in RFC 4271 §4.5 and
        // is intentionally not defined here.
        public const byte InvalidNextHopAttribute = 8;
        public const byte OptionalAttributeError = 9;
        public const byte InvalidNetworkField = 10;
        public const byte MalformedAsPath = 11;

        // RFC 4486 Cease subcodes (apply when ErrorCode = Cease = 6)
        public const byte CeaseMaxPrefixes = 1;
        public const byte CeaseAdministrativeShutdown = 2;
        public const byte CeasePeerDeconfigured = 3;
        public const byte CeaseAdministrativeReset = 6;
        public const byte CeaseConnectionRejected = 7;
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
        public const byte As4Path = 17;
        public const byte As4Aggregator = 18;
        public const byte LargeCommunity = 32;

        public const byte FlagOptional = 0x80;
        public const byte FlagTransitive = 0x40;
        public const byte FlagPartial = 0x20;
        public const byte FlagExtendedLength = 0x10;
        /// <summary>RFC 4271 §4.3: bit 0x08 is reserved and MUST be zero on the wire.</summary>
        public const byte FlagReserved = 0x08;
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
        public const ushort IPv6 = 2; // RFC 1700 / IANA address family numbers (#15 phase 1)
    }

    public static class Safi
    {
        public const byte Unicast = 1;
    }

    /// <summary>RFC 1997 well-known communities.</summary>
    public static class Community
    {
        public const uint NoExport = 0xFFFFFF01u;
        public const uint NoAdvertise = 0xFFFFFF02u;
        public const uint NoExportSubconfed = 0xFFFFFF03u;
    }

    public static uint IPAddressToUint(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("IPv4 address required", nameof(address));

        var bytes = address.GetAddressBytes();
        return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
    }

    public static IPAddress UintToIPAddress(uint address) =>
        new([(byte)(address >> 24), (byte)(address >> 16), (byte)(address >> 8), (byte)address]);

    // ---- #15 phase 1: 16-byte-aware (de)serializers -------------------------------------------

    /// <summary>
    /// Non-truncating 128-bit form of an IP address: IPv4 in the low 32 bits (family carried
    /// separately), IPv6 as the full big-endian 128 bits. Never truncates or maps silently.
    /// </summary>
    public static UInt128 ToUInt128(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var bytes = address.GetAddressBytes();
        if (bytes.Length is not 4 and not 16)
            throw new ArgumentException($"Unsupported address size: {bytes.Length} bytes.", nameof(address));

        UInt128 value = 0;
        var shift = bytes.Length == 4 ? 96 : 0; // IPv4 → low 32 bits
        foreach (var b in bytes)
        {
            value = (value << 8) | b;
            _ = shift;
        }
        return value;
    }

    /// <summary>Rebuilds an IP address from the 128-bit form produced by <see cref="ToUInt128"/>.
    /// <paramref name="isIpv4"/> selects the family: only the low 32 bits are read (no truncation
    /// of a genuine 128-bit value — that would be a caller bug, and it throws).</summary>
    public static IPAddress FromUInt128(UInt128 value, bool isIpv4)
    {
        if (isIpv4)
        {
            if (value > uint.MaxValue)
                throw new InvalidOperationException(
                    $"Cannot convert a 128-bit value above uint.MaxValue to an IPv4 address (got 0x{value:X}).");
            return UintToIPAddress((uint)value);
        }

        Span<byte> bytes = stackalloc byte[16];
        for (var i = 0; i < 16; i++)
            bytes[i] = (byte)(value >> (120 - i * 8));
        return new IPAddress(bytes);
    }

    /// <summary>
    /// Wrong-family guard for the IPv4-only consumers of a 128-bit value (#13/#15): throws
    /// instead of silently truncating the high bits.
    /// </summary>
    public static uint ToUint32OrThrow(UInt128 value, string field)
    {
        if (value > uint.MaxValue)
            throw new InvalidOperationException(
                $"128-bit value does not fit in 32 bits — wrong address family for {field} (got 0x{value:X}).");
        return (uint)value;
    }
}
