using Microsoft.Extensions.Logging;

namespace Motara.App.Scenes;

internal static partial class SceneAttachmentAnchorLog
{
    [LoggerMessage(6891, LogLevel.Debug, "No ArtMesh was hit when selecting an attachment anchor for {SourceId}")]
    internal static partial void SelectionMissed(ILogger logger, Guid sourceId);

    [LoggerMessage(6892, LogLevel.Warning, "Attachment anchor selection failed for {SourceId} with {ExceptionType}")]
    internal static partial void SelectionFailed(
        ILogger logger,
        Guid sourceId,
        string exceptionType);
}
