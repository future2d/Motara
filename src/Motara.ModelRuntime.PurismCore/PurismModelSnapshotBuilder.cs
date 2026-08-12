using System.Collections.Immutable;
using Motara.ModelRuntime.Abstractions;

namespace Motara.ModelRuntime.PurismCore;

internal readonly record struct NativeVector2Data(float X, float Y);

internal readonly record struct NativeColorData(float R, float G, float B, float A);

internal sealed record NativeCanvasData(float Width, float Height, float PixelsPerUnit);

internal sealed record NativeParameterData(string Id, float Minimum, float Default, float Maximum);

internal sealed record NativePartData(string Id, float Opacity);

internal sealed record NativeDrawableData
{
    internal NativeDrawableData(
        string id,
        int textureIndex,
        int renderOrder,
        float opacity,
        int blendMode,
        NativeVector2Data[] positions,
        NativeVector2Data[] uvs,
        ushort[] indices,
        int[] masks,
        bool isInvertedMask = false,
        NativeColorData? multiplyColor = null,
        NativeColorData? screenColor = null)
    {
        Id = id;
        TextureIndex = textureIndex;
        RenderOrder = renderOrder;
        Opacity = opacity;
        BlendMode = blendMode;
        Positions = positions;
        Uvs = uvs;
        Indices = indices;
        Masks = masks;
        IsInvertedMask = isInvertedMask;
        MultiplyColor = multiplyColor ?? new NativeColorData(1, 1, 1, 1);
        ScreenColor = screenColor ?? new NativeColorData(0, 0, 0, 1);
    }

    internal string Id { get; }

    internal int TextureIndex { get; }

    internal int RenderOrder { get; }

    internal float Opacity { get; }

    internal int BlendMode { get; }

    internal NativeVector2Data[] Positions { get; }

    internal NativeVector2Data[] Uvs { get; }

    internal ushort[] Indices { get; }

    internal int[] Masks { get; }

    internal bool IsInvertedMask { get; }

    internal NativeColorData MultiplyColor { get; }

    internal NativeColorData ScreenColor { get; }
}

internal interface IPurismModelView
{
    NativeCanvasData Canvas { get; }

    ImmutableArray<NativeParameterData> Parameters { get; }

    ImmutableArray<NativeDrawableData> Drawables { get; }
}

internal interface IPurismModelSession : IPurismModelView, IDisposable
{
    void ApplyParameters(
        ReadOnlySpan<ModelParameterValue> values,
        ReadOnlySpan<ModelPartOpacity> partOpacities);
}

internal sealed record PurismModelSnapshot(
    ModelCapabilities Capabilities,
    ModelRenderFrame Frame,
    int SelfMaskReferenceCount = 0,
    int InvertedMaskCount = 0,
    int NonDefaultBlendColorCount = 0);

internal static class PurismModelSnapshotBuilder
{
    private const float OpacityRoundingTolerance = 0.01f;
    private const int MaximumParameters = 100_000;
    private const int MaximumDrawables = 100_000;
    private const int MaximumVerticesPerDrawable = 1_000_000;
    private const int MaximumIndicesPerDrawable = 3_000_000;

    internal static PurismModelSnapshot Build(
        IPurismModelView view,
        int textureCount,
        IReadOnlyDictionary<string, string>? parameterNames = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentOutOfRangeException.ThrowIfNegative(textureCount);

        if (view.Parameters.IsDefault || view.Parameters.Length > MaximumParameters)
        {
            throw new InvalidDataException("Parameter count is invalid.");
        }

        ModelCanvasInfo canvas;
        try
        {
            canvas = new ModelCanvasInfo(
                view.Canvas.Width,
                view.Canvas.Height,
                view.Canvas.PixelsPerUnit);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("Canvas data is invalid.", exception);
        }

        ImmutableArray<ModelParameter> parameters = view.Parameters
            .Select(parameter => CreateParameter(parameter, parameterNames))
            .ToImmutableArray();

        ModelRenderFrame frame = BuildFrame(view, textureCount, canvas, revision: 0);
        var capabilities = new ModelCapabilities(canvas, parameters, textureCount, frame.Drawables.Length);
        int selfMaskReferenceCount = view.Drawables
            .Select((drawable, sourceIndex) => drawable.Masks.Count(index => index == sourceIndex))
            .Sum();
        int invertedMaskCount = view.Drawables.Count(static drawable => drawable.IsInvertedMask);
        int nonDefaultBlendColorCount = frame.Drawables.Count(static drawable =>
            drawable.MultiplyColor != ModelColor.MultiplyIdentity
            || drawable.ScreenColor != ModelColor.ScreenIdentity);
        return new PurismModelSnapshot(
            capabilities,
            frame,
            selfMaskReferenceCount,
            invertedMaskCount,
            nonDefaultBlendColorCount);
    }

