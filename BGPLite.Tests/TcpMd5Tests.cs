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
/// Platform pins: <c>IsSupported</c> is Linux-only and every non-Linux arm/clear throws —
/// XNU never shipped <c>TCP_MD5SIG</c> (option 0x10 there is <c>TCP_KEEPALIVE</c>), so macOS
/// must not claim support or mutate keepalive state while the API reports tcpMd5=true.
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
    /// could not catch this: AF_INET = 2 everywhere. Linux-only now: with TCP-MD5 armed only
    /// there, no other platform has an MD5 sockaddr layout to assert.
    /// </summary>
    [Fact]
    public void WriteSockaddr_V6Peer_CarriesKernelAddressFamily()
    {
        if (!OperatingSystem.IsLinux()) return;

        Span<byte> buffer = stackalloc byte[128];
        TcpMd5.WriteSockaddr(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 0), buffer);

        Assert.Equal(10, buffer[0]);   // AF_INET6 (kernel value — NOT 23)
        Assert.Equal(0, buffer[1]);    // Linux keeps byte 1 zero (family is 2 bytes little-endian)

        TcpMd5.WriteSockaddr(new IPEndPoint(IPAddress.Loopback, 0), buffer);
        Assert.Equal(2, buffer[0]);    // AF_INET
        Assert.Equal(0, buffer[1]);
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

        // No listener has applied the key yet, so even Linux must not claim kernel enforcement.
        Assert.False(server.IsPeerMd5Armed("192.0.2.10", "secret"));
        Assert.False(server.IsPeerMd5Armed("192.0.2.99", "secret"));   // never configured
        Assert.False(server.IsPeerMd5Armed("not-an-ip", "secret"));    // unparseable, not a crash
    }

    [Fact]
    public async Task PrestartKey_IsArmedDuringStart_BeforeAccepting()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var server = NewServer();
        server.SetPeerMd5Key("192.0.2.10", "secret");
        Assert.False(server.IsPeerMd5Armed("192.0.2.10", "secret"));

        await server.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(server.IsPeerMd5Armed("192.0.2.10", "secret"));
            Assert.False(server.IsPeerMd5Armed("192.0.2.10", "another"));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
        Assert.False(server.IsPeerMd5Armed("192.0.2.10", "secret"));
    }

    [Fact]
    public async Task FailedReplacementOrClear_PreservesPreviousKey()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var server = NewServer();
        await server.StartAsync(CancellationToken.None);
        var fail = false;
        server.Md5ApplyProbe = (_, _, _) => !fail;

        try
        {
            server.SetPeerMd5Key("192.0.2.10", "old");
            fail = true;
            Assert.Throws<InvalidOperationException>(() => server.SetPeerMd5Key("192.0.2.10", "new"));
            Assert.True(server.IsPeerMd5Armed("192.0.2.10", "old"));
            Assert.False(server.IsPeerMd5Armed("192.0.2.10", "new"));

            Assert.Throws<InvalidOperationException>(() => server.SetPeerMd5Key("192.0.2.10", null));
            Assert.True(server.IsPeerMd5Armed("192.0.2.10", "old"));
        }
        finally
        {
            fail = false;
            server.SetPeerMd5Key("192.0.2.10", null);
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Linux_ClearAlreadyMissingKey_TreatsEnoentAsSuccess()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var server = NewServer();
        await server.StartAsync(CancellationToken.None);
        server.Md5ApplyProbe = (_, _, key) => key is not null
            ? true
            : throw new SocketException(2);

        try
        {
            server.SetPeerMd5Key("192.0.2.10", "old");
            server.SetPeerMd5Key("192.0.2.10", null);

            Assert.False(server.IsPeerMd5Armed("192.0.2.10", "old"));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void ClearWithoutConfiguredKey_DoesNotTouchKernel()
    {
        using var server = NewServer();
        var calls = 0;
        server.Md5ApplyProbe = (_, _, _) => { calls++; return true; };

        server.SetPeerMd5Key("192.0.2.10", null);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task IsPeerMd5Armed_DoesNotObserveProvisionalReplacement()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var server = NewServer();
        await server.StartAsync(CancellationToken.None);
        server.SetPeerMd5Key("192.0.2.10", "old");
        var applying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Md5ApplyProbe = (_, _, key) =>
        {
            if (key is not null && key.Length == 3 && key[0] == (byte)'n')
            {
                applying.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
            return true;
        };

        try
        {
            var mutation = Task.Run(() => server.SetPeerMd5Key("192.0.2.10", "new"));
            await applying.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var read = Task.Run(() => server.IsPeerMd5Armed("192.0.2.10", "new"));
            await Task.Delay(100);
            Assert.False(read.IsCompletedSuccessfully && read.Result,
                "the provisional key must not be reported as armed before kernel success");
            release.TrySetResult();
            await mutation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await read.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.TrySetResult();
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ConcurrentKeyMutations_AreSerialized()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var server = NewServer();
        await server.StartAsync(CancellationToken.None);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var active = 0;
        var maxActive = 0;
        server.Md5ApplyProbe = (_, _, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            var current = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, current);
            if (call == 1)
            {
                firstEntered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
            if (call == 2)
                secondEntered.TrySetResult();
            Interlocked.Decrement(ref active);
            return true;
        };

        Task first = null!;
        Task second = null!;
        try
        {
            first = Task.Run(() => server.SetPeerMd5Key("192.0.2.10", "first"));
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = Task.Run(() => server.SetPeerMd5Key("192.0.2.10", "second"));
            var early = await Task.WhenAny(secondEntered.Task, Task.Delay(100));
            Assert.NotSame(secondEntered.Task, early);
            release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, maxActive);
        }
        finally
        {
            release.TrySetResult();
            if (first is not null && second is not null)
                await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static BgpServer NewServer()
    {
        var server = new BgpServer(
            new AppConfig { Bgp = new BgpConfig { Asn = 65001, RouterId = "127.0.0.1" } },
            new RouteTable(), AllowAllFilter.Instance, new BgpMetrics(),
            new NoopSessionFactory(), NullLogger<BgpServer>.Instance);
        server.ListenPort = 0;
        return server;
    }

    private sealed class NoopSessionFactory : IBgpSessionFactory
    {
        public BgpSession Create(IBgpConnection connection, PeerConfig peerConfig) => null!;
    }
}
