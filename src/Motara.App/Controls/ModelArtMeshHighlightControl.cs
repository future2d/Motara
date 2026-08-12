using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Motara.ModelRuntime.Abstractions;
using Motara.Scene;
using System.Globalization;

namespace Motara.App.Controls;

internal readonly record struct AnchorSelectorVisual(
    Point Point,
    string Label,
    AttachmentModelAnchorKind Kind = AttachmentModelAnchorKind.ArtMesh);

public sealed class ModelArtMeshHighlightControl : Control
{
    private ModelRenderFrame? frame;
    private PixelSize pixelSize;
    private ModelRasterTransform rasterTransform = ModelRasterTransform.Identity;
    private string? selectedArtMeshId;
    private AnchorSelectorVisual[] anchorSelectors = [];

    public ModelArtMeshHighlightControl()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    internal void SetFrameState(
        ModelRenderFrame value,
        PixelSize size,
        ModelRasterTransform transform)
    {
        ArgumentNullException.ThrowIfNull(value);
        frame = value;
        pixelSize = size;
        rasterTransform = transform;
        InvalidateVisual();
    }

    internal void SetSelectedArtMesh(string? artMeshId)
    {
        selectedArtMeshId = artMeshId;
        InvalidateVisual();
    }

    internal void SetAnchorSelectorPoint(Point? point)
    {
        anchorSelectors = point is { } value ? [new AnchorSelectorVisual(value, string.Empty)] : [];
        InvalidateVisual();
    }

    internal void SetAnchorSelectorPoints(IReadOnlyList<Point> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        anchorSelectors = points.Select(static point => new AnchorSelectorVisual(point, string.Empty)).ToArray();
        InvalidateVisual();
    }

    internal void SetAnchorSelectors(IReadOnlyList<AnchorSelectorVisual> selectors)
    {
        ArgumentNullException.ThrowIfNull(selectors);
        anchorSelectors = selectors.ToArray();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        DrawSelectedArtMesh(context);
        DrawAnchorSelector(context);
    }

    private void DrawSelectedArtMesh(DrawingContext context)
    {
        ModelRenderFrame? current = frame;
        if (current is null
            || string.IsNullOrWhiteSpace(selectedArtMeshId)
            || Bounds.Width <= 0
            || Bounds.Height <= 0
            || !rasterTransform.IsValid)
        {
            return;
        }

        ModelDrawable? drawable = current.Drawables.FirstOrDefault(candidate =>
            StringComparer.Ordinal.Equals(candidate.Id, selectedArtMeshId));
        if (drawable is null || drawable.Indices.IsEmpty)
        {
            return;
        }

        double modelWidth = current.Canvas.Width / current.Canvas.PixelsPerUnit;
        double modelHeight = current.Canvas.Height / current.Canvas.PixelsPerUnit;
        double scale = Math.Min(Bounds.Width / modelWidth, Bounds.Height / modelHeight)
            * rasterTransform.Scale;
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return;
        }

        double translationX = rasterTransform.TranslationXRatio * Bounds.Height;
        double translationY = rasterTransform.TranslationYRatio * Bounds.Height;
        Matrix transform = Matrix.CreateScale(scale, -scale)
            * Matrix.CreateRotation(rasterTransform.RotationDegrees * Math.PI / 180d)
            * Matrix.CreateTranslation(
                Bounds.Width / 2 + translationX,
                Bounds.Height / 2 + translationY);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext path = geometry.Open())
        {
            for (int index = 0; index < drawable.Indices.Length; index += 3)
            {
                path.BeginFigure(ToPoint(drawable, drawable.Indices[index]), isFilled: true);
                path.LineTo(ToPoint(drawable, drawable.Indices[index + 1]));
                path.LineTo(ToPoint(drawable, drawable.Indices[index + 2]));
                path.EndFigure(isClosed: true);
            }
        }

        if (!this.TryFindResource("CategoryApricot", out object? accentResource)
            || accentResource is not IBrush accent)
        {
            return;
        }

        IBrush fill = accent is ISolidColorBrush solid
            ? new SolidColorBrush(solid.Color, 0.20)
            : accent;
        using (context.PushTransform(transform))
        {
            context.DrawGeometry(
                fill,
                new Pen(accent, 1.5 / scale),
                geometry);
        }
    }

    private void DrawAnchorSelector(DrawingContext context)
    {
        if (anchorSelectors.Length == 0)
        {
            return;
        }

        if (!this.TryFindResource("CategoryApricot", out object? accentResource)
            || accentResource is not IBrush accent)
        {
            return;
        }

        var pen = new Pen(accent, 1.5);
        foreach (AnchorSelectorVisual selector in anchorSelectors)
        {
            Point point = selector.Point;
            context.DrawLine(pen, new Point(point.X - 10, point.Y), new Point(point.X + 10, point.Y));
            context.DrawLine(pen, new Point(point.X, point.Y - 10), new Point(point.X, point.Y + 10));
            if (selector.Kind == AttachmentModelAnchorKind.ArtMesh)
            {
                context.DrawEllipse(Brushes.Transparent, pen, point, 4, 4);
            }
            else
            {
                context.DrawRectangle(
                    Brushes.Transparent,
                    pen,
                    new Rect(point.X - 4, point.Y - 4, 8, 8));
            }

            if (string.IsNullOrWhiteSpace(selector.Label))
            {
                continue;
            }

            var text = new FormattedText(
                selector.Label,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                12,
                accent);
            var labelRect = new Rect(
                point.X + 8,
                point.Y - text.Height - 8,
                text.Width + 8,
                text.Height + 4);
            context.DrawRectangle(
                new SolidColorBrush(Colors.Black, 0.65),
                null,
                labelRect,
                3,
                3);
            context.DrawText(text, new Point(labelRect.X + 4, labelRect.Y + 2));
        }
    }

    private static Point ToPoint(ModelDrawable drawable, ushort index)
    {
        ModelVertex vertex = drawable.Vertices[index];
        return new Point(vertex.X, vertex.Y);
    }
}
