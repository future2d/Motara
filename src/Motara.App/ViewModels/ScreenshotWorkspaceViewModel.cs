using System.ComponentModel;
using System.Globalization;
using Motara.Persistence;

namespace Motara.App.ViewModels;

internal sealed record ScreenshotCaptureRequest(ScreenshotSettings Settings);

internal enum ScreenshotResolutionPreset
{
    Hd720,
    FullHd1080,
    Qhd2K,
    Uhd4K,
    Uhd8K,
    Uhd16K,
}

internal sealed class ScreenshotWorkspaceViewModel : INotifyPropertyChanged
{
    internal const int HighResolutionWarningWidth = 7_680;
    internal const int HighResolutionWarningHeight = 4_320;

    private readonly Func<ScreenshotSettings, CancellationToken, Task> saveAsync;
    private readonly Action<ScreenshotCaptureRequest> requestCapture;
    private readonly Action close;
    private readonly Action? openFolder;
    private int countdownSeconds;
    private bool useTransparentBackground;
    private bool useCustomResolution;
    private string widthText;
    private string heightText;
    private ScreenshotFramingMode framingMode;
    private string? validationResourceKey;

    public ScreenshotWorkspaceViewModel(
        ScreenshotSettings settings,
        Func<ScreenshotSettings, CancellationToken, Task> saveAsync,
        Action<ScreenshotCaptureRequest> requestCapture,
        Action close,
        Action? openFolder = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ScreenshotSettings.Validate(settings);
        this.saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        this.requestCapture = requestCapture ?? throw new ArgumentNullException(nameof(requestCapture));
        this.close = close ?? throw new ArgumentNullException(nameof(close));
        this.openFolder = openFolder;
        countdownSeconds = settings.CountdownSeconds;
        useTransparentBackground = settings.UseTransparentBackground;
        useCustomResolution = settings.UseCustomResolution;
        widthText = settings.WidthPixels.ToString(CultureInfo.InvariantCulture);
        heightText = settings.HeightPixels.ToString(CultureInfo.InvariantCulture);
        framingMode = settings.FramingMode;
        SaveDirectory = settings.SaveDirectory;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int CountdownSeconds
    {
        get => countdownSeconds;
        set => Set(ref countdownSeconds, Math.Clamp(value, 0, 10), nameof(CountdownSeconds));
    }

    public bool UseTransparentBackground
    {
        get => useTransparentBackground;
        set => Set(ref useTransparentBackground, value, nameof(UseTransparentBackground));
    }

    public bool UseCustomResolution
    {
        get => useCustomResolution;
        set
        {
            if (Set(ref useCustomResolution, value, nameof(UseCustomResolution)))
            {
                OnPropertyChanged(nameof(IsHighResolutionWarningVisible));
            }
        }
    }

    public string WidthText
    {
        get => widthText;
        set
        {
            if (Set(ref widthText, value, nameof(WidthText)))
            {
                OnPropertyChanged(nameof(IsHighResolutionWarningVisible));
            }
        }
    }

    public string HeightText
    {
        get => heightText;
        set
        {
            if (Set(ref heightText, value, nameof(HeightText)))
            {
                OnPropertyChanged(nameof(IsHighResolutionWarningVisible));
            }
        }
    }

    public ScreenshotFramingMode FramingMode
    {
        get => framingMode;
        set => Set(ref framingMode, value, nameof(FramingMode));
    }

    public bool IsHighResolutionWarningVisible => UseCustomResolution
        && int.TryParse(WidthText, NumberStyles.None, CultureInfo.InvariantCulture, out int width)
        && int.TryParse(HeightText, NumberStyles.None, CultureInfo.InvariantCulture, out int height)
        && (width >= HighResolutionWarningWidth || height >= HighResolutionWarningHeight);

    public string? ValidationResourceKey
    {
        get => validationResourceKey;
        private set => Set(ref validationResourceKey, value, nameof(ValidationResourceKey));
    }

    internal string? SaveDirectory { get; }

    public bool CanOpenFolder => openFolder is not null;

    public void OpenFolder() => openFolder?.Invoke();

    public void ApplyPreset(ScreenshotResolutionPreset preset)
    {
        (int width, int height) = preset switch
        {
            ScreenshotResolutionPreset.Hd720 => (1280, 720),
            ScreenshotResolutionPreset.FullHd1080 => (1920, 1080),
            ScreenshotResolutionPreset.Qhd2K => (2560, 1440),
            ScreenshotResolutionPreset.Uhd4K => (3840, 2160),
            ScreenshotResolutionPreset.Uhd8K => (7680, 4320),
            ScreenshotResolutionPreset.Uhd16K => (15360, 8640),
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };
        WidthText = width.ToString(CultureInfo.InvariantCulture);
        HeightText = height.ToString(CultureInfo.InvariantCulture);
        ValidationResourceKey = null;
    }

    public async Task CaptureAsync(CancellationToken cancellationToken)
    {
        ScreenshotSettings? candidate = CreateCandidate();
        if (candidate is null)
        {
            return;
        }

        await PersistAndRequestAsync(candidate, cancellationToken).ConfigureAwait(true);
    }

    public void Cancel() => close();

    private ScreenshotSettings? CreateCandidate()
    {
        ValidationResourceKey = null;
        if (!int.TryParse(WidthText, NumberStyles.None, CultureInfo.InvariantCulture, out int width)
            || !int.TryParse(HeightText, NumberStyles.None, CultureInfo.InvariantCulture, out int height))
        {
            ValidationResourceKey = "Workspace.Screenshot.InvalidResolution";
            return null;
        }

        var candidate = new ScreenshotSettings(
            CountdownSeconds,
            UseTransparentBackground,
            UseCustomResolution,
            width,
            height,
            FramingMode,
            SaveDirectory);
        try
        {
            ScreenshotSettings.Validate(candidate);
            return candidate;
        }
        catch (ArgumentException)
        {
            ValidationResourceKey = "Workspace.Screenshot.InvalidResolution";
            return null;
        }
    }

    private async Task PersistAndRequestAsync(
        ScreenshotSettings candidate,
        CancellationToken cancellationToken)
    {
        await saveAsync(candidate, cancellationToken).ConfigureAwait(true);
        close();
        requestCapture(new ScreenshotCaptureRequest(candidate));
    }

    private bool Set<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
