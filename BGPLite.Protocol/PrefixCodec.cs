using System.Buffers.Binary;

namespace BGPLite.Protocol;

public static class PrefixCodec
{
    /// <summary>
    /// Encodes one NLRI prefix, family-aware (#15 phase 1): IPv4 → length byte (0..32) plus at
    /// most 4 data bytes; IPv6 → length byte (0..128) plus at most 16 data bytes (big-endian,
    /// host bits cleared by <see cref="IpPrefix"/>).
    /// </summary>
    public static int Encode(IpPrefix prefix, Span<byte> buffer)
    {
        var length = prefix.Length;
        var maxLen = prefix.IsIpv4 ? 32 : 128;
        var maxBytes = prefix.IsIpv4 ? 4 : 16;
        if (length > maxLen)
            throw new ArgumentOutOfRangeException(nameof(prefix), length,
                $"{(prefix.IsIpv4 ? "IPv4" : "IPv6")} prefix length must be in 0..{maxLen}.");

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

        if (prefix.IsIpv4)
        {
            var addr = BgpConstants.ToUint32OrThrow(prefix.Address, "IPv4 NLRI");
            for (var i = 0; i < byteCount; i++)
                buffer[1 + i] = (byte)(addr >> (24 - i * 8));
        }
        else
        {
            for (var i = 0; i < byteCount; i++)
                buffer[1 + i] = (byte)(prefix.Address >> (128 - (i + 1) * 8));
        }

        return 1 + byteCount;
    }

    /// <summary>
    /// Decodes one IPv6 NLRI prefix from the head of <paramref name="buffer"/> (MP_REACH/MP_UNREACH
    /// context, #15 phase 2 groundwork): a length byte 0..128 plus big-endian data bytes, masked to
    /// the network address. Same malformed-input contract as <see cref="Decode"/> —
    /// <see cref="BgpParseException"/> with Update Message Error / Invalid Network Field.
    /// </summary>
    public static (IpPrefix prefix, int bytesConsumed) Decode6(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty)
            throw new BgpParseException("Truncated NLRI: buffer too small to contain a prefix length byte",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.InvalidNetworkField);

        var length = buffer[0];
        if (length > 128)
            throw new BgpParseException($"Invalid NLRI prefix length: {length} (must be 0..128)",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.InvalidNetworkField);

        if (length == 0)
            return (new IpPrefix(UInt128.Zero, 0, isIpv4: false), 1);

        var byteCount = (length + 7) / 8;
        if (buffer.Length < 1 + byteCount)
            throw new BgpParseException($"Truncated NLRI: need {1 + byteCount} bytes for prefix length {length}, have {buffer.Length}",
                BgpConstants.Error.UpdateMessageError, BgpConstants.SubError.InvalidNetworkField);

        UInt128 addr = 0;
        for (var i = 0; i < byteCount; i++)
            addr |= (UInt128)buffer[1 + i] << (128 - (i + 1) * 8);

        addr &= IpPrefix.Mask(length, isIpv4: false);
        return (new IpPrefix(addr, length, isIpv4: false), 1 + byteCount);
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
