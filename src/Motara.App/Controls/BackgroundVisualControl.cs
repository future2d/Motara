using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Motara.App.Backgrounds;
using Motara.Persistence;

namespace Motara.App.Controls;

public sealed class BackgroundVisualControl : Control
{
    private BackgroundVisualSnapshot snapshot = BackgroundVisualSnapshot.Initial;

    internal BackgroundVisualSnapshot Snapshot
    {
        get => snapshot;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(snapshot, value))
            {
                return;
            }

            if (VisualRoot is not null && snapshot.Video is not null)
            {
                snapshot.Video.FrameChanged -= OnVideoFrameChanged;
            }

            snapshot = value;
            if (VisualRoot is not null && snapshot.Video is not null)
            {
                snapshot.Video.FrameChanged += OnVideoFrameChanged;
            }

            InvalidateVisual();
        }
    }

    private void OnVideoFrameChanged(object? sender, EventArgs args) => InvalidateVisual();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        snapshot.Video?.FrameChanged += OnVideoFrameChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        snapshot.Video?.FrameChanged -= OnVideoFrameChanged;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        Rect target = new(Bounds.Size);
        Color matteColor = BackgroundColorParser.Parse(snapshot.Definition.SolidColor);
        context.DrawRectangle(new SolidColorBrush(matteColor), null, target);
        if (snapshot.Definition.Kind is not (BackgroundKind.Image or BackgroundKind.Video or BackgroundKind.Signal)
            || snapshot.Image is not { } image)
        {
            return;
        }

        BackgroundPlacement placement = BackgroundLayoutCalculator.Calculate(
            snapshot.Definition.Layout,
            image.PixelSize,
            Bounds.Size,
            matteColor);
        DrawingContext.PushedState? opacity = snapshot.Definition.Kind is BackgroundKind.Video or BackgroundKind.Signal
            ? context.PushOpacity(matteColor.A / 255d)
            : null;
        try
        {
            if (placement.Tile)
            {
                var brush = new ImageBrush(image)
                {
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                    DestinationRect = new RelativeRect(
                        placement.Destination,
                        RelativeUnit.Absolute),
                    SourceRect = new RelativeRect(
                        new Rect(0, 0, 1, 1),
                        RelativeUnit.Relative),
                    Stretch = Stretch.Fill,
                    TileMode = TileMode.Tile,
                };
                context.DrawRectangle(brush, null, target);
                return;
            }

            context.DrawImage(image, new Rect(image.Size), placement.Destination);
        }
        finally
        {
            opacity?.Dispose();
        }
    }
}
