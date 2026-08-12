using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Motara.App.Screenshots;

internal sealed record ScreenshotResult(
    string FilePath,
    byte[] PreviewPng,
    Avalonia.PixelSize PixelSize,
    long FileSizeBytes);

internal interface IScreenshotService
{
    Task<ScreenshotResult> CaptureAsync(
        ScreenshotRenderRequest request,
        string? saveDirectory,
        CancellationToken cancellationToken);
}

internal sealed partial class ScreenshotService : IScreenshotService
{
    private readonly IScreenshotFrameSource frameSource;
    private readonly IScreenshotPathProvider pathProvider;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ScreenshotService> logger;

    public ScreenshotService(
        IScreenshotFrameSource frameSource,
        IScreenshotPathProvider pathProvider,
        TimeProvider timeProvider,
        ILogger<ScreenshotService> logger)
    {
        this.frameSource = frameSource ?? throw new ArgumentNullException(nameof(frameSource));
        this.pathProvider = pathProvider ?? throw new ArgumentNullException(nameof(pathProvider));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ScreenshotResult> CaptureAsync(
        ScreenshotRenderRequest request,
        string? saveDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        long startedAt = Stopwatch.GetTimestamp();
        ScreenshotLog.Requested(
            logger,
            request.TargetPixelSize.Width,
            request.TargetPixelSize.Height,
            request.UseTransparentBackground,
            request.FramingMode.ToString(),
            request.Background.Definition.Kind.ToString(),
            request.Background.IsSceneOverride);
        try
        {
            using ScreenshotRenderedFrame frame = await frameSource.CaptureAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            ScreenshotResult result = await Task.Run(
                () => Save(frame, request, saveDirectory, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            ScreenshotLog.Completed(
                logger,
                request.TargetPixelSize.Width,
                request.TargetPixelSize.Height,
                result.FileSizeBytes,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            return result;
        }
        catch (OperationCanceledException)
        {
            ScreenshotLog.Cancelled(logger, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            throw;
        }
        catch (Exception exception)
        {
            ScreenshotLog.Failed(
                logger,
                exception.GetType().Name,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            throw;
        }
    }

    private ScreenshotResult Save(
        ScreenshotRenderedFrame frame,
        ScreenshotRenderRequest request,
        string? saveDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = pathProvider.ResolveDirectory(saveDirectory);
        Directory.CreateDirectory(directory);
        using SKData png = frame.Image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Could not encode screenshot PNG.");
        string temporaryPath = Path.Combine(
            directory,
            $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.SequentialScan))
            {
                png.SaveTo(stream);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            string finalPath = MoveToUniqueFinalPath(
                temporaryPath,
                directory,
                timeProvider.GetLocalNow());
            return new ScreenshotResult(
                finalPath,
                frame.PreviewPng,
                request.TargetPixelSize,
                new FileInfo(finalPath).Length);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string MoveToUniqueFinalPath(
        string temporaryPath,
        string directory,
        DateTimeOffset timestamp)
    {
        string stem = $"Motara-{timestamp:yyyyMMdd-HHmmss-fff}";
        for (int suffix = 0; suffix < 1000; suffix++)
        {
            string fileName = suffix == 0
                ? $"{stem}.png"
                : $"{stem}-{suffix:000}.png";
            string candidate = Path.Combine(directory, fileName);
            try
            {
                File.Move(temporaryPath, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
            }
        }

        throw new IOException("Could not allocate a unique screenshot file name.");
    }
}

internal static partial class ScreenshotLog
{
    [LoggerMessage(
        7300,
        LogLevel.Information,
        "Screenshot requested Width={WidthPixels} Height={HeightPixels} Transparent={Transparent} Framing={Framing} BackgroundKind={BackgroundKind} SceneOverride={SceneOverride}")]
    internal static partial void Requested(
        ILogger logger,
        int widthPixels,
        int heightPixels,
        bool transparent,
        string framing,
        string backgroundKind,
        bool sceneOverride);

    [LoggerMessage(
        7301,
        LogLevel.Information,
        "Screenshot completed Width={WidthPixels} Height={HeightPixels} Bytes={FileSizeBytes} DurationMs={DurationMs}")]
    internal static partial void Completed(
        ILogger logger,
        int widthPixels,
        int heightPixels,
        long fileSizeBytes,
        double durationMs);

    [LoggerMessage(7302, LogLevel.Information, "Screenshot cancelled DurationMs={DurationMs}")]
    internal static partial void Cancelled(ILogger logger, double durationMs);

    [LoggerMessage(
        7303,
        LogLevel.Error,
        "Screenshot failed ErrorType={ErrorType} DurationMs={DurationMs}")]
    internal static partial void Failed(ILogger logger, string errorType, double durationMs);
}
