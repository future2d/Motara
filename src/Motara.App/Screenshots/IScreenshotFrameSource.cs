using Avalonia;
using Motara.App.Backgrounds;
using Motara.Persistence;
using SkiaSharp;

namespace Motara.App.Screenshots;

internal sealed record ScreenshotRenderRequest(
    Size CurrentCanvasSize,
    PixelSize TargetPixelSize,
    ScreenshotFramingMode FramingMode,
    bool UseTransparentBackground,
    ResolvedBackground Background);

internal interface IScreenshotFrameSource
{
    Task<ScreenshotRenderedFrame> CaptureAsync(
        ScreenshotRenderRequest request,
        CancellationToken cancellationToken);
}

internal interface IScreenshotModelFrameSource
{
    Task<SKImage?> CaptureCurrentFrameAsync(
        PixelSize pixelSize,
        SKRect destination,
        SKColor background,
        CancellationToken cancellationToken);
}

internal sealed class ScreenshotRenderedFrame : IDisposable
{
    private readonly Action? disposed;

    public ScreenshotRenderedFrame(SKImage image, byte[] previewPng, Action? disposed = null)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        PreviewPng = previewPng ?? throw new ArgumentNullException(nameof(previewPng));
        this.disposed = disposed;
    }

    public SKImage Image { get; }

    public byte[] PreviewPng { get; }

    public void Dispose()
    {
        Image.Dispose();
        disposed?.Invoke();
    }
}
