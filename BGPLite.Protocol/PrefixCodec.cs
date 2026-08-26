using System.Buffers.Binary;

namespace BGPLite.Protocol;

public static class PrefixCodec
{
    public static int Encode(IpPrefix prefix, Span<byte> buffer)
    {
        var length = prefix.Length;
        if (length > 32)
            throw new ArgumentOutOfRangeException(nameof(prefix), length, "IPv4 prefix length must be in 0..32.");

        if (buffer.Length < 1)
            throw new ArgumentOutOfRangeException(nameof(buffer), buffer.Length, "Buffer must hold at least the prefix length byte.");

        if (length == 0)
        {
            buffer[0] = 0;
            return 1;
        }

        var byteCount = (length + 7) / 8;
        if (buffer.Length < 1 + byteCount)
            throw new ArgumentOutOfRangeException(nameof(buffer), buffer.Length, $"Buffer too small: need {1 + byteCount} bytes for prefix length {length}.");
        buffer[0] = length;

        var addr = prefix.Address;
        for (var i = 0; i < byteCount; i++)
            buffer[1 + i] = (byte)(addr >> (24 - i * 8));

        return 1 + byteCount;
    }

    /// <summary>
    /// Decodes one NLRI prefix from the head of <paramref name="buffer"/>. Throws
    /// <see cref="BgpParseException"/> (Update Message Error, Invalid Network Field per RFC 4271
    /// §6.3) on any malformed input — a prefix-length byte &gt; 32, or a buffer shorter than the
    /// declared prefix bytes — so the caller surfaces it through the treat-as-withdraw path instead
    /// of the previous <see cref="ArgumentOutOfRangeException"/> that escaped the read loop and
    /// tore down the session (#222, RFC 4271 §6.3 / RFC 7606 §2).
    /// </summary>
    public static (IpPrefix prefix, int bytesConsumed) Decode(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty)
            throw new BgpParseException("Truncated NLRI: buffer too small to contain a prefix length byte",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.InvalidNetworkField);

        var length = buffer[0];
        if (length > 32)
            throw new BgpParseException($"Invalid NLRI prefix length: {length} (must be 0..32)",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.InvalidNetworkField);

        if (length == 0)
            return (new IpPrefix(0, 0), 1);

        var byteCount = (length + 7) / 8;
        if (buffer.Length < 1 + byteCount)
            throw new BgpParseException($"Truncated NLRI: need {1 + byteCount} bytes for prefix length {length}, have {buffer.Length}",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.InvalidNetworkField);
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
