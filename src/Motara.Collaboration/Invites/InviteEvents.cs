using Microsoft.Extensions.Logging;

namespace Motara.Collaboration.Invites;

internal static partial class InviteEvents
{
    [LoggerMessage(8020, LogLevel.Information, "Friend invitation created")]
    internal static partial void Created(ILogger logger);

    [LoggerMessage(8021, LogLevel.Information, "Friend invitation validated")]
    internal static partial void Validated(ILogger logger);

    [LoggerMessage(8022, LogLevel.Debug, "Friend invitation validation rejected with {ErrorCode}")]
    internal static partial void Rejected(ILogger logger, InviteErrorCode errorCode);

    [LoggerMessage(8023, LogLevel.Information, "Invitation nonce consumed")]
    internal static partial void Consumed(ILogger logger);

    [LoggerMessage(8024, LogLevel.Debug, "Invitation nonce was already consumed")]
    internal static partial void Duplicate(ILogger logger);

    [LoggerMessage(8025, LogLevel.Information, "Session invitation created with {JoinPolicy}")]
    internal static partial void SessionCreated(ILogger logger, SessionJoinPolicy joinPolicy);

    [LoggerMessage(8026, LogLevel.Information, "Session invitation validated with {JoinPolicy}")]
    internal static partial void SessionValidated(ILogger logger, SessionJoinPolicy joinPolicy);

    [LoggerMessage(8027, LogLevel.Debug, "Session invitation validation rejected with {ErrorCode}")]
    internal static partial void SessionRejected(ILogger logger, SessionInviteErrorCode errorCode);
}
