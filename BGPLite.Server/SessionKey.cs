using System.Net;

namespace BGPLite.Server;

/// <summary>
/// Identifies a live BGP session by its accepted TCP connection: the remote peer's IP address
/// together with the remote TCP source port. Per RFC 4271 §8.2.1 / §8.2.1.2 there is one FSM —
/// one session — per TCP connection ("Each FSM corresponds to exactly one TCP connection"),
/// <em>not</em> per remote IP. Two distinct peers that arrive from the same source IP (e.g.
/// several routers behind one NAT/VPN) therefore use distinct ephemeral source ports and must
/// occupy two independent session slots; they may not replace one another.
/// </summary>
/// <remarks>
/// Keying only by the remote IP collapses both connections into one map slot and makes them
/// clobber each other (the second accept silently closes the first), so the peers flap and can
/// never stay Established — see issue #18. Note that per RFC 4271 §6.8 two genuinely distinct
/// peers (different BGP Identifier) sharing a source IP are <em>not</em> a connection collision;
/// the remote AS (RFC 4271 §4.2/§6.2) is a validation/policy field, not the transport identity,
/// and stays validated separately against <c>PeerConfig.RemoteAsn</c>.
/// </remarks>
internal readonly record struct SessionKey(IPAddress Address, int Port)
{
    /// <summary><c>"ip:port"</c> form used in logs so operators can tell concurrent peers behind
    /// one source IP apart.</summary>
    public override string ToString() => $"{Address}:{Port}";
}
