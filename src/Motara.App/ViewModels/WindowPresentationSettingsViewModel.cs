using System.ComponentModel;
using System.Globalization;
using Motara.Persistence;

namespace Motara.App.ViewModels;

public sealed class WindowPresentationSettingsViewModel : INotifyPropertyChanged
{
    private readonly MainWindowViewModel shell;
    private string widthText;
    private string heightText;
    private double contentScale;
    private ContentScaleMode contentScaleMode;
    private FrameRateMode frameRateMode;
    private string? statusResourceKey;

    public WindowPresentationSettingsViewModel(MainWindowViewModel shell)
    {
        this.shell = shell ?? throw new ArgumentNullException(nameof(shell));
        widthText = shell.WindowWidthPixels.ToString(CultureInfo.InvariantCulture);
        heightText = shell.WindowHeightPixels.ToString(CultureInfo.InvariantCulture);
        contentScale = shell.ContentScale;
        contentScaleMode = shell.ContentScaleMode;
        frameRateMode = shell.FrameRateMode;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string WidthText
    {
        get => widthText;
        set => Set(ref widthText, value, nameof(WidthText));
    }

    public string HeightText
    {
        get => heightText;
        set => Set(ref heightText, value, nameof(HeightText));
    }

    public double ContentScale
    {
        get => contentScale;
        set => Set(ref contentScale, value, nameof(ContentScale));
    }

    public ContentScaleMode ContentScaleMode
    {
        get => contentScaleMode;
        set => Set(ref contentScaleMode, value, nameof(ContentScaleMode));
    }

    public FrameRateMode FrameRateMode
    {
        get => frameRateMode;
        set => Set(ref frameRateMode, value, nameof(FrameRateMode));
    }

    public string? StatusResourceKey
    {
        get => statusResourceKey;
        private set => Set(ref statusResourceKey, value, nameof(StatusResourceKey));
    }

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        if (!int.TryParse(WidthText, NumberStyles.None, CultureInfo.InvariantCulture, out int width)
            || !int.TryParse(HeightText, NumberStyles.None, CultureInfo.InvariantCulture, out int height)
            || width <= 0 || height <= 0)
        {
            StatusResourceKey = "Workspace.WindowPresentation.InvalidSize";
            return;
        }

        await shell.ApplyWindowPresentationAsync(
            width,
            height,
            ContentScaleMode,
            ContentScale,
            FrameRateMode,
            cancellationToken).ConfigureAwait(true);
        StatusResourceKey = "Workspace.WindowPresentation.Applied";
    }

    public void RestoreDefaults()
    {
        WidthText = UiSettings.DefaultWindowWidthPixels.ToString(CultureInfo.InvariantCulture);
        HeightText = UiSettings.DefaultWindowHeightPixels.ToString(CultureInfo.InvariantCulture);
        ContentScale = UiSettings.DefaultContentScale;
        ContentScaleMode = ContentScaleMode.Automatic;
        FrameRateMode = FrameRateMode.FramesPerSecond60;
        StatusResourceKey = null;
    }

    private void Set<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
