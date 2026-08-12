using Avalonia;
using Motara.ModelRuntime.Abstractions;
using Motara.Scene;

namespace Motara.App.Scenes;

internal readonly record struct AttachmentModelBinding(
    AttachmentModelAnchor Anchor,
    SceneTransform AnchorParent);

internal static class AttachmentModelBindingResolver
{
    internal static bool TryCreate(
        ModelRenderFrame? frame,
        Point surfacePoint,
        Size bounds,
        double referenceHeight,
        SceneTransform mainModelTransform,
        ModelRasterTransform rasterTransform,
        out AttachmentModelBinding binding)
    {
        ArgumentNullException.ThrowIfNull(mainModelTransform);
        binding = default;
        if (!IsValid(bounds, referenceHeight, rasterTransform)
            || !double.IsFinite(surfacePoint.X)
            || !double.IsFinite(surfacePoint.Y))
        {
            return false;
        }

        if (frame is not null
            && ModelArtMeshAnchorResolver.TryResolveTopmostAnchor(
                frame,
                surfacePoint,
                bounds,
                referenceHeight,
                rasterTransform,
                out ModelArtMeshAnchorResolver.ModelArtMeshAnchorHit hit))
        {
            binding = new AttachmentModelBinding(
                AttachmentModelAnchor.ForArtMesh(
                    hit.ArtMeshId,
                    hit.TriangleIndex,
                    hit.BarycentricU,
                    hit.BarycentricV),
                CreateAnchorParent(mainModelTransform, hit.ScenePoint));
            return true;
        }

        SceneTransform rootParent = CreateRootParent(
            mainModelTransform,
            referenceHeight,
            rasterTransform);
        SceneTransform requestedPoint = new(
            (surfacePoint.X - bounds.Width / 2d) / bounds.Height * referenceHeight,
            (surfacePoint.Y - bounds.Height / 2d) / bounds.Height * referenceHeight,
            1,
            0);
        SceneTransform local = AttachmentMountTransform.RelativeTo(requestedPoint, rootParent);
        AttachmentModelAnchor anchor = AttachmentModelAnchor.ForModelPlane(local.X, local.Y);
        binding = new AttachmentModelBinding(
            anchor,
            CreatePlaneAnchorParent(rootParent, anchor));
        return true;
    }

    internal static bool TryResolveParent(
        ModelRenderFrame? frame,
        AttachmentModelAnchor anchor,
        PixelSize pixelSize,
        double referenceHeight,
        SceneTransform mainModelTransform,
        ModelRasterTransform rasterTransform,
        out SceneTransform parent)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(mainModelTransform);
        parent = mainModelTransform;
        if (!double.IsFinite(referenceHeight)
            || referenceHeight <= 0
            || !rasterTransform.IsValid)
        {
            return false;
        }

        if (anchor.Kind == AttachmentModelAnchorKind.ModelPlane)
        {
            SceneTransform rootParent = CreateRootParent(
                mainModelTransform,
                referenceHeight,
                rasterTransform);
            parent = CreatePlaneAnchorParent(rootParent, anchor);
            return true;
        }

        if (frame is null
            || !ModelArtMeshAnchorResolver.TryResolveAnchorPoint(
                frame,
                anchor,
                pixelSize,
                referenceHeight,
                rasterTransform,
                out Point point))
        {
            return false;
        }

        parent = CreateAnchorParent(mainModelTransform, point);
        return true;
    }

    private static SceneTransform CreateRootParent(
        SceneTransform mainModelTransform,
        double referenceHeight,
        ModelRasterTransform rasterTransform) =>
        AttachmentMountTransform.Compose(
            mainModelTransform,
            new SceneTransform(
                rasterTransform.TranslationXRatio * referenceHeight,
                rasterTransform.TranslationYRatio * referenceHeight,
                rasterTransform.Scale,
                rasterTransform.RotationDegrees));

    private static SceneTransform CreatePlaneAnchorParent(
        SceneTransform rootParent,
        AttachmentModelAnchor anchor)
    {
        SceneTransform point = AttachmentMountTransform.Compose(
            rootParent,
            new SceneTransform(anchor.PlaneX, anchor.PlaneY, 1, 0));
        return new SceneTransform(
            point.X,
            point.Y,
            rootParent.Scale,
            rootParent.RotationDegrees);
    }

    private static SceneTransform CreateAnchorParent(
        SceneTransform mainModelTransform,
        Point point) =>
        new(
            point.X,
            point.Y,
            mainModelTransform.Scale,
            mainModelTransform.RotationDegrees);

    private static bool IsValid(
        Size bounds,
        double referenceHeight,
        ModelRasterTransform rasterTransform) =>
        bounds.Width > 0
        && bounds.Height > 0
        && double.IsFinite(referenceHeight)
        && referenceHeight > 0
        && rasterTransform.IsValid;
}
