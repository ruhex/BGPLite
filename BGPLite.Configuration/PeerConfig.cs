using System.Net;
using YamlDotNet.Serialization;

namespace BGPLite.Configuration;

public sealed class PeerConfig
{
    // #390: empty default (was "0.0.0.0") — an omitted Address trips the fail-loud validation
    // instead of silently configuring a peer at the invalid all-zeros address.
    [YamlMember(Alias = "Address")]
    public string Address { get; init; } = "";

    [YamlMember(Alias = "RemoteAsn")]
    public uint? RemoteAsn { get; init; }

    [YamlMember(Alias = "Description")]
    public string? Description { get; init; }

    /// <summary>Remote TCP source port of the accepted connection. Runtime-only — NOT loaded from
    /// YAML (configured peers are matched by address when they connect). Combined with
    /// <see cref="Address"/> in <see cref="ToString"/> so session logs can tell apart the several
    /// peers that may share one source IP behind a NAT/VPN (issue #18).</summary>
    public int Port { get; init; }

    public IPAddress GetAddress() => IPAddress.Parse(Address);

    /// <summary><c>"address"</c>, or <c>"address:port"</c> when <see cref="Port"/> is set — used as
    /// the peer label in session logs. Callers that need the bare IP (peer-store lookups) use
    /// <see cref="Address"/> directly.</summary>
    public override string ToString() => Port > 0 ? $"{Address}:{Port}" : Address;
}
