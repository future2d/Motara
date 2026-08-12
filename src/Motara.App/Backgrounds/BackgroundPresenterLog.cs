using Microsoft.Extensions.Logging;

namespace Motara.App.Backgrounds;

internal static partial class BackgroundPresenterLog
{
    [LoggerMessage(6765, LogLevel.Debug, "Background presentation request {Version} started for {Kind}")]
    internal static partial void ApplyStarted(
        ILogger logger,
        long version,
        string kind);

    [LoggerMessage(6766, LogLevel.Information, "Background presentation request {Version} completed; image={HasImage}")]
    internal static partial void ApplyCompleted(
        ILogger logger,
        long version,
        bool hasImage);

    [LoggerMessage(6767, LogLevel.Debug, "Background presentation request {Version} became stale")]
    internal static partial void StaleResult(ILogger logger, long version);

    [LoggerMessage(6768, LogLevel.Warning, "Background presentation request {Version} fell back because {Reason}")]
    internal static partial void Fallback(
        ILogger logger,
        long version,
        BackgroundFallbackReason reason);

    [LoggerMessage(6769, LogLevel.Information, "Background presenter stopped")]
    internal static partial void Stopped(ILogger logger);
}
