using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Output.CubismEditor;
using Motara.Persistence;

namespace Motara.App.ViewModels;

/// <summary>Edits and validates the local Cubism Editor output connection preferences.</summary>
internal sealed class CubismEditorOutputSettingsWorkspaceViewModel : INotifyPropertyChanged
{
    private readonly Func<CubismEditorOutputSettings, CancellationToken, Task> applyAsync;
    private readonly Action close;
    private readonly ILogger<CubismEditorOutputSettingsWorkspaceViewModel> logger;
    private string endpointText;
    private bool startOnLaunch;
    private string? validationResourceKey;
    private bool isApplying;

    public CubismEditorOutputSettingsWorkspaceViewModel(
        CubismEditorOutputSettings settings,
        Func<CubismEditorOutputSettings, CancellationToken, Task> applyAsync,
        Action close,
        ILogger<CubismEditorOutputSettingsWorkspaceViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.applyAsync = applyAsync ?? throw new ArgumentNullException(nameof(applyAsync));
        this.close = close ?? throw new ArgumentNullException(nameof(close));
        this.logger = logger ?? NullLogger<CubismEditorOutputSettingsWorkspaceViewModel>.Instance;
        endpointText = settings.Endpoint;
        startOnLaunch = settings.StartOnLaunch;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string EndpointText
    {
        get => endpointText;
        set => Set(ref endpointText, value ?? string.Empty, nameof(EndpointText));
    }

    public bool StartOnLaunch
    {
        get => startOnLaunch;
        set => Set(ref startOnLaunch, value, nameof(StartOnLaunch));
    }

    public string? ValidationResourceKey
    {
        get => validationResourceKey;
        private set => Set(ref validationResourceKey, value, nameof(ValidationResourceKey));
    }

    public bool IsApplying
    {
        get => isApplying;
        private set => Set(ref isApplying, value, nameof(IsApplying));
    }

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        if (IsApplying)
        {
            return;
        }

        CubismEditorOutputSettings? candidate = CreateCandidate();
        if (candidate is null)
        {
            return;
        }

        IsApplying = true;
        bool applied = false;
        try
        {
            await applyAsync(candidate, cancellationToken).ConfigureAwait(true);
            ValidationResourceKey = null;
            CubismEditorOutputSettingsWorkspaceLog.Applied(logger, candidate.StartOnLaunch);
            applied = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ValidationResourceKey = "Workspace.CubismEditor.ApplyFailed";
            CubismEditorOutputSettingsWorkspaceLog.ApplyFailed(logger, exception);
        }
        finally
        {
            IsApplying = false;
        }

        if (applied)
        {
            close();
        }
    }

    public void Cancel() => close();

    private CubismEditorOutputSettings? CreateCandidate()
    {
        ValidationResourceKey = null;
        if (!Uri.TryCreate(EndpointText.Trim(), UriKind.Absolute, out Uri? endpoint))
        {
            ValidationResourceKey = "Workspace.CubismEditor.InvalidEndpoint";
            CubismEditorOutputSettingsWorkspaceLog.ValidationFailed(logger);
            return null;
        }

        try
        {
            var options = new CubismEditorConnectionOptions(endpoint);
            return new CubismEditorOutputSettings(options.Endpoint.AbsoluteUri, StartOnLaunch);
        }
        catch (ArgumentException)
        {
            ValidationResourceKey = "Workspace.CubismEditor.InvalidEndpoint";
            CubismEditorOutputSettingsWorkspaceLog.ValidationFailed(logger);
            return null;
        }
    }

    private bool Set<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

internal static partial class CubismEditorOutputSettingsWorkspaceLog
{
    [LoggerMessage(6715, LogLevel.Information, "Cubism Editor output settings applied; start on launch {StartOnLaunch}")]
    internal static partial void Applied(ILogger logger, bool startOnLaunch);

    [LoggerMessage(6716, LogLevel.Warning, "Cubism Editor output settings validation failed")]
    internal static partial void ValidationFailed(ILogger logger);

    [LoggerMessage(6717, LogLevel.Warning, "Cubism Editor output settings could not be applied")]
    internal static partial void ApplyFailed(ILogger logger, Exception exception);
}
