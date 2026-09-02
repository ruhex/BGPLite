using System.Net;
using System.Net.Sockets;

namespace BGPLite.Protocol;

/// <summary>
/// A family-aware IP prefix: IPv4 lives in the LOW 32 bits with <see cref="IsIpv4"/> set
/// (e.g. 10.0.0.0/8 → <c>0x0A000000</c>); IPv6 uses the full <see cref="UInt128"/>. The
/// constructor is canonicalizing — host bits are masked off, so an <see cref="IpPrefix"/>
/// value is always a valid network address and is safe to use as a routing key (dual-stack:
/// the IPv4 low-bits form cannot collide with a full-128 IPv6 form that carries
/// <see cref="IsIpv4"/> = false).
/// <para>
/// Length domain: 0..32 for IPv4, 0..128 for IPv6. <c>ToString</c> renders IPv4 dotted-quad
/// and IPv6 in RFC 5952 compressed form.
/// </para>
/// </summary>
public readonly record struct IpPrefix : IComparable<IpPrefix>
{
    public UInt128 Address { get; }
    public byte Length { get; }
    public bool IsIpv4 { get; }

    /// <summary>IPv4 prefix: <paramref name="ipv4Address"/> occupies the low 32 bits.</summary>
    public IpPrefix(uint ipv4Address, byte length)
        : this(ipv4Address, length, isIpv4: true)
    {
    }

    /// <summary>Family-aware prefix. IPv4 values occupy the low 32 bits; IPv6 the full 128.
    /// Host bits are masked to the network address.</summary>
    public IpPrefix(UInt128 address, byte length, bool isIpv4)
    {
        if (isIpv4 && length > 32)
            throw new ArgumentOutOfRangeException(nameof(length), length, "IPv4 prefix length must be in 0..32.");
        if (length > 128)
            throw new ArgumentOutOfRangeException(nameof(length), length, "Prefix length must be in 0..128.");

        Address = isIpv4 ? address & Ipv4Mask(length) : address & V6Mask(length);
        Length = length;
        IsIpv4 = isIpv4;
    }

    public IpPrefix(IPAddress address, byte length)
        : this(BgpConstants.ToUInt128(address), length, isIpv4: address.AddressFamily == AddressFamily.InterNetwork)
    {
    }

    public static bool IsValidLength(byte length, bool isIpv4) => isIpv4 ? length <= 32 : length <= 128;

    /// <summary>Network mask for the family, aligned to the value's bit layout.</summary>
    public static UInt128 Mask(byte length, bool isIpv4) =>
        isIpv4 ? Ipv4Mask(length) : V6Mask(length);

    private static UInt128 Ipv4Mask(byte length) =>
        length == 0 ? UInt128.Zero : (UInt128)(0xFFFFFFFFu << (32 - length));

    private static UInt128 V6Mask(byte length) =>
        length == 0 ? UInt128.Zero : UInt128.MaxValue << (128 - length);

    public int CompareTo(IpPrefix other)
    {
        var byFamily = IsIpv4.CompareTo(other.IsIpv4);
        if (byFamily != 0) return byFamily;
        var byAddress = Address.CompareTo(other.Address);
        if (byAddress != 0) return byAddress;
        return Length.CompareTo(other.Length);
    }

    public override string ToString()
    {
        if (IsIpv4)
            return $"{BgpConstants.UintToIPAddress((uint)Address)}/{Length}";

        Span<byte> bytes = stackalloc byte[16];
        for (var i = 0; i < 16; i++)
            bytes[i] = (byte)(Address >> (120 - i * 8));
        return $"{new IPAddress(bytes)}/{Length}";
    }
}
