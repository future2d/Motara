namespace Motara.Scene;

public sealed record BuiltInBlurEffectSettings
{
    public BuiltInBlurEffectSettings(double radius)
    {
        if (!double.IsFinite(radius) || radius < 0 || radius > 40)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Blur radius must be between 0 and 40.");
        }

        Radius = radius;
    }

    public double Radius { get; }
}

public sealed record SceneEffectInstance
{
    public SceneEffectInstance(
        Guid sourceId,
        string effectId,
        bool isEnabled,
        BuiltInBlurEffectSettings? blur = null)
    {
        if (sourceId == Guid.Empty) throw new ArgumentException("Effect ID cannot be empty.", nameof(sourceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(effectId);
        if (StringComparer.Ordinal.Equals(effectId, "builtin.blur") != (blur is not null))
        {
            throw new ArgumentException("The effect settings do not match the effect ID.", nameof(blur));
        }

        SourceId = sourceId;
        EffectId = effectId;
        IsEnabled = isEnabled;
        Blur = blur;
    }

    public Guid SourceId { get; }

    public string EffectId { get; }

    public bool IsEnabled { get; init; }

    public BuiltInBlurEffectSettings? Blur { get; init; }

    public static SceneEffectInstance CreateBlur(double radius, bool isEnabled = true) =>
        new(Guid.NewGuid(), "builtin.blur", isEnabled, new BuiltInBlurEffectSettings(radius));

    public SceneEffectInstance SetEnabled(bool isEnabled) => this with { IsEnabled = isEnabled };

    public SceneEffectInstance SetBlurRadius(double radius) =>
        EffectId == "builtin.blur"
            ? this with { Blur = new BuiltInBlurEffectSettings(radius) }
            : throw new InvalidOperationException("Only blur effects support a blur radius.");
}
