using Microsoft.Extensions.Logging;

namespace Motara.Collaboration.Models;

internal static partial class ModelPackageTransferEvents
{
    [LoggerMessage(8110, LogLevel.Information,
        "Remote model package reception started; fileCount={FileCount}; totalBytes={TotalBytes}")]
    internal static partial void Started(ILogger logger, int fileCount, long totalBytes);

    [LoggerMessage(8111, LogLevel.Information,
        "Remote model package reception completed; fileCount={FileCount}; totalBytes={TotalBytes}; chunkCount={ChunkCount}; elapsedMilliseconds={ElapsedMilliseconds}")]
    internal static partial void Completed(
        ILogger logger,
        int fileCount,
        long totalBytes,
        long chunkCount,
        long elapsedMilliseconds);

    [LoggerMessage(8112, LogLevel.Information,
        "Remote model package reception aborted; releasedBytes={ReleasedBytes}")]
    internal static partial void Aborted(ILogger logger, long releasedBytes);

    [LoggerMessage(8113, LogLevel.Warning,
        "Remote model package reception rejected; errorCode={ErrorCode}")]
    internal static partial void Rejected(ILogger logger, ModelPackageErrorCode errorCode);

    [LoggerMessage(8114, LogLevel.Information,
        "Remote model package released; fileCount={FileCount}; releasedBytes={ReleasedBytes}")]
    internal static partial void Released(ILogger logger, int fileCount, long releasedBytes);
}
