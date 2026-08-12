using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Backgrounds;
using Motara.Persistence;
using SkiaSharp;

namespace Motara.App.Screenshots;

internal sealed class SkiaScreenshotFrameSource : IScreenshotFrameSource
{
    private const int MaximumPreviewWidth = 1280;
    private const int MaximumPreviewHeight = 720;
    private const long MaximumDecodedPixels = 64L * 1024 * 1024;
    private static readonly SKSamplingOptions Sampling = new(
        SKFilterMode.Linear,
        SKMipmapMode.None);

    private readonly IScreenshotModelFrameSource modelFrames;
    private readonly Func<string, string>? resolveManagedPath;
    private readonly Func<BackgroundVisualSnapshot?>? resolveCurrentBackground;
    private readonly ILogger<SkiaScreenshotFrameSource> logger;

    internal SkiaScreenshotFrameSource(IScreenshotModelFrameSource modelFrames)
        : this(modelFrames, resolveManagedPath: null, logger: null)
    {
    }

    internal SkiaScreenshotFrameSource(
        IScreenshotModelFrameSource modelFrames,
        Func<string, string>? resolveManagedPath,
        ILogger<SkiaScreenshotFrameSource>? logger = null,
        Func<BackgroundVisualSnapshot?>? resolveCurrentBackground = null)
    {
        this.modelFrames = modelFrames ?? throw new ArgumentNullException(nameof(modelFrames));
        this.resolveManagedPath = resolveManagedPath;
        this.resolveCurrentBackground = resolveCurrentBackground;
        this.logger = logger ?? NullLogger<SkiaScreenshotFrameSource>.Instance;
    }

