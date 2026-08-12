using Avalonia;
using Motara.App.Models;
using Motara.ModelLibrary;
using Motara.ModelRuntime.Abstractions;
using Motara.Scene;

namespace Motara.App.Rendering;

internal sealed record ModelRenderStateSnapshot(
    SceneTransform Scene,
    double ReferenceHeight,
    PixelSize PixelSize,
    ModelMotionExpansionSnapshot? Motion);

internal sealed class ModelRenderStateSource
{
    private readonly object gate = new();
    private ModelRenderStateSnapshot current =
        new(SceneTransform.Default, 1080, default, null);
    private ModelRasterTransform last = ModelRasterTransform.Identity;
    private ModelId? lastModelId;

    internal void PublishScene(SceneTransform scene, double referenceHeight, PixelSize pixelSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(referenceHeight, 0);
        lock (gate)
        {
            current = current with
            {
                Scene = scene,
                ReferenceHeight = referenceHeight,
                PixelSize = pixelSize,
            };
        }
    }

    internal void PublishMotion(ModelMotionExpansionSnapshot motion)
    {
        ArgumentNullException.ThrowIfNull(motion);
        lock (gate)
        {
            current = current with { Motion = motion };
        }
    }

    internal bool TryGetRasterTransform(ModelId modelId, out ModelRasterTransform transform)
    {
        lock (gate)
        {
            if (current.PixelSize.Height <= 0)
            {
                transform = default;
                return false;
            }

            ModelMotionExpansionSnapshot? motion =
                current.Motion?.ModelId == modelId ? current.Motion : null;
            double candidateScale = 1 - ((motion?.Y ?? 0) / 100d);
            double scaleStep = 1d / current.PixelSize.Height;
            double scale = lastModelId == modelId
                && Math.Abs(candidateScale - last.Scale) < scaleStep
                ? last.Scale
                : candidateScale;
            double aspect = current.PixelSize.Width / (double)current.PixelSize.Height;
            transform = new ModelRasterTransform(
                current.Scene.X / current.ReferenceHeight * scale
                    + aspect * ((motion?.X ?? 0) + (motion?.Z ?? 0)) / 100d,
                current.Scene.Y / current.ReferenceHeight * scale,
                current.Scene.Scale * scale,
                current.Scene.RotationDegrees);
            last = transform;
            lastModelId = modelId;
            return true;
        }
    }
}
