namespace Motara.ModelRuntime.Abstractions;

/// <summary>
/// View transform applied while rasterizing a model frame.
/// Translation values are ratios of the destination height so the same transform
/// remains stable when the output surface or DPI changes.
/// </summary>
public readonly record struct ModelRasterTransform(
    double TranslationXRatio,
    double TranslationYRatio,
    double Scale,
    double RotationDegrees)
{
    public static ModelRasterTransform Identity => new(0, 0, 1, 0);

    public bool IsValid =>
        double.IsFinite(TranslationXRatio)
        && double.IsFinite(TranslationYRatio)
        && double.IsFinite(Scale)
        && Scale > 0
        && double.IsFinite(RotationDegrees);
}