    internal static ModelRenderFrame BuildFrame(
        IPurismModelView view,
        int textureCount,
        ModelCanvasInfo canvas,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentOutOfRangeException.ThrowIfNegative(textureCount);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        if (view.Drawables.IsDefault || view.Drawables.Length > MaximumDrawables)
        {
            throw new InvalidDataException("Drawable count is invalid.");
        }

        var sourceIndices = Enumerable.Range(0, view.Drawables.Length)
            .OrderBy(index => view.Drawables[index].RenderOrder)
            .ThenBy(static index => index)
            .ToArray();
        var remappedIndices = new int[sourceIndices.Length];
        for (int sortedIndex = 0; sortedIndex < sourceIndices.Length; sortedIndex++)
        {
            remappedIndices[sourceIndices[sortedIndex]] = sortedIndex;
        }

        var drawables = ImmutableArray.CreateBuilder<ModelDrawable>(sourceIndices.Length);
        foreach (int sourceIndex in sourceIndices)
        {
            drawables.Add(CreateDrawable(
                view.Drawables[sourceIndex],
                sourceIndex,
                textureCount,
                sourceIndices.Length,
                remappedIndices));
        }

        return new ModelRenderFrame(revision, canvas, drawables.MoveToImmutable());
    }

    private static ModelParameter CreateParameter(
        NativeParameterData parameter,
        IReadOnlyDictionary<string, string>? parameterNames)
    {
        try
        {
            string? name = null;
            parameterNames?.TryGetValue(parameter.Id, out name);
            return new ModelParameter(parameter.Id, parameter.Minimum, parameter.Default, parameter.Maximum, name);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Parameter data is invalid.", exception);
        }
    }

    private static ModelDrawable CreateDrawable(
        NativeDrawableData drawable,
        int sourceIndex,
        int textureCount,
        int drawableCount,
        int[] remappedIndices)
    {
        if (drawable.TextureIndex < 0 || drawable.TextureIndex >= textureCount)
        {
            throw new InvalidDataException("Drawable texture index is invalid.");
        }

        if (drawable.Positions.Length != drawable.Uvs.Length
            || drawable.Positions.Length > MaximumVerticesPerDrawable
            || drawable.Indices.Length > MaximumIndicesPerDrawable
            || drawable.Indices.Length % 3 != 0)
        {
            throw new InvalidDataException("Drawable geometry size is invalid.");
        }

        if (drawable.Masks.Any(index => index < 0 || index >= drawableCount))
        {
            throw new InvalidDataException("Drawable mask index is invalid.");
        }

        try
        {
            ImmutableArray<ModelVertex> vertices = drawable.Positions
                .Select((position, index) => new ModelVertex(
                    position.X,
                    position.Y,
                    drawable.Uvs[index].X,
                    1f - drawable.Uvs[index].Y))
                .ToImmutableArray();
            ImmutableArray<int> masks = drawable.Masks
                .Select(index => remappedIndices[index])
                .ToImmutableArray();

            return new ModelDrawable(
                drawable.Id,
                drawable.TextureIndex,
                drawable.RenderOrder,
                NormalizeOpacity(drawable.Opacity),
                MapBlendMode(drawable.BlendMode),
                vertices,
                [.. drawable.Indices],
                masks,
                drawable.IsInvertedMask,
                CreateColor(drawable.MultiplyColor),
                CreateColor(drawable.ScreenColor));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Drawable data is invalid.", exception);
        }
    }

    private static ModelColor CreateColor(NativeColorData color) =>
        new(color.R, color.G, color.B, color.A);

    private static float NormalizeOpacity(float opacity)
    {
        if (!float.IsFinite(opacity)
            || opacity < -OpacityRoundingTolerance
            || opacity > 1 + OpacityRoundingTolerance)
        {
            throw new InvalidDataException("Drawable opacity is invalid.");
        }

        return Math.Clamp(opacity, 0, 1);
    }

    private static ModelBlendMode MapBlendMode(int blendMode)
    {
        if ((blendMode & ~0xFFFF) != 0 || ((blendMode >> 8) & 0xFF) > 4)
        {
            throw new InvalidDataException("Drawable blend mode is unsupported.");
        }

        return (blendMode & 0xFF) switch
        {
            0 => ModelBlendMode.Normal,
            1 or 3 or 4 => ModelBlendMode.Additive,
            2 or 6 => ModelBlendMode.Multiplicative,
            5 => ModelBlendMode.Darken,
            7 => ModelBlendMode.ColorBurn,
            9 => ModelBlendMode.Lighten,
            10 => ModelBlendMode.Screen,
            11 => ModelBlendMode.ColorDodge,
            12 => ModelBlendMode.Overlay,
            13 => ModelBlendMode.SoftLight,
            14 => ModelBlendMode.HardLight,
            16 => ModelBlendMode.Hue,
            17 => ModelBlendMode.Color,
            _ => throw new InvalidDataException("Drawable blend mode is unsupported."),
        };
    }
}
