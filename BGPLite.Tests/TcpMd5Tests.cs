using System.Net;
using System.Net.Sockets;
using BGPLite.Contracts;

namespace BGPLite.Tests;

/// <summary>
/// TCP-MD5 (RFC 2385) per-peer socket plumbing (#36). The smoke test runs everywhere Linux runs
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
}
