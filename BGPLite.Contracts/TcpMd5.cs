using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BGPLite.Contracts;

/// <summary>
/// TCP MD5 Signature Option (RFC 2385) — per-peer socket option plumbing (#36). The kernel
/// verifies the MD5 signature on every segment of the connection: with a key attached to the
/// listening socket for a peer's address, unsigned segments are dropped before they ever reach
/// the application, so a peer without the password cannot complete the handshake.
/// <para>
/// Linux: <c>TCP_MD5SIG</c> (opt 14) with <c>struct tcp_md5sig</c> (sockaddr_storage, keylen,
/// 80-byte key). macOS/Darwin: <c>TCP_MD5SIG</c> (opt 0x10) with <c>struct tcpmd5sig</c>
/// (keylen first, then the key, then sockaddr_storage). Both attach the key to the
/// (remote address, port 0) pair = "any port from this peer". Windows has no TCP-MD5 support.
/// </para>
/// <para>
/// Accepted sockets inherit the listener's key on Linux. Re-applying to the accepted socket is
/// harmless (idempotent) and covers platforms/setups where inheritance does not hold.
/// </para>
/// </summary>
public static class TcpMd5
{
    private const int IpprotoTcp = 6;
    private const int LinuxTcpMd5Sig = 14;
    private const int DarwinTcpMd5Sig = 0x10;
    private const int MaxKeyBytes = 80;
    private const int SockaddrStorageSize = 128;

    /// <summary>Whether this platform can enforce TCP-MD5 (Linux and macOS; not Windows).</summary>
    public static bool IsSupported =>
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>UTF-8 byte length limit of the shared key (Linux tcpm_key[80]).</summary>
    public const int PasswordMaxBytes = MaxKeyBytes;

    /// <summary>
    /// Attaches the shared key for <paramref name="peer"/> to <paramref name="socket"/>.
    /// Call on the listening socket (before the peer's SYN arrives) and, idempotently, on the
    /// accepted socket.
    /// </summary>
    public static void Apply(Socket socket, IPAddress peer, ReadOnlySpan<byte> key)
        => Apply(socket, new IPEndPoint(peer, 0), key);

    public static void Apply(Socket socket, IPEndPoint peer, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(peer);
        if (key.IsEmpty || key.Length > MaxKeyBytes)
            throw new ArgumentException($"TCP-MD5 key must be 1..{MaxKeyBytes} bytes.", nameof(key));

        if (OperatingSystem.IsLinux())
            socket.SetRawSocketOption(IpprotoTcp, LinuxTcpMd5Sig, LinuxTcpMd5SigValue(peer, key));
        else if (OperatingSystem.IsMacOS())
            socket.SetRawSocketOption(IpprotoTcp, DarwinTcpMd5Sig, DarwinTcpMd5SigValue(peer, key));
        else
            throw new PlatformNotSupportedException("TCP-MD5 (RFC 2385) is supported on Linux and macOS only.");
    }

    /// <summary>Removes the key for <paramref name="peer"/> from <paramref name="socket"/> (disables enforcement).</summary>
    public static void Clear(Socket socket, IPAddress peer)
        => Clear(socket, new IPEndPoint(peer, 0));

    public static void Clear(Socket socket, IPEndPoint peer)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(peer);

        if (OperatingSystem.IsLinux())
            socket.SetRawSocketOption(IpprotoTcp, LinuxTcpMd5Sig, LinuxTcpMd5SigValue(peer, ReadOnlySpan<byte>.Empty));
        else if (OperatingSystem.IsMacOS())
            socket.SetRawSocketOption(IpprotoTcp, DarwinTcpMd5Sig, DarwinTcpMd5SigValue(peer, ReadOnlySpan<byte>.Empty));
        else
            throw new PlatformNotSupportedException("TCP-MD5 (RFC 2385) is supported on Linux and macOS only.");
    }

    /// <summary>Builds <c>struct tcp_md5sig</c>: sockaddr_storage(128) + pad1(2) + keylen(2) + pad2(4) + key(80).</summary>
    private static byte[] LinuxTcpMd5SigValue(IPEndPoint peer, ReadOnlySpan<byte> key)
    {
        var buffer = new byte[SockaddrStorageSize + 2 + 2 + 4 + MaxKeyBytes];
        WriteSockaddr(peer, buffer);
        // __tcpm_pad1 (2 bytes) stays zero.
        buffer[SockaddrStorageSize + 2] = (byte)key.Length;          // tcpm_keylen low byte (host-endian u16)
        buffer[SockaddrStorageSize + 3] = 0;
        // __tcpm_pad2 (4 bytes) stays zero.
        key.CopyTo(new Span<byte>(buffer, SockaddrStorageSize + 8, key.Length));
        return buffer;
    }

    /// <summary>Builds <c>struct tcpmd5sig</c>: keylen(1) + key(80) + sockaddr_storage(128).</summary>
    private static byte[] DarwinTcpMd5SigValue(IPEndPoint peer, ReadOnlySpan<byte> key)
    {
        var buffer = new byte[1 + MaxKeyBytes + SockaddrStorageSize];
        buffer[0] = (byte)key.Length;                                 // tcpmd5keylen
        key.CopyTo(new Span<byte>(buffer, 1, key.Length));
        WriteSockaddr(peer, buffer.AsSpan(1 + MaxKeyBytes));
        return buffer;
    }

    /// <summary>Writes a sockaddr_storage-shaped sockaddr_in/inn6 for the peer, port 0 = "any port".</summary>
    private static void WriteSockaddr(IPEndPoint peer, Span<byte> destination)
    {
        destination.Clear();
        var port = (ushort)peer.Port;
        if (peer.Address.AddressFamily == AddressFamily.InterNetwork)
        {
            destination[0] = (byte)AddressFamily.InterNetwork;        // AF_INET (little-endian host order)
            destination[2] = (byte)(port >> 8);                       // port, network byte order
            destination[3] = (byte)port;
            peer.Address.TryWriteBytes(destination.Slice(4, 4), out _);
        }
        else
        {
            destination[0] = (byte)AddressFamily.InterNetworkV6;      // AF_INET6
            destination[2] = (byte)(port >> 8);
            destination[3] = (byte)port;
            peer.Address.TryWriteBytes(destination.Slice(8, 16), out _);
        }
    }

    /// <summary>True when <paramref name="password"/> would be accepted as a TCP-MD5 key.</summary>
    public static bool IsValidPassword(string? password) =>
        !string.IsNullOrEmpty(password) &&
        Encoding.UTF8.GetByteCount(password) is >= 1 and <= MaxKeyBytes;

    /// <summary>Encodes a validated password to its wire key bytes.</summary>
    public static byte[] KeyBytes(string password) => Encoding.UTF8.GetBytes(password);
}
