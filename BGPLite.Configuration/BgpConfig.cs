using System.Net;
using System.Net.Sockets;
using YamlDotNet.Serialization;

namespace BGPLite.Configuration;

public sealed class BgpConfig
{
    [YamlMember(Alias = "Asn")]
    public uint Asn { get; init; }

    [YamlMember(Alias = "RouterId")]
    public string RouterId { get; init; } = "0.0.0.0";

    /// <summary>
    /// Optional global IPv6 next hop advertised to MP-BGP IPv6/Unicast peers (#14 phase 4,
    /// RFC 2545 §3: the MP_REACH next hop is the speaker's GLOBAL address — the IPv4 router-id
    /// cannot serve the IPv6 address family). Unset = IPv6 routes are never advertised to any
    /// peer (suppressed with a warning per send); set = each MP-IPv6-negotiated session
    /// announces its IPv6 routes with this next hop.
    /// </summary>
    [YamlMember(Alias = "NextHopIpv6")]
    public string? NextHopIpv6 { get; init; }

    /// <summary>
    /// Whether <paramref name="value"/> parses as a GLOBAL IPv6 unicast address (2000::/3) —
    /// the RFC 2545 §3 requirement for the MP_REACH next hop. Shared by <see cref="Validate"/>
    /// and the session's send-time gate (a hand-built config record skips Validate, so the gate
    /// must not trust the string blindly).
    /// </summary>
    public static bool IsGlobalUnicastV6([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value) =>
        IPAddress.TryParse(value, out var v6)
        && v6.AddressFamily == AddressFamily.InterNetworkV6
        && !v6.IsIPv4MappedToIPv6
        && v6.ScopeId == 0
        && (v6.GetAddressBytes()[0] & 0xE0) == 0x20; // 2000::/3

    [YamlMember(Alias = "KeepAlive")]
    public int KeepAlive { get; init; } = 60;

    [YamlMember(Alias = "HoldTime")]
    public int HoldTime { get; init; } = 180;

    /// <summary>Sending-side Graceful Restart conveniences (RFC 4724): an End-of-RIB marker after
    /// the initial route dump, and a silent TCP close (no NOTIFICATION) on server shutdown. The GR
    /// capability itself is NOT advertised (#318, D6): the receiving-speaker half of RFC 4724 §4.2
    /// (retaining and stale-marking a restarting peer's routes) is not implemented, and advertising
    /// the &lt;AFI, SAFI, F&gt; tuple promised behavior the code does not have.</summary>
    [YamlMember(Alias = "GracefulRestart")]
    public bool GracefulRestart { get; init; } = true;

    /// <summary>Restart Time intended for the GR capability's 12-bit field. Currently unused —
    /// the capability is not advertised while the receiving-speaker half of RFC 4724 is
    /// unimplemented (#318, D6). Accepted for config compatibility.</summary>
    [YamlMember(Alias = "RestartTime")]
    public int RestartTime { get; init; } = 120;

    /// <summary>Forwarding State (F) bit for IPv4/Unicast, intended for the GR capability.
    /// Currently unused — the capability is not advertised while the receiving-speaker half of
    /// RFC 4724 is unimplemented (#318, D6). Accepted for config compatibility.</summary>
    [YamlMember(Alias = "GracefulRestartForwardingState")]
    public bool GracefulRestartForwardingState { get; init; } = true;

    /// <summary>
    /// Connect-to-OPEN timeout in seconds (#115, Slowloris defense). Bounds how long a freshly
    /// accepted TCP connection may wait for the peer's OPEN before being dropped. The negotiated
    /// hold timer only starts AFTER the handshake, so without this bound a connection that opens
    /// TCP but never sends OPEN pins a BgpSession + task + socket FD until the OS TCP timeout
    /// (minutes). 30s comfortably exceeds a legitimate peer's OPEN latency. 0 = disabled (legacy
    /// behavior). Peers that complete OPEN within the window are unaffected.
    /// </summary>
    [YamlMember(Alias = "OpenTimeoutSeconds")]
    public int OpenTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Per-source-IP accept throttle for the BGP listener (#115): the maximum number of inbound TCP
    /// connects accepted from a single remote IP within any rolling 60s window. An IP exceeding the
    /// limit has its just-accepted socket closed immediately WITHOUT spawning a session — no
    /// FD/task/session pinned — defending one-IP accept floods. This deliberately does NOT cap the
    /// count of legitimate established sessions (a route server is designed to hold many peers —
    /// that is capacity/business logic, not a security control); it only bounds incomplete-handshake
    /// floods. 0 = disabled (legacy behavior). Default 60/min is generous for legitimate peers (one
    /// connect per session) while still throttling a flood.
    /// </summary>
    [YamlMember(Alias = "MaxAcceptsPerIpPerMinute")]
    public int MaxAcceptsPerIpPerMinute { get; init; } = 60;

    /// <summary>#304: per-peer ceiling on prefixes installed from one session (distinct
    /// NLRI this session currently owns); exceeding it tears the session down with
    /// NOTIFICATION(Cease, MaxPrefixesExceeded) per RFC 4271 §6.7 / RFC 4486 §2.
    /// #481: the shipped default is bounded (1,000,000 — above any legitimate provisioning
    /// peer, far below memory exhaustion) so the RFC 4486 defense is on out of the box;
    /// 0 = unlimited remains available as an explicit opt-out (the convention of
    /// OpenTimeoutSeconds / MaxAcceptsPerIpPerMinute).</summary>
    [YamlMember(Alias = "MaxPrefixesPerPeer")]
    public int MaxPrefixesPerPeer { get; init; } = 1_000_000;

    public IPAddress GetRouterIdAddress() => IPAddress.Parse(RouterId);

    /// <summary>
    /// Validates the BGP settings, throwing <see cref="InvalidOperationException"/> with a clear
    /// message on the first violation. Called from <see cref="AppConfig.Validate"/> at startup so
    /// invalid YAML fails loud before the host is built (rather than surfacing later as a peer
    /// OPEN rejection / wrong-port bind). Rules follow RFC 4271 §4.2/§6.8.
    /// </summary>
    public void Validate()
    {
        if (Asn == 0)
            throw new InvalidOperationException(
                $"Invalid configuration: Bgp.Asn must be greater than 0 (got 0).");

        // RFC 4271 §6.8: the BGP Identifier (RouterId) must be a non-zero IPv4 address. The peer-side
        // OPEN validator already rejects 0.0.0.0; this catches the local side before it is advertised.
        var routerIdValid = IPAddress.TryParse(RouterId, out var routerIdAddress)
            && routerIdAddress.AddressFamily == AddressFamily.InterNetwork
            && !routerIdAddress.Equals(IPAddress.Any);
        if (!routerIdValid)
            throw new InvalidOperationException(
                $"Invalid configuration: Bgp.RouterId must be a non-zero IPv4 address (got '{RouterId}').");

        // RFC 4271 §4.2: a Hold Time of 0 disables KeepAlive processing; any other value must be >= 3s.
        if (HoldTime != 0 && HoldTime < 3)
            throw new InvalidOperationException(
                $"Invalid configuration: Bgp.HoldTime must be 0 (disabled) or at least 3 seconds (got {HoldTime}).");

        // RFC 4271 §4.2: Hold Time is a 2-octet field — a value above 65535 cannot be carried in
        // an OPEN; the wire write silently truncated it before ((ushort)70000 -> 4464) (#265 item 2).
        if (HoldTime > ushort.MaxValue)
            throw new InvalidOperationException(
                $"Invalid configuration: Bgp.HoldTime must fit the 2-octet OPEN field (0..65535, got {HoldTime}).");

        // KeepAlive is only meaningful when a Hold Time is negotiated. The session computes its
        // keepalive interval as max(HoldTime/3, 1) (BgpSession OPEN negotiation), so the configured
        // value must fit within the same window: 1..max(HoldTime/3, 1).
        if (HoldTime > 0)
        {
            var maxKeepAlive = Math.Max(HoldTime / 3, 1);
            if (KeepAlive < 1 || KeepAlive > maxKeepAlive)
                throw new InvalidOperationException(
                    $"Invalid configuration: Bgp.KeepAlive must be between 1 and {maxKeepAlive} seconds " +
                    $"for HoldTime={HoldTime} (got {KeepAlive}).");
        }

        // Listener hardening (#115): the connect-to-OPEN timeout and per-source-IP accept throttle
        // are non-negative integers; 0 disables each (legacy behavior). Reject negatives at startup
        // rather than letting them surprise the operator (negative → treated as disabled silently).
        if (OpenTimeoutSeconds < 0)
            throw new InvalidOperationException(
                $"Invalid configuration: Bgp.OpenTimeoutSeconds must be >= 0 (got {OpenTimeoutSeconds}).");

        if (MaxAcceptsPerIpPerMinute < 0)
            throw new InvalidOperationException(
                $"Invalid configuration: Bgp.MaxAcceptsPerIpPerMinute must be >= 0 (got {MaxAcceptsPerIpPerMinute}).");

        if (MaxPrefixesPerPeer < 0)
            throw new InvalidOperationException(
                $"Invalid configuration: Bgp.MaxPrefixesPerPeer must be >= 0, 0 = unlimited (got {MaxPrefixesPerPeer}).");

        // #14 phase 4: when an IPv6 next hop is configured it must be a GLOBAL IPv6 unicast
        // address — RFC 2545 §3 requires the (first) MP_REACH next hop to be global, and the
        // 16-byte form we advertise has no room for interface semantics (a link-local address
        // is only meaningful on a shared link, which a route-server session is not required to
        // be). Global unicast space is 2000::/3 — that single check also rejects the unspecified
        // address, loopback (::1), link-local (fe80::/10), ULA (fc00::/7) and multicast (ff00::/8).
        // Unset stays legal: it simply disables IPv6 advertisements (fail-visible at send time),
        // never a startup failure for IPv4-only deployments.
        if (!string.IsNullOrWhiteSpace(NextHopIpv6))
        {
            if (!IsGlobalUnicastV6(NextHopIpv6))
                throw new InvalidOperationException(
                    "Invalid configuration: Bgp.NextHopIpv6 must be a global unicast IPv6 address (2000::/3 — " +
                    $"not link-local, multicast, ULA or mapped) when set (got '{NextHopIpv6}'). " +
                    "Leave it unset to disable IPv6 advertisements.");
        }
    }
}
