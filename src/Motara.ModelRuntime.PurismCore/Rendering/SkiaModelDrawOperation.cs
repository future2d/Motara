using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Motara.ModelRuntime.Abstractions;
using SkiaSharp;

namespace Motara.ModelRuntime.PurismCore;

internal sealed class SkiaModelDrawOperation : ICustomDrawOperation
{
    private SkiaModelRenderer? _renderer;
    private readonly ModelRenderFrame _frame;
    private readonly PixelSize _pixelSize;
    private readonly double _renderingScale;
    private readonly ModelRasterTransform _rasterTransform;
    private readonly double? _blurRadius;
    private readonly SKPaint? _blurPaint;

    internal SkiaModelDrawOperation(
        SkiaModelRenderer renderer,
        ModelRenderFrame frame,
        PixelSize pixelSize,
        double renderingScale,
        ModelRasterTransform rasterTransform,
        double? blurRadius)
    {
        _renderer = renderer;
        _frame = frame;
        _pixelSize = pixelSize;
        _renderingScale = renderingScale;
        _rasterTransform = rasterTransform;
        if (blurRadius is double radius && radius > 0)
        {
            _blurRadius = radius;
            _blurPaint = new SKPaint
            {
                ImageFilter = SKImageFilter.CreateBlur((float)radius, (float)radius),
            };
        }
        Bounds = new Rect(
            0,
            0,
            pixelSize.Width / renderingScale,
            pixelSize.Height / renderingScale);
    }

    public Rect Bounds { get; }

    public bool HitTest(Point point) => false;

    public void Render(ImmediateDrawingContext context)
    {
        SkiaModelRenderer? renderer = Volatile.Read(ref _renderer);
        if (renderer is null)
        {
            return;
        }

        if (!renderer.TryAcquireRenderReference())
        {
            return;
        }

        try
        {
            ISkiaSharpApiLeaseFeature? leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null)
            {
                return;
            }

            using ISkiaSharpApiLease lease = leaseFeature.Lease();
            renderer.TryRenderLeasedCpu(
                lease.SkCanvas,
                _frame,
                new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height),
                _pixelSize,
                _renderingScale,
                _rasterTransform,
                _blurPaint);
        }
        catch (ObjectDisposedException)
        {
            renderer.ReportDisposedDrawOperation();
        }
        finally
        {
            renderer.ReleaseRenderReference();
        }
    }

    public bool Equals(ICustomDrawOperation? other) => other is SkiaModelDrawOperation operation
        && ReferenceEquals(Volatile.Read(ref _renderer), Volatile.Read(ref operation._renderer))
        && ReferenceEquals(_frame, operation._frame)
        && _pixelSize == operation._pixelSize
        && _renderingScale.Equals(operation._renderingScale)
        && _rasterTransform == operation._rasterTransform
        && Nullable.Equals(_blurRadius, operation._blurRadius);

    public void Dispose()
    {
        _blurPaint?.Dispose();
        Interlocked.Exchange(ref _renderer, null)?.ReleaseDrawOperation();
    }
}
