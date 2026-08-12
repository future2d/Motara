using Microsoft.Extensions.Logging;

namespace Motara.Collaboration.Profile;

internal static partial class LocalCollaborationProfileEvents
{
    [LoggerMessage(8090, LogLevel.Information,
        "Local collaboration profile loaded; exists={Exists}")]
    internal static partial void Loaded(ILogger logger, bool exists);

    [LoggerMessage(8091, LogLevel.Information,
        "Local collaboration profile saved; displayNameLength={DisplayNameLength}")]
    internal static partial void Saved(ILogger logger, int displayNameLength);

    [LoggerMessage(8092, LogLevel.Warning,
        "Local collaboration profile operation failed; operation={Operation}; errorType={ErrorType}")]
    internal static partial void Failed(ILogger logger, string operation, string errorType);
}
