namespace Motara.Persistence;

public enum ScreenshotFramingMode
{
    ExtendCanvas = 0,
    CenterCrop = 1,
}

public sealed record ScreenshotSettings(
    int CountdownSeconds,
    bool UseTransparentBackground,
    bool UseCustomResolution,
    int WidthPixels,
    int HeightPixels,
    ScreenshotFramingMode FramingMode,
    string? SaveDirectory)
{
    public const int MaximumDimensionPixels = 16_384;
    public const long MaximumPixels = 132_710_400;

    public static ScreenshotSettings Default { get; } = new(
        CountdownSeconds: 0,
        UseTransparentBackground: false,
        UseCustomResolution: false,
        WidthPixels: 1920,
        HeightPixels: 1080,
        FramingMode: ScreenshotFramingMode.ExtendCanvas,
        SaveDirectory: null);

    public static void Validate(ScreenshotSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.CountdownSeconds is < 0 or > 10
            || value.WidthPixels is <= 0 or > MaximumDimensionPixels
            || value.HeightPixels is <= 0 or > MaximumDimensionPixels
            || (long)value.WidthPixels * value.HeightPixels > MaximumPixels
            || !Enum.IsDefined(value.FramingMode))
        {
            throw new ArgumentException("Invalid screenshot settings.", nameof(value));
        }

        if (value.SaveDirectory is not null && string.IsNullOrWhiteSpace(value.SaveDirectory))
        {
            throw new ArgumentException("Screenshot directory cannot be blank.", nameof(value));
        }
    }
}