    public async Task<ScreenshotRenderedFrame> CaptureAsync(
        ScreenshotRenderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Rect destination = ScreenshotFraming.CalculateDestination(
            request.CurrentCanvasSize,
            request.TargetPixelSize,
            request.FramingMode);
        SKImage? modelImage = await modelFrames.CaptureCurrentFrameAsync(
            request.TargetPixelSize,
            new SKRect(
                (float)destination.Left,
                (float)destination.Top,
                (float)destination.Right,
                (float)destination.Bottom),
            SKColors.Transparent,
            cancellationToken).ConfigureAwait(false);
        SKImage image;
        try
        {
            image = await Task.Run(
                () => ComposeFrame(request, modelImage, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            modelImage?.Dispose();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] preview = await Task.Run(
                () => CreatePreviewPng(image),
                cancellationToken).ConfigureAwait(false);
            return new ScreenshotRenderedFrame(image, preview);
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private SKImage ComposeFrame(
        ScreenshotRenderRequest request,
        SKImage? modelImage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SKSurface surface = CreateSurface(request.TargetPixelSize);
        DrawBackground(surface.Canvas, request, cancellationToken);
        if (modelImage is not null)
        {
            surface.Canvas.DrawImage(
                modelImage,
                new SKRect(
                    0,
                    0,
                    request.TargetPixelSize.Width,
                    request.TargetPixelSize.Height),
                Sampling);
        }

        cancellationToken.ThrowIfCancellationRequested();
        surface.Canvas.Flush();
        return surface.Snapshot();
    }

    private void DrawBackground(
        SKCanvas canvas,
        ScreenshotRenderRequest request,
        CancellationToken cancellationToken)
    {
        if (request.UseTransparentBackground)
        {
            canvas.Clear(SKColors.Transparent);
            return;
        }

        BackgroundDefinition definition = request.Background.Definition;
        Color matte = BackgroundColorParser.Parse(definition.SolidColor);
        var matteColor = new SKColor(matte.R, matte.G, matte.B, matte.A);
        canvas.Clear(matteColor);
        if (definition.Kind == BackgroundKind.Video)
        {
            DrawCurrentVideoFrame(canvas, request, definition, matte, cancellationToken);
            return;
        }

        if (definition.Kind != BackgroundKind.Image)
        {
            return;
        }

        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            if (resolveManagedPath is null)
            {
                throw new FileNotFoundException("Background asset resolution is unavailable.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            string path = resolveManagedPath(definition.ImageAssetId!);
            using SKBitmap bitmap = DecodeImage(path, cancellationToken);
            using SKImage image = SKImage.FromBitmap(bitmap);
            BackgroundPlacement placement = BackgroundLayoutCalculator.Calculate(
                definition.Layout,
                new PixelSize(bitmap.Width, bitmap.Height),
                new Size(request.TargetPixelSize.Width, request.TargetPixelSize.Height),
                matte);
            DrawPlacedImage(canvas, image, placement, request.TargetPixelSize);
            ScreenshotBackgroundLog.Drawn(
                logger,
                definition.ImageAssetId!,
                bitmap.Width,
                bitmap.Height,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ScreenshotBackgroundLog.Fallback(
                logger,
                definition.ImageAssetId!,
                exception.GetType().Name);
        }
    }

    private void DrawCurrentVideoFrame(
        SKCanvas canvas,
        ScreenshotRenderRequest request,
        BackgroundDefinition definition,
        Color matte,
        CancellationToken cancellationToken)
    {
        BackgroundVideoFrameSnapshot? snapshot = resolveCurrentBackground?.Invoke()?.Video?.CaptureCurrentFrame();
        if (snapshot is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var bitmap = new SKBitmap(
            new SKImageInfo(
                snapshot.Width,
                snapshot.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Unpremul));
        IntPtr pixels = bitmap.GetPixels();
        int rowBytes = checked(snapshot.Width * 4);
        for (int row = 0; row < snapshot.Height; row++)
        {
            Marshal.Copy(
                snapshot.BgraPixels,
                row * rowBytes,
                pixels + row * bitmap.RowBytes,
                rowBytes);
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        BackgroundPlacement placement = BackgroundLayoutCalculator.Calculate(
            definition.Layout,
            new PixelSize(snapshot.Width, snapshot.Height),
            new Size(request.TargetPixelSize.Width, request.TargetPixelSize.Height),
            matte);
        DrawPlacedImage(canvas, image, placement, request.TargetPixelSize);
    }

    private static SKBitmap DecodeImage(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SKCodec codec = SKCodec.Create(path)
            ?? throw new InvalidDataException("Background image could not be decoded.");
        SKImageInfo source = codec.Info;
        if (source.Width <= 0
            || source.Height <= 0
            || (long)source.Width * source.Height > MaximumDecodedPixels)
        {
            throw new InvalidDataException("Background image dimensions are invalid.");
        }

        var decodeInfo = new SKImageInfo(
            source.Width,
            source.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        var bitmap = new SKBitmap(decodeInfo);
        try
        {
            SKCodecResult result = codec.GetPixels(decodeInfo, bitmap.GetPixels());
            if (result != SKCodecResult.Success)
            {
                throw new InvalidDataException("Background image decoding failed.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static void DrawPlacedImage(
        SKCanvas canvas,
        SKImage image,
        BackgroundPlacement placement,
        PixelSize targetSize)
    {
        if (placement.Tile)
        {
            using SKShader shader = image.ToShader(
                SKShaderTileMode.Repeat,
                SKShaderTileMode.Repeat,
                Sampling);
            using var paint = new SKPaint { Shader = shader, IsAntialias = true };
            canvas.DrawRect(
                new SKRect(0, 0, targetSize.Width, targetSize.Height),
                paint);
            return;
        }

        Rect destination = placement.Destination;
        canvas.DrawImage(
            image,
            new SKRect(
                (float)destination.Left,
                (float)destination.Top,
                (float)destination.Right,
                (float)destination.Bottom),
            Sampling);
    }

    private static SKSurface CreateSurface(PixelSize pixelSize) =>
        SKSurface.Create(
            new SKImageInfo(
                pixelSize.Width,
                pixelSize.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Premul))
        ?? throw new InvalidOperationException("Could not create screenshot surface.");

    private static byte[] CreatePreviewPng(SKImage image)
    {
        double scale = Math.Min(
            1,
            Math.Min(
                (double)MaximumPreviewWidth / image.Width,
                (double)MaximumPreviewHeight / image.Height));
        int width = Math.Max(1, (int)Math.Round(image.Width * scale));
        int height = Math.Max(1, (int)Math.Round(image.Height * scale));
        using SKSurface surface = SKSurface.Create(
            new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create screenshot preview surface.");
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(
            image,
            new SKRect(0, 0, width, height),
            Sampling);
        surface.Canvas.Flush();
        using SKImage preview = surface.Snapshot();
        using SKData png = preview.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Could not encode screenshot preview.");
        return png.ToArray();
    }
}

internal static partial class ScreenshotBackgroundLog
{
    [LoggerMessage(
        7304,
        LogLevel.Debug,
        "Screenshot background {AssetId} drawn at {Width}x{Height} in {DurationMs} ms")]
    internal static partial void Drawn(
        ILogger logger,
        string assetId,
        int width,
        int height,
        double durationMs);

    [LoggerMessage(
        7305,
        LogLevel.Warning,
        "Screenshot background {AssetId} fell back to its matte color because of {ErrorType}")]
    internal static partial void Fallback(
        ILogger logger,
        string assetId,
        string errorType);
}
