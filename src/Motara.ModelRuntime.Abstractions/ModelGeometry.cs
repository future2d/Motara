using System.Collections.Immutable;

namespace Motara.ModelRuntime.Abstractions;

public enum ModelBlendMode
{
    Normal = 0,
    Additive = 1,
    Multiplicative = 2,
    Darken = 3,
    ColorBurn = 4,
    Lighten = 5,
    Screen = 6,
    ColorDodge = 7,
    Overlay = 8,
    SoftLight = 9,
    HardLight = 10,
    Hue = 11,
    Color = 12,
}

public readonly record struct ModelColor
{
    public ModelColor(float r, float g, float b, float a)
    {
        R = ValidateChannel(r, nameof(r));
        G = ValidateChannel(g, nameof(g));
        B = ValidateChannel(b, nameof(b));
        A = ValidateChannel(a, nameof(a));
    }

    public static ModelColor MultiplyIdentity { get; } = new(1, 1, 1, 1);

    public static ModelColor ScreenIdentity { get; } = new(0, 0, 0, 1);

    public float R { get; }

    public float G { get; }

    public float B { get; }

    public float A { get; }

    private static float ValidateChannel(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}

public readonly record struct ModelVertex
{
    public ModelVertex(float x, float y, float u, float v)
    {
        X = ValidateFinite(x, nameof(x));
        Y = ValidateFinite(y, nameof(y));
        U = ValidateFinite(u, nameof(u));
        V = ValidateFinite(v, nameof(v));
    }

    public float X { get; }

    public float Y { get; }

    public float U { get; }

    /// <summary>Gets the vertical texture coordinate measured from the top edge.</summary>
    public float V { get; }

    private static float ValidateFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}

public sealed record ModelDrawable
{
    public ModelDrawable(
        string id,
        int textureIndex,
        int renderOrder,
        float opacity,
        ModelBlendMode blendMode,
        ImmutableArray<ModelVertex> vertices,
        ImmutableArray<ushort> indices,
        ImmutableArray<int> masks,
        bool isInvertedMask = false,
        ModelColor? multiplyColor = null,
        ModelColor? screenColor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegative(textureIndex);

        if (!float.IsFinite(opacity) || opacity is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity));
        }

        if (!Enum.IsDefined(blendMode))
        {
            throw new ArgumentOutOfRangeException(nameof(blendMode));
        }

        if (vertices.IsDefault)
        {
            throw new ArgumentException("Vertices must be initialized.", nameof(vertices));
        }

        if (indices.IsDefault || indices.Length % 3 != 0)
        {
            throw new ArgumentException("Triangle indices must be initialized.", nameof(indices));
        }

        if (indices.Any(index => index >= vertices.Length))
        {
            throw new ArgumentOutOfRangeException(nameof(indices));
        }

        if (masks.IsDefault)
        {
            throw new ArgumentException("Masks must be initialized.", nameof(masks));
        }

        if (masks.Any(static index => index < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(masks));
        }

        Id = id;
        TextureIndex = textureIndex;
        RenderOrder = renderOrder;
        Opacity = opacity;
        BlendMode = blendMode;
        Vertices = vertices;
        Indices = indices;
        Masks = masks;
        IsInvertedMask = isInvertedMask;
        MultiplyColor = multiplyColor ?? ModelColor.MultiplyIdentity;
        ScreenColor = screenColor ?? ModelColor.ScreenIdentity;
    }

    public string Id { get; }

    public int TextureIndex { get; }

    public int RenderOrder { get; }

    public float Opacity { get; }

    public ModelBlendMode BlendMode { get; }

    public ImmutableArray<ModelVertex> Vertices { get; }

    public ImmutableArray<ushort> Indices { get; }

    public ImmutableArray<int> Masks { get; }

    public bool IsInvertedMask { get; }

    public ModelColor MultiplyColor { get; }

    public ModelColor ScreenColor { get; }
}

public readonly record struct ModelPartOpacity
{
    public ModelPartOpacity(string partId, float opacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partId);
        if (!float.IsFinite(opacity) || opacity is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity));
        }

        PartId = partId;
        Opacity = opacity;
    }

    public string PartId { get; }

    public float Opacity { get; }
}

public sealed record ModelRenderFrame
{
    public ModelRenderFrame(
        long revision,
        ModelCanvasInfo canvas,
        ImmutableArray<ModelDrawable> drawables)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(revision);

        ArgumentNullException.ThrowIfNull(canvas);
        if (drawables.IsDefault)
        {
            throw new ArgumentException("Drawables must be initialized.", nameof(drawables));
        }

        if (drawables.Any(static drawable => drawable is null))
        {
            throw new ArgumentException("Drawables cannot contain null values.", nameof(drawables));
        }

        for (int drawableIndex = 0; drawableIndex < drawables.Length; drawableIndex++)
        {
            foreach (int maskIndex in drawables[drawableIndex].Masks)
            {
                if (maskIndex >= drawables.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(drawables));
                }
            }
        }

        Revision = revision;
        Canvas = canvas;
        Drawables = drawables;
    }

    public long Revision { get; }

    public ModelCanvasInfo Canvas { get; }

    public ImmutableArray<ModelDrawable> Drawables { get; }
}
