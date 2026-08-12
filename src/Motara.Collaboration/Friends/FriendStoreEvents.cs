using Microsoft.Extensions.Logging;

namespace Motara.Collaboration.Friends;

internal static partial class FriendStoreEvents
{
    [LoggerMessage(8010, LogLevel.Debug, "Collaboration friend records loaded; count={FriendCount}")]
    internal static partial void Loaded(ILogger logger, int friendCount);

    [LoggerMessage(8011, LogLevel.Information, "Collaboration friend record saved; trust={TrustState}")]
    internal static partial void Saved(ILogger logger, FriendTrustState trustState);

    [LoggerMessage(8012, LogLevel.Information, "Collaboration friend record removed")]
    internal static partial void Removed(ILogger logger);

    [LoggerMessage(8013, LogLevel.Warning, "Collaboration friend storage failed with {ErrorType}")]
    internal static partial void Failed(ILogger logger, string errorType);
}
