using Microsoft.Extensions.Logging;

namespace Motara.Collaboration.Migration;

internal static partial class CollaborationIdentityArchiveEvents
{
    [LoggerMessage(8070, LogLevel.Information,
        "Collaboration identity archive exported; friendCount={FriendCount}; secretCount={SecretCount}; consumedInviteCount={ConsumedInviteCount}; elapsedMilliseconds={ElapsedMilliseconds}")]
    internal static partial void Exported(
        ILogger logger,
        int friendCount,
        int secretCount,
        int consumedInviteCount,
        long elapsedMilliseconds);

    [LoggerMessage(8071, LogLevel.Information,
        "Collaboration identity archive imported; friendCount={FriendCount}; secretCount={SecretCount}; consumedInviteCount={ConsumedInviteCount}; elapsedMilliseconds={ElapsedMilliseconds}")]
    internal static partial void Imported(
        ILogger logger,
        int friendCount,
        int secretCount,
        int consumedInviteCount,
        long elapsedMilliseconds);

    [LoggerMessage(8072, LogLevel.Warning,
        "Collaboration identity archive operation failed; operation={Operation}; errorType={ErrorType}")]
    internal static partial void Failed(ILogger logger, string operation, string errorType);

    [LoggerMessage(8073, LogLevel.Debug,
        "Collaboration identity archive inspected; friendCount={FriendCount}; secretCount={SecretCount}; consumedInviteCount={ConsumedInviteCount}")]
    internal static partial void Inspected(
        ILogger logger,
        int friendCount,
        int secretCount,
        int consumedInviteCount);
}
