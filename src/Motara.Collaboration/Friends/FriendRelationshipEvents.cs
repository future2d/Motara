using Microsoft.Extensions.Logging;

namespace Motara.Collaboration.Friends;

internal static partial class FriendRelationshipEvents
{
    [LoggerMessage(8060, LogLevel.Information,
        "Friend relationship removal completed; result={ResultCode}; elapsedMs={ElapsedMilliseconds}")]
    internal static partial void Completed(
        ILogger logger,
        FriendRelationshipRemovalResultCode resultCode,
        long elapsedMilliseconds);
}
