using SkiaSharp;

namespace Motara.ModelRuntime.PurismCore;

internal sealed class MaskSurfacePool : IDisposable
{
    private const int MaximumDimension = 16_384;
    private const long MaximumPixels = 64L * 1024 * 1024;
    private SKSurface? _surface;
    private SKImageInfo _info;

    internal SKImageInfo Info => _info;

    internal SKSurface Rent(int width, int height)
    {
        ValidateDimensions(width, height);

        if (_surface is null || _info.Width < width || _info.Height < height)
        {
            int allocatedWidth = Math.Max(width, _info.Width);
            int allocatedHeight = Math.Max(height, _info.Height);
            ValidateDimensions(allocatedWidth, allocatedHeight);
            _surface?.Dispose();
            _info = new SKImageInfo(
                allocatedWidth,
                allocatedHeight,
                SKColorType.Bgra8888,
                SKAlphaType.Premul);
            _surface = SKSurface.Create(_info)
                ?? throw new InvalidOperationException("The mask surface could not be created.");
        }

        _surface.Canvas.Clear(SKColors.Transparent);
        return _surface;
    }

    internal static SKSurface CreateExact(int width, int height)
    {
        ValidateDimensions(width, height);
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        return SKSurface.Create(info)
            ?? throw new InvalidOperationException("The composite surface could not be created.");
    }

    public void Dispose()
    {
        _surface?.Dispose();
        _surface = null;
        _info = default;
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0
            || height <= 0
            || width > MaximumDimension
            || height > MaximumDimension
            || (long)width * height > MaximumPixels)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Mask surface dimensions are invalid.");
        }
    }
}
