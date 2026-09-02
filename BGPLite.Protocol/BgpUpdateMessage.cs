namespace BGPLite.Protocol;

public sealed class BgpUpdateMessage : BgpMessage
{
    public override BgpMessageType Type => BgpMessageType.Update;
    public List<IpPrefix> WithdrawnRoutes { get; init; } = [];
    public List<PathAttribute> PathAttributes { get; init; } = [];
    public List<IpPrefix> Nlri { get; init; } = [];

    /// <summary>Decoded MP_REACH_NLRI (RFC 4760, AFI=2/SAFI=1) IPv6 announcements — extracted from
    /// the attribute list by the reader (#15 phase 2). Null when the UPDATE carries none.</summary>
    public MpReachCodec.MpReachV6? MpReachV6 { get; init; }

    /// <summary>Decoded MP_UNREACH_NLRI (RFC 4760, AFI=2/SAFI=1) IPv6 withdrawals. Null/empty when
    /// the UPDATE carries none.</summary>
    public IReadOnlyList<IpPrefix>? MpUnreachV6 { get; init; }
}
