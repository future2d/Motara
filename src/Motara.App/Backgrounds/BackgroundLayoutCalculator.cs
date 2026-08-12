using Avalonia;
using Avalonia.Media;
using Motara.Persistence;

namespace Motara.App.Backgrounds;

internal readonly record struct BackgroundPlacement(
    Rect Destination,
    bool Tile,
    Color MatteColor);

internal static class BackgroundLayoutCalculator
{
    internal static BackgroundPlacement Calculate(
        BackgroundLayoutMode mode,
        PixelSize sourcePixels,
        Size targetDip,
        Color matteColor)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (sourcePixels.Width <= 0 || sourcePixels.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourcePixels));
        }

        if (!double.IsFinite(targetDip.Width)
            || !double.IsFinite(targetDip.Height)
            || targetDip.Width <= 0
            || targetDip.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetDip));
        }

        double sourceWidth = sourcePixels.Width;
        double sourceHeight = sourcePixels.Height;
        return mode switch
        {
            BackgroundLayoutMode.Fill => CreateScaled(
                sourceWidth,
                sourceHeight,
                targetDip,
                Math.Max(targetDip.Width / sourceWidth, targetDip.Height / sourceHeight),
                matteColor),
            BackgroundLayoutMode.Fit => CreateScaled(
                sourceWidth,
                sourceHeight,
                targetDip,
                Math.Min(targetDip.Width / sourceWidth, targetDip.Height / sourceHeight),
                matteColor),
            BackgroundLayoutMode.Stretch => new BackgroundPlacement(
                new Rect(0, 0, targetDip.Width, targetDip.Height),
                Tile: false,
                matteColor),
            BackgroundLayoutMode.Center => new BackgroundPlacement(
                Center(sourceWidth, sourceHeight, targetDip),
                Tile: false,
                matteColor),
            BackgroundLayoutMode.Tile => new BackgroundPlacement(
                new Rect(0, 0, sourceWidth, sourceHeight),
                Tile: true,
                matteColor),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    private static BackgroundPlacement CreateScaled(
        double sourceWidth,
        double sourceHeight,
        Size target,
        double scale,
        Color matteColor)
    {
        double width = sourceWidth * scale;
        double height = sourceHeight * scale;
        return new BackgroundPlacement(
            Center(width, height, target),
            Tile: false,
            matteColor);
    }

    private static Rect Center(double width, double height, Size target) =>
        new(
            (target.Width - width) / 2,
            (target.Height - height) / 2,
            width,
            height);
}
