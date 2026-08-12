using Microsoft.Extensions.Logging;

namespace Motara.App.Backgrounds;

internal static partial class BackgroundAssetStoreLog
{
    [LoggerMessage(6760, LogLevel.Information, "Background asset import started")]
    internal static partial void ImportStarted(ILogger logger);

    [LoggerMessage(
        6761,
        LogLevel.Information,
        "Background asset {AssetId} imported as {Extension} at {PixelWidth}x{PixelHeight} from {ByteCount} bytes; decode took {DecodeMilliseconds} ms")]
    internal static partial void ImportCompleted(
        ILogger logger,
        string assetId,
        string extension,
        int pixelWidth,
        int pixelHeight,
        long byteCount,
        long decodeMilliseconds);

    [LoggerMessage(6762, LogLevel.Information, "Background asset {AssetId} reused")]
    internal static partial void ImportReused(ILogger logger, string assetId);

    [LoggerMessage(6763, LogLevel.Information, "Background asset import cancelled")]
    internal static partial void ImportCancelled(ILogger logger);

    [LoggerMessage(6764, LogLevel.Warning, "Background asset import failed with {ErrorType}")]
    internal static partial void ImportFailed(ILogger logger, string errorType);

    [LoggerMessage(6770, LogLevel.Information, "Background video import started")]
    internal static partial void VideoImportStarted(ILogger logger);

    [LoggerMessage(6771, LogLevel.Information, "Background video {AssetId} imported as {Extension} at {PixelWidth}x{PixelHeight}, {FramesPerSecond} FPS, alpha={HasAlpha}, from {ByteCount} bytes")]
    internal static partial void VideoImportCompleted(
        ILogger logger,
        string assetId,
        string extension,
        int pixelWidth,
        int pixelHeight,
        double framesPerSecond,
        bool hasAlpha,
        long byteCount);

    [LoggerMessage(6772, LogLevel.Information, "Background video {AssetId} reused")]
    internal static partial void VideoImportReused(ILogger logger, string assetId);

    [LoggerMessage(6773, LogLevel.Information, "Background video import cancelled")]
    internal static partial void VideoImportCancelled(ILogger logger);

    [LoggerMessage(6774, LogLevel.Warning, "Background video import failed with {ErrorType}")]
    internal static partial void VideoImportFailed(ILogger logger, string errorType);
}
