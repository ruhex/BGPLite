using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using BGPLite.Server;
using Xunit;

namespace BGPLite.Tests;

/// <summary>
/// Regression coverage for issue #18: the live-session map must be keyed by the accepted TCP
/// connection (remote IP + remote source port), so several distinct peers that arrive from the
/// same source IP (behind a NAT/VPN) coexist instead of clobbering each other. Per RFC 4271
/// §8.2.1 there is one session per TCP connection; per §6.8 two peers with different BGP
/// Identifiers sharing a source IP are <em>not</em> a collision and must both be retained.
/// </summary>
public class SessionKeyingTests
{
    private static readonly IPAddress SharedIp = IPAddress.Parse("203.0.113.10");

    [Fact]
    public void SessionKey_Distinguishes_SameIp_DifferentSourcePort()
    {
        var a = new SessionKey(SharedIp, 50001);
        var b = new SessionKey(SharedIp, 50002);

        Assert.Equal(a, new SessionKey(SharedIp, 50001));
        Assert.True(a == new SessionKey(SharedIp, 50001));
        Assert.NotEqual(a, b);
        Assert.True(a != b);
        Assert.Equal("203.0.113.10:50001", a.ToString());
    }

    /// <summary>
    /// The core regression: two distinct peers from one source IP register under different
    /// (IP, port) keys and BOTH remain — the second no longer replaces the first (which was the
    /// IP-only-key bug: both would hit the same slot and the second would overwrite the first).
    /// </summary>
    [Fact]
    public void SameSourceIp_DifferentPorts_Coexist_In_SessionMap()
    {
        var dict = new ConcurrentDictionary<SessionKey, string>();
        var peerA = new SessionKey(SharedIp, 50001); // e.g. AS 64512
        var peerB = new SessionKey(SharedIp, 50002); // e.g. AS 64513, same source IP

        Assert.True(dict.TryAdd(peerA, "A"));
        Assert.True(dict.TryAdd(peerB, "B"));

        Assert.Equal(2, dict.Count);
        Assert.True(dict.ContainsKey(peerA));
        Assert.True(dict.ContainsKey(peerB));
    }

    /// <summary>
    /// Mirrors <c>BgpServer.RemoveSessionIfOwner</c> (the atomic
    /// <c>ICollection{KeyValuePair}.Remove</c> compare-and-remove): tearing down the session for
    /// one source port must not erase the session registered under a different source port, even
    /// though both share the source IP.
    /// </summary>
    [Fact]
    public void RemoveOwner_Does_Not_Erase_DifferentPort_Session()
    {
        var dict = new ConcurrentDictionary<SessionKey, string>();
        var peerA = new SessionKey(SharedIp, 50001);
        var peerB = new SessionKey(SharedIp, 50002);
        dict.TryAdd(peerA, "A");
        dict.TryAdd(peerB, "B");

        // peerA's finally removes only its own (key, value) pair.
        var removed = ((ICollection<KeyValuePair<SessionKey, string>>)dict)
            .Remove(new KeyValuePair<SessionKey, string>(peerA, "A"));

        Assert.True(removed, "peerA's own session must be removable");
        Assert.False(dict.ContainsKey(peerA));
        Assert.True(dict.ContainsKey(peerB), "peerB (different source port) must be untouched");
        Assert.Equal("B", dict[peerB]);
    }
}
