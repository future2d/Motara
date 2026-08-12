using System.Collections.Immutable;

namespace Motara.ModelRuntime.Abstractions;

public sealed record ModelCanvasInfo
{
    public ModelCanvasInfo(double width, double height, double pixelsPerUnit)
    {
        Width = ValidatePositive(width, nameof(width));
        Height = ValidatePositive(height, nameof(height));
        PixelsPerUnit = ValidatePositive(pixelsPerUnit, nameof(pixelsPerUnit));
    }

    public double Width { get; }

    public double Height { get; }

    public double PixelsPerUnit { get; }

    private static double ValidatePositive(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}

public sealed record ModelParameter
{
    public ModelParameter(string id, double minimum, double @default, double maximum, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!double.IsFinite(minimum)
            || !double.IsFinite(@default)
            || !double.IsFinite(maximum)
            || minimum > @default
            || @default > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(@default));
        }

        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Minimum = minimum;
        Default = @default;
        Maximum = maximum;
    }

    public string Id { get; }

    public string? Name { get; }

    public double Minimum { get; }

    public double Default { get; }

    public double Maximum { get; }
}

public sealed record ModelCapabilities
{
    public ModelCapabilities(
        ModelCanvasInfo canvas,
        ImmutableArray<ModelParameter> parameters,
        int textureCount,
        int drawableCount)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (parameters.IsDefault)
        {
            throw new ArgumentException("Parameters must be initialized.", nameof(parameters));
        }

        if (parameters.Any(static parameter => parameter is null))
        {
            throw new ArgumentException("Parameters cannot contain null values.", nameof(parameters));
        }

        if (parameters.Select(static parameter => parameter.Id).Distinct(StringComparer.Ordinal).Count()
            != parameters.Length)
        {
            throw new ArgumentException("Parameter IDs must be unique.", nameof(parameters));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(textureCount);
        ArgumentOutOfRangeException.ThrowIfNegative(drawableCount);

        Canvas = canvas;
        Parameters = parameters;
        TextureCount = textureCount;
        DrawableCount = drawableCount;
    }

    public ModelCanvasInfo Canvas { get; }

    public ImmutableArray<ModelParameter> Parameters { get; }

    public int TextureCount { get; }

    public int DrawableCount { get; }
}
