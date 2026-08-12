using Motara.ModelRuntime.Abstractions;

namespace Motara.Persistence;

public enum ModelCatalogLayoutMode
{
    List = 0,
    Grid = 1,
}

public enum FrameRateMode
{
    FramesPerSecond60 = 0,
    FramesPerSecond30 = 1,
    VSync = 2,
    VSyncHalf = 3,
}

public enum ContentScaleMode
{
    Automatic = 0,
    Fixed = 1,
}

public enum ApplicationLanguage
{
    Automatic = 0,
    English = 1,
    SimplifiedChinese = 2,
}

/// <summary>Contains versioned, local-only shell presentation settings.</summary>
public sealed record UiSettings
{
    public const int CurrentSchemaVersion = 1;
    public const int DefaultWindowWidthPixels = 1280;
    public const int DefaultWindowHeightPixels = 720;
    public const double DefaultContentScale = 1;
    public const string DefaultCanvasBackgroundColor = "#F4F3F1";

    public static UiSettings Default { get; } = Create(
        isDeveloperModeEnabled: false,
        isNavigationRailVisible: true);

    public UiSettings(
        int schemaVersion,
        bool isDeveloperModeEnabled,
        bool isNavigationRailVisible,
        DiagnosticLogLevel diagnosticLogLevel = DiagnosticLogLevel.Information,
        bool restoreActiveSceneOnStartup = false,
        ModelCatalogLayoutMode modelCatalogLayoutMode = ModelCatalogLayoutMode.List,
        int windowWidthPixels = DefaultWindowWidthPixels,
        int windowHeightPixels = DefaultWindowHeightPixels,
        ContentScaleMode contentScaleMode = ContentScaleMode.Automatic,
        double contentScale = DefaultContentScale,
        FrameRateMode frameRateMode = FrameRateMode.FramesPerSecond60,
        bool isWindowSizeLocked = false,
        ScreenshotSettings? screenshot = null,
        CubismEditorOutputSettings? cubismEditor = null,
        bool rememberFaceTrackingOnStartup = false,
        ApplicationLanguage applicationLanguage = ApplicationLanguage.Automatic,
        ModelRenderingBackendPreference modelRenderingBackendPreference = ModelRenderingBackendPreference.Cpu,
        BackgroundDefinition? globalBackground = null)
    {
        SchemaVersion = schemaVersion;
        IsDeveloperModeEnabled = isDeveloperModeEnabled;
        IsNavigationRailVisible = isNavigationRailVisible;
        DiagnosticLogLevel = diagnosticLogLevel;
        RestoreActiveSceneOnStartup = restoreActiveSceneOnStartup;
        ModelCatalogLayoutMode = modelCatalogLayoutMode;
        WindowWidthPixels = windowWidthPixels;
        WindowHeightPixels = windowHeightPixels;
        ContentScaleMode = contentScaleMode;
        ContentScale = contentScale;
        FrameRateMode = frameRateMode;
        IsWindowSizeLocked = isWindowSizeLocked;
        Screenshot = screenshot ?? ScreenshotSettings.Default;
        CubismEditor = cubismEditor ?? CubismEditorOutputSettings.Default;
        RememberFaceTrackingOnStartup = rememberFaceTrackingOnStartup;
        ApplicationLanguage = applicationLanguage;
        ModelRenderingBackendPreference = modelRenderingBackendPreference;
        GlobalBackground = globalBackground ?? BackgroundDefinition.Solid(DefaultCanvasBackgroundColor);
    }

    public int SchemaVersion { get; init; }

    public bool IsDeveloperModeEnabled { get; init; }

    public bool IsNavigationRailVisible { get; init; }

    public DiagnosticLogLevel DiagnosticLogLevel { get; init; }

    public bool RestoreActiveSceneOnStartup { get; init; }

    public ModelCatalogLayoutMode ModelCatalogLayoutMode { get; init; }

    public int WindowWidthPixels { get; init; }

    public int WindowHeightPixels { get; init; }

    public ContentScaleMode ContentScaleMode { get; init; }

    public double ContentScale { get; init; }

    public FrameRateMode FrameRateMode { get; init; }

    public bool IsWindowSizeLocked { get; init; }

    public ScreenshotSettings Screenshot { get; init; }

    public CubismEditorOutputSettings CubismEditor { get; init; }

    public bool RememberFaceTrackingOnStartup { get; init; }

    public ApplicationLanguage ApplicationLanguage { get; init; }

    public ModelRenderingBackendPreference ModelRenderingBackendPreference { get; init; }

    public BackgroundDefinition GlobalBackground { get; init; }

