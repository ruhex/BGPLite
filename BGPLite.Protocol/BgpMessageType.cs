namespace BGPLite.Protocol;

public enum BgpMessageType : byte
{
    Open = 1,
    Update = 2,
    Notification = 3,
    Keepalive = 4,
    RouteRefresh = 5
}
