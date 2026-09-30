using System.Net;
using System.Net.Sockets;
using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// TCP-MD5 (RFC 2385) per-peer socket plumbing. The smoke test runs everywhere Linux runs
/// (CI): a listening socket accepts <c>TcpMd5.Apply</c> and <c>Clear</c> without error. The
/// handshake test additionally proves the kernel-level effect on Linux loopback: a client WITH
/// the key completes the handshake, a client WITHOUT it never does (the kernel drops its SYN).
/// </summary>
public class TcpMd5Tests
{
    private static readonly byte[] Key = "test-md5-key"u8.ToArray();

    [Fact]
    public void IsValidPassword_Accepts_UpTo80Utf8Bytes()
    {
        Assert.True(TcpMd5.IsValidPassword("k"));
        Assert.True(TcpMd5.IsValidPassword(new string('x', 80)));
        Assert.False(TcpMd5.IsValidPassword(null));
        Assert.False(TcpMd5.IsValidPassword(""));
        Assert.False(TcpMd5.IsValidPassword(new string('x', 81)));  // 81 bytes > tcpm_key[80]
        Assert.False(TcpMd5.IsValidPassword(new string('é', 41)));  // 82 UTF-8 bytes
    }

    /// <summary>
    /// The sockaddr carries the KERNEL's address family, not .NET's (Winsock-derived) enum
    /// value: Linux wants AF_INET6 = 10, but (byte)AddressFamily.InterNetworkV6 is 23 — the
    /// kernel's md5 parse path rejects that with EINVAL (sin6_family != AF_INET6). The v4 path
    /// could not catch this: AF_INET = 2 everywhere. Platform-dependent for the v6 value
    /// (Darwin = 28), so asserted per-OS; the Linux branch is what CI proves.
    /// </summary>
    [Fact]
    public void WriteSockaddr_V6Peer_CarriesKernelAddressFamily()
    {
        // TCP-MD5 is only ever applied on Linux/macOS; other platforms have no defined layout.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        Span<byte> buffer = stackalloc byte[128];
        TcpMd5.WriteSockaddr(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 0), buffer);

        var expectedFamily = OperatingSystem.IsLinux() ? 10 : 28;
        Assert.Equal(expectedFamily, buffer[0]);
        // Linux keeps byte 1 zero (family is 2 bytes little-endian); Darwin's byte 1 is the family.
        Assert.Equal(OperatingSystem.IsLinux() ? 0 : 28, buffer[1]);

