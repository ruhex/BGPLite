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

    [YamlMember(Alias = "KeepAlive")]
    public int KeepAlive { get; init; } = 60;

    [YamlMember(Alias = "HoldTime")]
    public int HoldTime { get; init; } = 180;

    /// <summary>Advertise Graceful Restart (RFC 4724) and send an End-of-RIB marker after the
    /// initial route dump, so GR-capable peers retain our routes across our restart.</summary>
    [YamlMember(Alias = "GracefulRestart")]
    public bool GracefulRestart { get; init; } = true;

    /// <summary>Restart Time (seconds) advertised in the GR capability. Clamped to
    /// min(HoldTime, 4095) — the RFC 4724 §2.2 field is 12 bits.</summary>
    [YamlMember(Alias = "RestartTime")]
    public int RestartTime { get; init; } = 120;

    /// <summary>Forwarding State (F) bit for IPv4/Unicast. When true, GR-capable peers keep our
    /// stale routes through the restart window (smoothest — prefixes never visibly disappear).
    /// Only keep true if the deployment preserves forwarding at the advertised next-hop (the
    /// router-id); otherwise a peer could forward to a non-forwarding speaker during the window.
    /// See RFC 4724 §3/§4.2.</summary>
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
    }
}