    public static UiSettings Create(
        bool isDeveloperModeEnabled,
        bool isNavigationRailVisible,
        DiagnosticLogLevel diagnosticLogLevel = DiagnosticLogLevel.Information,
        bool restoreActiveSceneOnStartup = false,
        ModelCatalogLayoutMode modelCatalogLayoutMode = ModelCatalogLayoutMode.List,
        int windowWidthPixels = DefaultWindowWidthPixels,
        int windowHeightPixels = DefaultWindowHeightPixels,
        ContentScaleMode contentScaleMode = ContentScaleMode.Automatic,
        double contentScale = DefaultContentScale,
        FrameRateMode frameRateMode = FrameRateMode.FramesPerSecond60,
        bool isWindowSizeLocked = false,
        ScreenshotSettings? screenshot = null,
        CubismEditorOutputSettings? cubismEditor = null,
        bool rememberFaceTrackingOnStartup = false,
        ApplicationLanguage applicationLanguage = ApplicationLanguage.Automatic,
        ModelRenderingBackendPreference modelRenderingBackendPreference = ModelRenderingBackendPreference.Cpu,
        BackgroundDefinition? globalBackground = null)
    {
        var settings = new UiSettings(
            CurrentSchemaVersion,
            isDeveloperModeEnabled,
            isNavigationRailVisible,
            diagnosticLogLevel,
            restoreActiveSceneOnStartup,
            modelCatalogLayoutMode,
            windowWidthPixels,
            windowHeightPixels,
            contentScaleMode,
            contentScale,
            frameRateMode,
            isWindowSizeLocked,
            screenshot,
            cubismEditor,
            rememberFaceTrackingOnStartup,
            applicationLanguage,
            modelRenderingBackendPreference,
            globalBackground);
        Validate(settings);
        return settings;
    }

    internal static void Validate(UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SchemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException("Unsupported UI settings schema version.", nameof(settings));
        }

        if (!Enum.IsDefined(settings.DiagnosticLogLevel))
        {
            throw new ArgumentException("Diagnostic log level is invalid.", nameof(settings));
        }

        if (!Enum.IsDefined(settings.ModelCatalogLayoutMode))
        {
            throw new ArgumentException("Model catalog layout mode is invalid.", nameof(settings));
        }

        if (settings.WindowWidthPixels <= 0 || settings.WindowHeightPixels <= 0)
        {
            throw new ArgumentException("Window dimensions must be positive pixel values.", nameof(settings));
        }

        if (!Enum.IsDefined(settings.ContentScaleMode))
        {
            throw new ArgumentException("Content scale mode is invalid.", nameof(settings));
        }

        if (settings.ContentScale is not (0.25 or 0.5 or 0.75 or 1 or 1.5 or 2 or 3 or 4))
        {
            throw new ArgumentException("Content scale is unsupported.", nameof(settings));
        }

        if (!Enum.IsDefined(settings.FrameRateMode))
        {
            throw new ArgumentException("Frame rate mode is invalid.", nameof(settings));
        }

        if (!Enum.IsDefined(settings.ApplicationLanguage))
        {
            throw new ArgumentException("Application language is invalid.", nameof(settings));
        }

        if (!Enum.IsDefined(settings.ModelRenderingBackendPreference))
        {
            throw new ArgumentException("Model rendering backend preference is invalid.", nameof(settings));
        }

        BackgroundDefinition.Validate(settings.GlobalBackground);

        ScreenshotSettings.Validate(settings.Screenshot);
        CubismEditorOutputSettings.Validate(settings.CubismEditor);

    }

    public bool Equals(UiSettings? other) => other is not null
        && SchemaVersion == other.SchemaVersion
        && IsDeveloperModeEnabled == other.IsDeveloperModeEnabled
        && IsNavigationRailVisible == other.IsNavigationRailVisible
        && DiagnosticLogLevel == other.DiagnosticLogLevel
        && RestoreActiveSceneOnStartup == other.RestoreActiveSceneOnStartup
        && ModelCatalogLayoutMode == other.ModelCatalogLayoutMode
        && WindowWidthPixels == other.WindowWidthPixels
        && WindowHeightPixels == other.WindowHeightPixels
        && ContentScaleMode == other.ContentScaleMode
        && ContentScale.Equals(other.ContentScale)
        && FrameRateMode == other.FrameRateMode
        && IsWindowSizeLocked == other.IsWindowSizeLocked
        && Screenshot == other.Screenshot
        && CubismEditor == other.CubismEditor
        && RememberFaceTrackingOnStartup == other.RememberFaceTrackingOnStartup
        && ApplicationLanguage == other.ApplicationLanguage
        && ModelRenderingBackendPreference == other.ModelRenderingBackendPreference
        && GlobalBackground == other.GlobalBackground;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(IsDeveloperModeEnabled);
        hash.Add(IsNavigationRailVisible);
        hash.Add(DiagnosticLogLevel);
        hash.Add(RestoreActiveSceneOnStartup);
        hash.Add(ModelCatalogLayoutMode);
        hash.Add(WindowWidthPixels);
        hash.Add(WindowHeightPixels);
        hash.Add(ContentScaleMode);
        hash.Add(ContentScale);
        hash.Add(FrameRateMode);
        hash.Add(IsWindowSizeLocked);
        hash.Add(Screenshot);
        hash.Add(CubismEditor);
        hash.Add(RememberFaceTrackingOnStartup);
        hash.Add(ApplicationLanguage);
        hash.Add(ModelRenderingBackendPreference);
        hash.Add(GlobalBackground);
        return hash.ToHashCode();
    }
}
