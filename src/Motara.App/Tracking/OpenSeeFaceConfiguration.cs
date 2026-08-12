namespace Motara.App.Tracking;

internal sealed record OpenSeeFaceConfiguration
{
    internal const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public int CameraIndex { get; init; }

    public int Width { get; init; } = 640;

    public int Height { get; init; } = 360;

    public int Fps { get; init; } = 24;

    internal static OpenSeeFaceConfiguration Create(
        int cameraIndex = 0,
        int width = 640,
        int height = 360,
        int fps = 24)
    {
        var configuration = new OpenSeeFaceConfiguration
        {
            CameraIndex = cameraIndex,
            Width = width,
            Height = height,
            Fps = fps,
        };
        Validate(configuration);
        return configuration;
    }

    internal static void Validate(OpenSeeFaceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentOutOfRangeException.ThrowIfNotEqual(
            configuration.SchemaVersion,
            CurrentSchemaVersion);
        ArgumentOutOfRangeException.ThrowIfNegative(configuration.CameraIndex);
        ArgumentOutOfRangeException.ThrowIfLessThan(configuration.Width, 160);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(configuration.Width, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(configuration.Height, 120);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(configuration.Height, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(configuration.Fps, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(configuration.Fps, 120);
    }
}
