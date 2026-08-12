using Microsoft.Extensions.Logging;

namespace Motara.Collaboration.Models;

internal static partial class ModelPackageEvents
{
    [LoggerMessage(8100, LogLevel.Debug,
        "Model package build started; fileCount={FileCount}")]
    internal static partial void BuildStarted(ILogger logger, int fileCount);

    [LoggerMessage(8101, LogLevel.Information,
        "Model package build completed; fileCount={FileCount}; totalBytes={TotalBytes}; elapsedMilliseconds={ElapsedMilliseconds}")]
    internal static partial void BuildCompleted(
        ILogger logger,
        int fileCount,
        long totalBytes,
        long elapsedMilliseconds);

    [LoggerMessage(8102, LogLevel.Warning,
        "Model package build failed; errorCode={ErrorCode}; elapsedMilliseconds={ElapsedMilliseconds}")]
    internal static partial void BuildFailed(
        ILogger logger,
        ModelPackageErrorCode errorCode,
        long elapsedMilliseconds);

    [LoggerMessage(8103, LogLevel.Warning,
        "Model package build failed unexpectedly; errorType={ErrorType}; elapsedMilliseconds={ElapsedMilliseconds}")]
    internal static partial void BuildUnexpectedFailure(
        ILogger logger,
        string errorType,
        long elapsedMilliseconds);
}
