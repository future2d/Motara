using Microsoft.Extensions.Logging;

namespace Motara.Media;

internal static partial class VideoSignalLog
{
    [LoggerMessage(6800, LogLevel.Debug, "Video signal source discovery started for {Protocol}")]
    internal static partial void SourceDiscoveryStarted(ILogger logger, VideoSignalProtocol protocol);

    [LoggerMessage(6801, LogLevel.Information, "Video signal source discovery completed for {Protocol} with {SourceCount} sources")]
    internal static partial void SourceDiscoveryCompleted(ILogger logger, VideoSignalProtocol protocol, int sourceCount);

    [LoggerMessage(6802, LogLevel.Warning, "Video signal source discovery failed for {Protocol} with {ErrorType}")]
    internal static partial void SourceDiscoveryFailed(ILogger logger, VideoSignalProtocol protocol, string errorType);

    [LoggerMessage(6803, LogLevel.Information, "Video signal receiver state changed to {State} for {Protocol}; error={ErrorType}")]
    internal static partial void ReceiverStateChanged(ILogger logger, VideoSignalState state, string protocol, string errorType);

    [LoggerMessage(6804, LogLevel.Information, "Video signal reconnect scheduled after {DelayMs}ms")]
    internal static partial void ReconnectScheduled(ILogger logger, double delayMs);

    [LoggerMessage(6805, LogLevel.Warning, "Video signal reconnect task failed with {ErrorType}")]
    internal static partial void ReconnectFailed(ILogger logger, string errorType);

    [LoggerMessage(6806, LogLevel.Warning, "Video signal stop did not complete because of {ErrorType}")]
    internal static partial void StopIncomplete(ILogger logger, string errorType);
}
