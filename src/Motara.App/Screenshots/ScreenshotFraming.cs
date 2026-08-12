using Avalonia;
using Motara.Persistence;

namespace Motara.App.Screenshots;

internal static class ScreenshotFraming
{
    internal static Rect CalculateSourceViewport(
        Size currentCanvas,
        PixelSize target,
        ScreenshotFramingMode mode)
    {
        if (!double.IsFinite(currentCanvas.Width)
            || !double.IsFinite(currentCanvas.Height)
            || currentCanvas.Width <= 0
            || currentCanvas.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentCanvas));
        }

        if (target.Width <= 0 || target.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        double sourceAspect = currentCanvas.Width / currentCanvas.Height;
        double targetAspect = (double)target.Width / target.Height;
        if (Math.Abs(sourceAspect - targetAspect) < 1e-12)
        {
            return new Rect(currentCanvas);
        }

        bool adjustWidth = mode switch
        {
            ScreenshotFramingMode.ExtendCanvas => targetAspect > sourceAspect,
            ScreenshotFramingMode.CenterCrop => targetAspect < sourceAspect,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        double width = adjustWidth
            ? currentCanvas.Height * targetAspect
            : currentCanvas.Width;
        double height = adjustWidth
            ? currentCanvas.Height
            : currentCanvas.Width / targetAspect;
        return new Rect(
            (currentCanvas.Width - width) / 2,
            (currentCanvas.Height - height) / 2,
            width,
            height);
    }

    internal static Rect CalculateDestination(
        Size currentCanvas,
        PixelSize target,
        ScreenshotFramingMode mode)
    {
        Rect viewport = CalculateSourceViewport(currentCanvas, target, mode);
        double scaleX = target.Width / viewport.Width;
        double scaleY = target.Height / viewport.Height;
        return new Rect(
            -viewport.X * scaleX,
            -viewport.Y * scaleY,
            currentCanvas.Width * scaleX,
            currentCanvas.Height * scaleY);
    }
}
