using Microsoft.Extensions.Logging;

namespace Motara.App.Collaboration;

internal static partial class CollaborationWorkspaceEvents
{
    [LoggerMessage(8050, LogLevel.Information,
        "Collaboration workspace initialized; contactCount={ContactCount}")]
    internal static partial void Initialized(ILogger logger, int contactCount);

    [LoggerMessage(8051, LogLevel.Information,
        "Collaboration contact operation completed; operation={Operation}; result={Result}")]
    internal static partial void OperationCompleted(ILogger logger, string operation, string result);

    [LoggerMessage(8052, LogLevel.Warning,
        "Collaboration workspace operation failed; operation={Operation}; errorType={ErrorType}")]
    internal static partial void OperationFailed(ILogger logger, string operation, string errorType);

    [LoggerMessage(8053, LogLevel.Information,
        "Pending collaboration session invitation expired; errorCode={ErrorCode}")]
    internal static partial void SessionInvitationExpired(ILogger logger, string errorCode);
}
