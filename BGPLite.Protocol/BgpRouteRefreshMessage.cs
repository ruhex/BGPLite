namespace BGPLite.Protocol;

public sealed class BgpRouteRefreshMessage : BgpMessage
{
    public ushort Afi { get; init; }
    public byte Reserved { get; init; }
    public byte Safi { get; init; }

    public override BgpMessageType Type => BgpMessageType.RouteRefresh;
}
