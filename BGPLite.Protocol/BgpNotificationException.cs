namespace BGPLite.Protocol;

/// <summary>
/// Thrown when a BGP protocol error is detected that requires sending a NOTIFICATION
/// (Error/SubError) to the peer before tearing down the session. Carries the exact codes
/// (per RFC 4271 §6) so the session handler sends the right ones instead of a generic
/// Message Header Error.
/// </summary>
public sealed class BgpNotificationException(byte errorCode, byte subErrorCode, string message, byte[]? notificationData = null) : Exception(message)
{
    private readonly byte[]? _notificationData = notificationData is null ? null : (byte[])notificationData.Clone();

    public byte ErrorCode { get; } = errorCode;
    public byte SubErrorCode { get; } = subErrorCode;
    public byte[]? NotificationData => _notificationData is null ? null : (byte[])_notificationData.Clone();
}
