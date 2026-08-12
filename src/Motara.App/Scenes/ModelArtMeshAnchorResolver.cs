using Avalonia;
using Motara.ModelRuntime.Abstractions;
using Motara.Scene;

namespace Motara.App.Scenes;

internal static class ModelArtMeshAnchorResolver
{
    internal readonly record struct ModelArtMeshAnchorHit(
        string ArtMeshId,
        int TriangleIndex,
        double BarycentricU,
        double BarycentricV,
        Point ScenePoint);

    internal static bool TryResolveTopmost(
        ModelRenderFrame frame,
        Point surfacePoint,
        Size bounds,
        double referenceHeight,
        ModelRasterTransform rasterTransform,
        out string? artMeshId,
        out Point center)
    {
        bool resolved = TryResolveTopmostAnchor(
            frame,
            surfacePoint,
            bounds,
            referenceHeight,
            rasterTransform,
            out ModelArtMeshAnchorHit hit);
        artMeshId = resolved ? hit.ArtMeshId : null;
        center = resolved ? hit.ScenePoint : default;
        return resolved;
    }

    internal static bool TryResolveTopmostAnchor(
        ModelRenderFrame frame,
        Point surfacePoint,
        Size bounds,
        double referenceHeight,
        ModelRasterTransform rasterTransform,
        out ModelArtMeshAnchorHit hit)
    {
        ArgumentNullException.ThrowIfNull(frame);
        hit = default;
        if (!TryGetModelTransform(
                frame,
                bounds,
                referenceHeight,
                rasterTransform,
                out double scale,
                out double cosine,
                out double sine))
        {
            return false;
        }

        double dx = surfacePoint.X - bounds.Width / 2d
            - rasterTransform.TranslationXRatio * bounds.Height;
        double dy = surfacePoint.Y - bounds.Height / 2d
            - rasterTransform.TranslationYRatio * bounds.Height;
        double localX = (cosine * dx + sine * dy) / scale;
        double localY = (sine * dx - cosine * dy) / scale;
        var localPoint = new Point(localX, localY);

        foreach (ModelDrawable drawable in frame.Drawables
                     .Where(static candidate => candidate.Opacity > 0)
                     .OrderByDescending(static candidate => candidate.RenderOrder))
        {
            if (drawable.Vertices.IsEmpty || drawable.Indices.IsEmpty)
            {
                continue;
            }

            for (int index = 0; index + 2 < drawable.Indices.Length; index += 3)
            {
                ModelVertex first = drawable.Vertices[drawable.Indices[index]];
                ModelVertex second = drawable.Vertices[drawable.Indices[index + 1]];
                ModelVertex third = drawable.Vertices[drawable.Indices[index + 2]];
                if (!ContainsPoint(localPoint, first, second, third))
                {
                    continue;
                }

                (double firstWeight, double secondWeight, double thirdWeight) =
                    CalculateBarycentric(localPoint, first, second, third);
                Point scenePoint = ToScenePoint(
                    localX: (first.X * firstWeight)
                        + (second.X * secondWeight)
                        + (third.X * thirdWeight),
                    localY: (first.Y * firstWeight)
                        + (second.Y * secondWeight)
                        + (third.Y * thirdWeight),
                    bounds,
                    referenceHeight,
                    scale,
                    rasterTransform,
                    cosine,
                    sine);
                hit = new ModelArtMeshAnchorHit(
                    drawable.Id,
                    index / 3,
                    secondWeight,
                    thirdWeight,
                    scenePoint);
                return true;
            }
        }

        return false;
    }

    internal static bool TryCreateModelPlaneAnchor(
        ModelRenderFrame frame,
        Point surfacePoint,
        Size bounds,
        double referenceHeight,
        ModelRasterTransform rasterTransform,
        out AttachmentModelAnchor? anchor)
    {
        ArgumentNullException.ThrowIfNull(frame);
        anchor = null;
        if (!TryGetModelTransform(
                frame,
                bounds,
                referenceHeight,
                rasterTransform,
                out double scale,
                out double cosine,
                out double sine))
        {
            return false;
        }

        double dx = surfacePoint.X - bounds.Width / 2d
            - rasterTransform.TranslationXRatio * bounds.Height;
        double dy = surfacePoint.Y - bounds.Height / 2d
            - rasterTransform.TranslationYRatio * bounds.Height;
        double localX = (cosine * dx + sine * dy) / scale;
        double localY = (sine * dx - cosine * dy) / scale;
        if (!double.IsFinite(localX) || !double.IsFinite(localY))
        {
            return false;
        }

        anchor = AttachmentModelAnchor.ForModelPlane(localX, localY);
        return true;
    }

