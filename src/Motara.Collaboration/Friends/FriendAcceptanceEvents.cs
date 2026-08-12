using Microsoft.Extensions.Logging;

namespace Motara.Collaboration.Friends;

internal static partial class FriendAcceptanceEvents
{
    [LoggerMessage(8030, LogLevel.Information,
        "Friend invitation acceptance completed; result={ResultCode}; elapsedMs={ElapsedMilliseconds}")]
    internal static partial void Completed(
        ILogger logger,
        FriendAcceptanceResultCode resultCode,
        long elapsedMilliseconds);
}
