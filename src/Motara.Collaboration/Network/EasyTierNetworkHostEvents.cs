using Microsoft.Extensions.Logging;

namespace Motara.Collaboration.Network;

internal static partial class EasyTierNetworkHostEvents
{
    [LoggerMessage(8150, LogLevel.Information, "EasyTier network host started")]
    public static partial void Started(ILogger logger);

    [LoggerMessage(8151, LogLevel.Information, "EasyTier network host startup cancelled")]
    public static partial void StartCancelled(ILogger logger);

    [LoggerMessage(8152, LogLevel.Warning, "EasyTier network host startup failed")]
    public static partial void StartFailed(ILogger logger);

    [LoggerMessage(8153, LogLevel.Information, "EasyTier network host stopped")]
    public static partial void Stopped(ILogger logger);

    [LoggerMessage(8154, LogLevel.Warning, "EasyTier network host cleanup failed with {ErrorType}")]
    public static partial void StopFailed(ILogger logger, string errorType);

    [LoggerMessage(8155, LogLevel.Warning, "EasyTier network host exited unexpectedly")]
    public static partial void Exited(ILogger logger);

    [LoggerMessage(8156, LogLevel.Warning, "EasyTier network host exit observation failed with {ErrorType}")]
    public static partial void ExitObservationFailed(ILogger logger, string errorType);
}