    internal static bool TryResolveCenter(
        ModelRenderFrame frame,
        string artMeshId,
        PixelSize pixelSize,
        double referenceHeight,
        ModelRasterTransform rasterTransform,
        out Point center)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(artMeshId);
        center = default;
        if (pixelSize.Width <= 0
            || pixelSize.Height <= 0
            || !double.IsFinite(referenceHeight)
            || referenceHeight <= 0
            || !rasterTransform.IsValid)
        {
            return false;
        }

        ModelDrawable? drawable = frame.Drawables.FirstOrDefault(candidate =>
            StringComparer.Ordinal.Equals(candidate.Id, artMeshId));
        if (drawable is null || drawable.Vertices.IsEmpty)
        {
            return false;
        }

        double modelWidth = frame.Canvas.Width / frame.Canvas.PixelsPerUnit;
        double modelHeight = frame.Canvas.Height / frame.Canvas.PixelsPerUnit;
        double modelScale = Math.Min(pixelSize.Width / modelWidth, pixelSize.Height / modelHeight);
        if (!double.IsFinite(modelScale) || modelScale <= 0)
        {
            return false;
        }

        double minX = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity;
        double maxY = double.NegativeInfinity;
        foreach (ModelVertex vertex in drawable.Vertices)
        {
            minX = Math.Min(minX, vertex.X);
            maxX = Math.Max(maxX, vertex.X);
            minY = Math.Min(minY, vertex.Y);
            maxY = Math.Max(maxY, vertex.Y);
        }