        TcpMd5.WriteSockaddr(new IPEndPoint(IPAddress.Loopback, 0), buffer);
        // v4 layout per OS: Linux = family in byte 0; Darwin = sin_len first, family in byte 1.
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(2, buffer[0]);
            Assert.Equal(0, buffer[1]);
        }
        else
        {
            Assert.Equal(16, buffer[0]); // sin_len
            Assert.Equal(2, buffer[1]);  // sin_family
        }
    }

    [Fact]
    public void WriteSockaddr_Darwin_CarriesSinLenFirst()
    {
        // BSD sockaddr_in/in6 start with a LENGTH byte; the family follows in byte 1. Asserted
        // only on Darwin, where the layout applies (the Linux expectations live above).
        if (!OperatingSystem.IsMacOS()) return;

        var buffer = new byte[128];
        TcpMd5.WriteSockaddr(new IPEndPoint(IPAddress.Loopback, 0), buffer);
        Assert.Equal(16, buffer[0]); // sin_len
        Assert.Equal(2, buffer[1]);  // sin_family

        TcpMd5.WriteSockaddr(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 0), buffer);
        Assert.Equal(28, buffer[0]); // sin6_len
        Assert.Equal(28, buffer[1]); // sin6_family
    }

    [Fact]
    public void InvalidKeyLength_Throws()
    {
        if (!OperatingSystem.IsLinux()) return; // struct shape asserted on the supported platform

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Assert.Throws<ArgumentException>(() => TcpMd5.Apply(socket, IPAddress.Loopback, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Linux_Apply_And_Clear_DontThrow_OnAListeningSocket()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.Listen(1);

        var ex = Record.Exception(() =>
        {
            TcpMd5.Apply(socket, IPAddress.Loopback, Key);
            TcpMd5.Clear(socket, IPAddress.Loopback);
        });
        Assert.Null(ex);
    }

    [Fact]
    public async Task Linux_Handshake_WithKey_Connects_WithoutKey_IsDropped()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(2);
        var serverEp = (IPEndPoint)listener.LocalEndPoint!;

        // Kernel-level enforcement: the listener drops unsigned segments from this peer.
        TcpMd5.Apply(listener, IPAddress.Loopback, Key);

        // Client WITH the key (attached for the server endpoint) completes the handshake.
        using var goodClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        TcpMd5.Apply(goodClient, serverEp, Key);
        var acceptTask = listener.AcceptAsync();
        await goodClient.ConnectAsync(serverEp);
        using var accepted = await acceptTask;
        Assert.True(accepted.Connected);
        Assert.True(goodClient.Connected);

        // Client WITHOUT the key: its SYN is dropped by the kernel — no connection within the window.
        using var badClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connectTask = badClient.ConnectAsync(serverEp);
        var timedOut = await Task.WhenAny(connectTask, Task.Delay(1500)) != connectTask;
        Assert.True(timedOut || !badClient.Connected, "an unsigned client must not complete the handshake");

        goodClient.Dispose();
        accepted.Dispose();
    }

    [Fact]
    public void IsSupported_IsLinuxOnly_MacOsMustNotClaimSupport()
    {
        // XNU never shipped TCP_MD5SIG: setsockopt(IPPROTO_TCP, 0x10) on Darwin hits
        // TCP_KEEPALIVE (netinet/tcp.h:223), not an MD5 signature — claiming macOS support
        // armed nothing while the control plane reported tcpMd5=true. Pinned per-OS so the
        // claim cannot silently return.
        if (OperatingSystem.IsMacOS())
            Assert.False(TcpMd5.IsSupported);
        else if (OperatingSystem.IsLinux())
            Assert.True(TcpMd5.IsSupported);
        // Windows and others: no TCP-MD5 anywhere; excluded on both sides of the branch above.
    }

    [Fact]
    public void ApplyAndClear_OnUnsupportedPlatform_ThrowInsteadOfMutatingSockets()
    {
        if (!OperatingSystem.IsMacOS()) return; // Linux arming is covered by the Linux_* tests

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.Listen(1);

        // Pre-fix these calls "succeeded": a 209-byte buffer either failed setsockopt with
        // EINVAL (swallowed by the caller) or, for a 1-2 byte key, silently overwrote the
        // listener's TCP keepalive idle time — never an MD5 key.
        Assert.Throws<PlatformNotSupportedException>(() => TcpMd5.Apply(socket, IPAddress.Loopback, Key));
        Assert.Throws<PlatformNotSupportedException>(() => TcpMd5.Clear(socket, IPAddress.Loopback));
    }

    [Fact]
    public void SetPeerMd5Key_FailsLoud_WhenPlatformCannotArmTheKey()
    {
        var server = new BgpServer(
            new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } },
            new RouteTable(),
            AllowAllFilter.Instance,
            new BgpMetrics(),
            new NoopSessionFactory(),
            NullLogger<BgpServer>.Instance);

        if (OperatingSystem.IsMacOS())
        {
            // Pre-fix: the key was stored and the listener never armed (ApplyMd5 swallowed the
            // failure into a Warning) — the API kept answering tcpMd5=true.
            Assert.Throws<PlatformNotSupportedException>(() => server.SetPeerMd5Key("192.0.2.10", "secret"));
        }
        else if (OperatingSystem.IsLinux())
        {
            // The supported platform keeps its contract: a valid password is accepted (the
            // listener is not started here, so there is nothing to arm yet — no throw).
            Assert.Null(Record.Exception(() => server.SetPeerMd5Key("192.0.2.10", "secret")));
        }
    }

    private sealed class NoopSessionFactory : IBgpSessionFactory
    {
        public BgpSession Create(IBgpConnection connection, PeerConfig peerConfig) => null!;
    }
}
