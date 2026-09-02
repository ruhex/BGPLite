using System.Net;
using System.Net.Sockets;
using BGPLite.Contracts;
using BGPLite.Server;

namespace BGPLite.Tests;

/// <summary>
/// #14 phase 4: the dual-mode listener surfaces IPv4 peers as IPv4-mapped IPv6 addresses —
/// these cover the address-form conversions that keep session identity, the PeerStore lookup
/// and TCP-MD5 keys working across both transport families.
/// </summary>
public class BgpServerAcceptTests
{
    private static readonly byte[] Key = "test-md5-key"u8.ToArray();

    // ---- NormalizeAcceptedAddress: identity/lookup form ----

    [Fact]
    public void Normalize_MappedIpv4_BecomesPlainIpv4()
    {
        var mapped = IPAddress.Parse("::ffff:10.0.0.1");
        Assert.Equal(IPAddress.Parse("10.0.0.1"), BgpServer.NormalizeAcceptedAddress(mapped));
    }

    [Fact]
    public void Normalize_PlainIpv4_Unchanged()
    {
        Assert.Equal(IPAddress.Parse("192.168.1.2"), BgpServer.NormalizeAcceptedAddress(IPAddress.Parse("192.168.1.2")));
    }

    [Fact]
    public void Normalize_Ipv6_Unchanged()
    {
        Assert.Equal(IPAddress.Parse("2001:db8::1"), BgpServer.NormalizeAcceptedAddress(IPAddress.Parse("2001:db8::1")));
    }

    /// <summary>The premise the normalization exists for: an IPv4 client connecting to a
    /// dual-mode listener really does arrive as an IPv4-mapped endpoint, so without the
    /// conversion the PeerStore key would read "::ffff:127.0.0.1" where the config says
    /// "127.0.0.1". Drives a real dual-mode socket pair on a loopback high port.</summary>
    [Fact]
    public void Normalize_DualModeListener_Ipv4ClientArrivesMapped()
    {
        if (!Socket.OSSupportsIPv6) return; // same idiom as TcpMd5Tests' platform guard

        using var listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        listener.DualMode = true;
        // Wildcard bind (as BgpServer.StartAsync does) — a bind to a specific v6 address
        // (::1) does not match IPv4 traffic even in dual mode.
        listener.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        listener.Listen(1);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndPoint!).Port));
        var accepted = listener.Accept();
        try
        {
            var remote = ((IPEndPoint)accepted.RemoteEndPoint!).Address;
            Assert.True(remote.IsIPv4MappedToIPv6, "expected an IPv4-mapped remote address on the dual-mode listener");
            Assert.Equal(IPAddress.Loopback, BgpServer.NormalizeAcceptedAddress(remote));
        }
        finally
        {
            accepted.Dispose();
        }
    }

    // ---- Md5WireAddress: the form a TCP-MD5 key is stored under ----

    [Fact]
    public void Md5WireAddress_V6Socket_Ipv4Peer_MapsToV6()
    {
        // Linux tcp_v6_parse_md5_keys rejects AF_INET on a v6 socket but accepts the
        // IPv4-mapped form (prefixlen 32) — the key must be stored mapped.
        var wire = BgpServer.Md5WireAddress(AddressFamily.InterNetworkV6, IPAddress.Parse("10.0.0.1"));
        Assert.Equal(IPAddress.Parse("::ffff:10.0.0.1"), wire);
    }

    [Fact]
    public void Md5WireAddress_V6Socket_Ipv6Peer_Unchanged()
    {
        var peer = IPAddress.Parse("2001:db8::1");
        Assert.Equal(peer, BgpServer.Md5WireAddress(AddressFamily.InterNetworkV6, peer));
    }

    [Fact]
    public void Md5WireAddress_V6Socket_AlreadyMappedPeer_Unchanged()
    {
        // The accept path passes the RAW remote address (already mapped for IPv4 peers);
        // re-mapping must not double-map or alter it.
        var peer = IPAddress.Parse("::ffff:10.0.0.1");
        Assert.Equal(peer, BgpServer.Md5WireAddress(AddressFamily.InterNetworkV6, peer));
    }

    [Fact]
    public void Md5WireAddress_V4Socket_Ipv4Peer_Unchanged()
    {
        var peer = IPAddress.Parse("10.0.0.1");
        Assert.Equal(peer, BgpServer.Md5WireAddress(AddressFamily.InterNetwork, peer));
    }

    /// <summary>
    /// Kernel-level proof of the dual-mode MD5 path (Linux loopback, same guarantees as
    /// TcpMd5Tests' v4 handshake test): a key stored under the mapped form
    /// (<see cref="BgpServer.Md5WireAddress"/>) on a dual-mode listener is enforced — an IPv4
    /// client WITH the key completes the handshake, WITHOUT it the kernel drops the SYN.
    /// Proves the AF_INET6+::ffff:a.b.c.d sockaddr actually matches a v4-mapped connection,
    /// which no pure-function test can show.
    /// </summary>
    [Fact]
    public async Task Linux_DualMode_Handshake_MappedKey_Connects_WithoutKey_IsDropped()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        listener.DualMode = true;
        listener.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        listener.Listen(2);
        var serverPort = ((IPEndPoint)listener.LocalEndPoint!).Port;

        // The key is stored under EXACTLY the form production code derives (Md5WireAddress).
        var wirePeer = BgpServer.Md5WireAddress(listener.AddressFamily, IPAddress.Loopback);
        Assert.Equal(IPAddress.Parse("::ffff:127.0.0.1"), wirePeer);
        TcpMd5.Apply(listener, wirePeer, Key);

        // IPv4 client WITH the key (attached for the server's v4 endpoint) completes the handshake.
        var clientEp = new IPEndPoint(IPAddress.Loopback, serverPort);
        using var goodClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        TcpMd5.Apply(goodClient, clientEp, Key);
        var acceptTask = listener.AcceptAsync();
        await goodClient.ConnectAsync(clientEp);
        using var accepted = await acceptTask;
        Assert.True(accepted.Connected);
        Assert.True(goodClient.Connected);

        // IPv4 client WITHOUT the key: its SYN is dropped by the kernel — no connection.
        using var badClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connectTask = badClient.ConnectAsync(clientEp);
        var timedOut = await Task.WhenAny(connectTask, Task.Delay(1500)) != connectTask;
        Assert.True(timedOut || !badClient.Connected, "an unsigned client must not complete the handshake");

        goodClient.Dispose();
        accepted.Dispose();
    }
}
