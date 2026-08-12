using Microsoft.Extensions.Logging;

namespace Motara.Collaboration.Handshake;

internal static partial class FriendshipHandshakeEvents
{
    [LoggerMessage(8050, LogLevel.Information,
        "Friendship handshake operation completed; operation={Operation}; result={ResultCode}; elapsedMs={ElapsedMilliseconds}")]
    internal static partial void Completed(
        ILogger logger,
        string operation,
        FriendshipHandshakeResultCode resultCode,
        long elapsedMilliseconds);

    [LoggerMessage(8051, LogLevel.Debug,
        "Friendship handshake offer created; elapsedMs={ElapsedMilliseconds}")]
    internal static partial void OfferCreated(ILogger logger, long elapsedMilliseconds);
}
