using Microsoft.Extensions.Logging;

namespace Motara.Collaboration.Presence;

internal static partial class PresenceRefreshEvents
{
    [LoggerMessage(8160, LogLevel.Debug, "Presence record published")]
    public static partial void Published(ILogger logger);

    [LoggerMessage(8161, LogLevel.Debug, "Presence record publish retry {Attempt} after {ErrorType}")]
    public static partial void Retrying(ILogger logger, int attempt, string errorType);

    [LoggerMessage(8162, LogLevel.Warning, "Presence record unavailable after retry exhaustion with {ErrorType}")]
    public static partial void Unavailable(ILogger logger, string errorType);

    [LoggerMessage(8163, LogLevel.Warning, "Presence refresh shutdown exceeded its bounded wait")]
    public static partial void ShutdownTimedOut(ILogger logger);

    [LoggerMessage(8164, LogLevel.Information, "Presence refresh stopped")]
    public static partial void Stopped(ILogger logger);
}