        double localX = (minX + maxX) / 2;
        double localY = (minY + maxY) / 2;
        double scale = modelScale * rasterTransform.Scale;
        double radians = rasterTransform.RotationDegrees * Math.PI / 180d;
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        center = ToScenePoint(
            localX,
            localY,
            new Size(pixelSize.Width, pixelSize.Height),
            referenceHeight,
            scale,
            rasterTransform,
            cosine,
            sine);
        return double.IsFinite(center.X) && double.IsFinite(center.Y);
    }

    internal static bool TryResolveAnchorPoint(
        ModelRenderFrame frame,
        AttachmentModelAnchor anchor,
        PixelSize pixelSize,
        double referenceHeight,
        ModelRasterTransform rasterTransform,
        out Point point)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        point = default;
        if (anchor.Kind == AttachmentModelAnchorKind.ModelPlane)
        {
            if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
            {
                return false;
            }

            double planeModelWidth = frame.Canvas.Width / frame.Canvas.PixelsPerUnit;
            double planeModelHeight = frame.Canvas.Height / frame.Canvas.PixelsPerUnit;
            double planeModelScale = Math.Min(
                pixelSize.Width / planeModelWidth,
                pixelSize.Height / planeModelHeight);
            double planeScale = planeModelScale * rasterTransform.Scale;
            double planeRadians = rasterTransform.RotationDegrees * Math.PI / 180d;
            point = ToScenePoint(
                anchor.PlaneX,
                anchor.PlaneY,
                new Size(pixelSize.Width, pixelSize.Height),
                referenceHeight,
                planeScale,
                rasterTransform,
                Math.Cos(planeRadians),
                Math.Sin(planeRadians));
            return double.IsFinite(point.X) && double.IsFinite(point.Y);
        }

        string artMeshId = anchor.ArtMeshId!;
        if (anchor.TriangleIndex < 0
            || pixelSize.Width <= 0
            || pixelSize.Height <= 0
            || !double.IsFinite(anchor.BarycentricU)
            || !double.IsFinite(anchor.BarycentricV)
            || anchor.BarycentricU < 0
            || anchor.BarycentricV < 0
            || anchor.BarycentricU + anchor.BarycentricV > 1)
        {
            return TryResolveCenter(
                frame,
                artMeshId,
                pixelSize,
                referenceHeight,
                rasterTransform,
                out point);
        }

        ModelDrawable? drawable = frame.Drawables.FirstOrDefault(candidate =>
            StringComparer.Ordinal.Equals(candidate.Id, artMeshId));
        int start = anchor.TriangleIndex * 3;
        if (drawable is null
            || start < 0
            || start + 2 >= drawable.Indices.Length
            || drawable.Vertices.IsEmpty)
        {
            return false;
        }

        ModelVertex first = drawable.Vertices[drawable.Indices[start]];
        ModelVertex second = drawable.Vertices[drawable.Indices[start + 1]];
        ModelVertex third = drawable.Vertices[drawable.Indices[start + 2]];
        double secondWeight = anchor.BarycentricU;
        double thirdWeight = anchor.BarycentricV;
        double firstWeight = 1 - secondWeight - thirdWeight;
        if (!double.IsFinite(firstWeight) || firstWeight < 0)
        {
            return false;
        }

        double modelWidth = frame.Canvas.Width / frame.Canvas.PixelsPerUnit;
        double modelHeight = frame.Canvas.Height / frame.Canvas.PixelsPerUnit;
        double modelScale = Math.Min(pixelSize.Width / modelWidth, pixelSize.Height / modelHeight);
        double scale = modelScale * rasterTransform.Scale;
        double radians = rasterTransform.RotationDegrees * Math.PI / 180d;
        point = ToScenePoint(
            (first.X * firstWeight) + (second.X * secondWeight) + (third.X * thirdWeight),
            (first.Y * firstWeight) + (second.Y * secondWeight) + (third.Y * thirdWeight),
            new Size(pixelSize.Width, pixelSize.Height),
            referenceHeight,
            scale,
            rasterTransform,
            Math.Cos(radians),
            Math.Sin(radians));
        return double.IsFinite(point.X) && double.IsFinite(point.Y);
    }

    private static Point ToScenePoint(
        double localX,
        double localY,
        Size bounds,
        double referenceHeight,
        double scale,
        ModelRasterTransform rasterTransform,
        double cosine,
        double sine)
    {
        double screenX = bounds.Width / 2d
            + rasterTransform.TranslationXRatio * bounds.Height
            + localX * scale * cosine
            + localY * scale * sine;
        double screenY = bounds.Height / 2d
            + rasterTransform.TranslationYRatio * bounds.Height
            + localX * scale * sine
            - localY * scale * cosine;
        return new Point(
            (screenX - bounds.Width / 2d) / bounds.Height * referenceHeight,
            (screenY - bounds.Height / 2d) / bounds.Height * referenceHeight);
    }

    private static (double First, double Second, double Third) CalculateBarycentric(
        Point point,
        ModelVertex first,
        ModelVertex second,
        ModelVertex third)
    {
        double denominator = Cross(
            second.X - first.X,
            second.Y - first.Y,
            third.X - first.X,
            third.Y - first.Y);
        if (Math.Abs(denominator) <= 1e-7)
        {
            return (1, 0, 0);
        }

        double secondWeight = Cross(
            point.X - first.X,
            point.Y - first.Y,
            third.X - first.X,
            third.Y - first.Y) / denominator;
        double thirdWeight = Cross(
            second.X - first.X,
            second.Y - first.Y,
            point.X - first.X,
            point.Y - first.Y) / denominator;
        return (1 - secondWeight - thirdWeight, secondWeight, thirdWeight);
    }

    private static bool TryGetModelTransform(
        ModelRenderFrame frame,
        Size bounds,
        double referenceHeight,
        ModelRasterTransform rasterTransform,
        out double scale,
        out double cosine,
        out double sine)
    {
        scale = 0;
        cosine = 0;
        sine = 0;
        if (bounds.Width <= 0
            || bounds.Height <= 0
            || !double.IsFinite(referenceHeight)
            || referenceHeight <= 0
            || !rasterTransform.IsValid
            || frame.Canvas.Width <= 0
            || frame.Canvas.Height <= 0
            || frame.Canvas.PixelsPerUnit <= 0)
        {
            return false;
        }

        double modelWidth = frame.Canvas.Width / frame.Canvas.PixelsPerUnit;
        double modelHeight = frame.Canvas.Height / frame.Canvas.PixelsPerUnit;
        double modelScale = Math.Min(bounds.Width / modelWidth, bounds.Height / modelHeight);
        scale = modelScale * rasterTransform.Scale;
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return false;
        }

        double radians = rasterTransform.RotationDegrees * Math.PI / 180d;
        cosine = Math.Cos(radians);
        sine = Math.Sin(radians);
        return true;
    }

    private static bool ContainsPoint(Point point, ModelVertex first, ModelVertex second, ModelVertex third)
    {
        const double epsilon = 1e-7;
        if (Math.Abs(Cross(
                second.X - first.X,
                second.Y - first.Y,
                third.X - first.X,
                third.Y - first.Y)) <= epsilon)
        {
            return false;
        }

        double firstCross = Cross(second.X - first.X, second.Y - first.Y, point.X - first.X, point.Y - first.Y);
        double secondCross = Cross(third.X - second.X, third.Y - second.Y, point.X - second.X, point.Y - second.Y);
        double thirdCross = Cross(first.X - third.X, first.Y - third.Y, point.X - third.X, point.Y - third.Y);
        return (firstCross >= -epsilon && secondCross >= -epsilon && thirdCross >= -epsilon)
            || (firstCross <= epsilon && secondCross <= epsilon && thirdCross <= epsilon);
    }

    private static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;
}
