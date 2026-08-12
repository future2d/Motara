using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Rendering;
using Motara.Media;

namespace Motara.App.ViewModels;

internal sealed class CompositionVideoOutputSettingsViewModel : INotifyPropertyChanged
{
    private readonly Func<CompositionVideoOutputController.Settings, CancellationToken, Task> applyAsync;
    private readonly Action close;
    private readonly ILogger logger;
    private string name;
    private string width;
    private string height;
    private string framesPerSecond;
    private string? validation;

    internal CompositionVideoOutputSettingsViewModel(
        VideoSignalProtocol protocol,
        CompositionVideoOutputController.Settings settings,
        Func<CompositionVideoOutputController.Settings, CancellationToken, Task> applyAsync,
        Action close,
        ILogger<CompositionVideoOutputSettingsViewModel>? logger = null)
    {
        Protocol = protocol;
        this.applyAsync = applyAsync ?? throw new ArgumentNullException(nameof(applyAsync));
        this.close = close ?? throw new ArgumentNullException(nameof(close));
        this.logger = logger ?? NullLogger<CompositionVideoOutputSettingsViewModel>.Instance;
        name = settings.Name;
        width = settings.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
        height = settings.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
        framesPerSecond = settings.FramesPerSecond.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal VideoSignalProtocol Protocol { get; }
    internal string Name { get => name; set => Set(ref name, value); }
    internal string Width { get => width; set => Set(ref width, value); }
    internal string Height { get => height; set => Set(ref height, value); }
    internal string FramesPerSecond { get => framesPerSecond; set => Set(ref framesPerSecond, value); }
    internal string? Validation { get => validation; private set => Set(ref validation, value); }

    internal async Task ApplyAsync(CancellationToken cancellationToken)
    {
        Validation = null;
        if (string.IsNullOrWhiteSpace(Name)
            || !int.TryParse(Width, out int parsedWidth) || parsedWidth < 0
            || !int.TryParse(Height, out int parsedHeight) || parsedHeight < 0
            || !double.TryParse(FramesPerSecond, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double fps)
            || fps < 1 || fps > 240)
        {
            Validation = "Workspace.VideoOutput.Invalid";
            return;
        }

        try
        {
            await applyAsync(
                new CompositionVideoOutputController.Settings(Name.Trim(), parsedWidth, parsedHeight, fps),
                cancellationToken).ConfigureAwait(true);
            close();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            Validation = "Workspace.VideoOutput.ApplyFailed";
            CompositionVideoOutputSettingsLog.ApplyFailed(logger, Protocol, exception.GetType().Name);
        }
    }

    internal void Cancel() => close();

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

internal static partial class CompositionVideoOutputSettingsLog
{
    [LoggerMessage(6862, LogLevel.Warning, "Video output settings apply failed for {Protocol}: {ErrorType}")]
    internal static partial void ApplyFailed(ILogger logger, VideoSignalProtocol protocol, string errorType);
}
