using Microsoft.Extensions.Logging;

namespace Motara.App.Diagnostics;

internal static partial class LogFileRetentionEvents
{
    [LoggerMessage(
        EventId = 1020,
        EventName = "LogRetentionCompleted",
        Level = LogLevel.Information,
        Message = "Log retention completed: deleted {DeletedFileCount} files, freed {FreedBytes} bytes, skipped {SkippedFileCount} files in {DurationMilliseconds} ms")]
    internal static partial void Completed(
        ILogger logger,
        int deletedFileCount,
        long freedBytes,
        int skippedFileCount,
        long durationMilliseconds);
}
