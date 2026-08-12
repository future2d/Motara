using Microsoft.Extensions.Logging;

namespace Motara.App.Models;

internal static partial class ModelFileOrganizationLog
{
    [LoggerMessage(6680, LogLevel.Debug, "Model file organization analysis started for {ModelId}")]
    internal static partial void AnalysisStarted(ILogger logger, string modelId);

    [LoggerMessage(6681, LogLevel.Information,
        "Model file organization analysis completed for {ModelId}: motions={MotionCount}, expressions={ExpressionCount}, preview={HasPreviewMove}, needsOrganization={NeedsOrganization}; elapsed={ElapsedMilliseconds}ms")]
    internal static partial void AnalysisCompleted(
        ILogger logger, string modelId, int motionCount, int expressionCount,
        bool hasPreviewMove, bool needsOrganization, long elapsedMilliseconds);

    [LoggerMessage(6682, LogLevel.Warning,
        "Model file organization analysis failed for {ModelId}: {ErrorType}; elapsed={ElapsedMilliseconds}ms")]
    internal static partial void AnalysisFailed(
        ILogger logger, string modelId, string errorType, long elapsedMilliseconds);

    [LoggerMessage(6683, LogLevel.Information, "Model file organization execution started for {ModelId}")]
    internal static partial void ExecutionStarted(ILogger logger, string modelId);

    [LoggerMessage(6684, LogLevel.Warning,
        "Model file organization execution blocked for {ModelId}: {ErrorCode}")]
    internal static partial void ExecutionBlocked(ILogger logger, string modelId, string errorCode);

    [LoggerMessage(6685, LogLevel.Information,
        "Model file organization execution completed for {ModelId}: moved={MovedFileCount}; elapsed={ElapsedMilliseconds}ms")]
    internal static partial void ExecutionCompleted(
        ILogger logger, string modelId, int movedFileCount, long elapsedMilliseconds);

    [LoggerMessage(6686, LogLevel.Information,
        "Model file organization execution cancelled for {ModelId}; elapsed={ElapsedMilliseconds}ms")]
    internal static partial void ExecutionCancelled(ILogger logger, string modelId, long elapsedMilliseconds);

    [LoggerMessage(6687, LogLevel.Error,
        "Model file organization execution failed for {ModelId}: {ErrorType}, rolledBack={RolledBack}; elapsed={ElapsedMilliseconds}ms")]
    internal static partial void ExecutionFailed(
        ILogger logger, string modelId, string errorType, bool rolledBack, long elapsedMilliseconds);
}
