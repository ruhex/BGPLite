using System.Buffers.Binary;

namespace BGPLite.Protocol;

public static class PrefixCodec
{
    public static int Encode(IpPrefix prefix, Span<byte> buffer)
    {
        var length = prefix.Length;
        if (length > 32)
            throw new ArgumentOutOfRangeException(nameof(prefix), length, "IPv4 prefix length must be in 0..32.");

        if (length == 0)
        {
            buffer[0] = 0;
            return 1;
        }

        var byteCount = (length + 7) / 8;
        buffer[0] = length;

        var addr = prefix.Address;
        for (var i = 0; i < byteCount; i++)
            buffer[1 + i] = (byte)(addr >> (24 - i * 8));

        return 1 + byteCount;
    }

    public static (IpPrefix prefix, int bytesConsumed) Decode(ReadOnlySpan<byte> buffer)
    {
        var length = buffer[0];
        if (length > 32)
            throw new ArgumentOutOfRangeException(nameof(buffer), length, "IPv4 prefix length must be in 0..32.");

        if (length == 0)
            return (new IpPrefix(0, 0), 1);

        var byteCount = (length + 7) / 8;
        uint addr = 0;
        for (var i = 0; i < byteCount; i++)
            addr |= (uint)buffer[1 + i] << (24 - i * 8);

        addr &= 0xFFFFFFFF << (32 - length);
        return (new IpPrefix(addr, length), 1 + byteCount);
    }

    public static int EncodeList(ReadOnlySpan<IpPrefix> prefixes, Span<byte> buffer)
    {
        var offset = 0;
        for (var i = 0; i < prefixes.Length; i++)
            offset += Encode(prefixes[i], buffer[offset..]);
        return offset;
    }

    public static int DecodeList(ReadOnlySpan<byte> buffer, int length, Span<IpPrefix> prefixes)
    {
        var offset = 0;
        var count = 0;
        while (offset < length)
        {
            var (prefix, consumed) = Decode(buffer[offset..]);
            prefixes[count++] = prefix;
            offset += consumed;
        }
        return count;
    }
}
