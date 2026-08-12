using Microsoft.Extensions.Logging;

namespace Motara.Scene;

internal static partial class SceneRepositoryLog
{
    [LoggerMessage(6200, LogLevel.Information, "Scene repository loaded {SceneCount} scenes")]
    internal static partial void LoadCompleted(ILogger logger, int sceneCount);

    [LoggerMessage(
        6201,
        LogLevel.Debug,
        "Scene repository saved {SceneCount} scenes, {MainModelCount} main models, {AttachmentCount} attachments, {EffectCount} effects")]
    internal static partial void SaveCompleted(
        ILogger logger,
        int sceneCount,
        int mainModelCount,
        int attachmentCount,
        int effectCount);

    [LoggerMessage(6202, LogLevel.Warning, "Scene repository load failed with {ErrorType}; default scene restored")]
    internal static partial void LoadFailed(ILogger logger, string errorType);
}
