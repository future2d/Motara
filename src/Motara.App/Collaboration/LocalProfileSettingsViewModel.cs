using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Profile;

namespace Motara.App.Collaboration;

internal sealed class LocalProfileSettingsViewModel : INotifyPropertyChanged
{
    private readonly CollaborationWorkspaceViewModel workspace;
    private readonly Action close;
    private readonly ILogger<LocalProfileSettingsViewModel> logger;
    private string displayName;
    private bool isBusy;
    private string? validationResourceKey;

    internal LocalProfileSettingsViewModel(
        CollaborationWorkspaceViewModel workspace,
        Action close,
        ILogger<LocalProfileSettingsViewModel>? logger = null)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.close = close ?? throw new ArgumentNullException(nameof(close));
        this.logger = logger ?? NullLogger<LocalProfileSettingsViewModel>.Instance;
        displayName = workspace.LocalProfile?.DisplayName ?? string.Empty;
        DeviceId = workspace.LocalIdentity?.DeviceId.Value ?? string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal string DisplayName
    {
        get => displayName;
        set => Set(ref displayName, value);
    }

    internal string DeviceId { get; }

    internal bool IsBusy
    {
        get => isBusy;
        private set => Set(ref isBusy, value);
    }

    internal string? ValidationResourceKey
    {
        get => validationResourceKey;
        private set => Set(ref validationResourceKey, value);
    }

    internal async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        ValidationResourceKey = null;
        try
        {
            _ = LocalCollaborationProfile.NormalizeDisplayName(DisplayName);
        }
        catch (ArgumentException)
        {
            ValidationResourceKey = "Workspace.Collaboration.Profile.InvalidName";
            LocalProfileSettingsEvents.Rejected(logger, "invalid-name");
            return;
        }

        IsBusy = true;
        try
        {
            await workspace.SaveLocalDisplayNameAsync(DisplayName, cancellationToken);
            LocalProfileSettingsEvents.Saved(logger);
            close();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ValidationResourceKey = "Workspace.Collaboration.Profile.SaveFailed";
            LocalProfileSettingsEvents.Failed(logger, exception.GetType().Name);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal void Cancel() => close();

    internal void RecordIdentityCodeCopyResult(bool succeeded, string? errorType) =>
        LocalProfileSettingsEvents.IdentityCodeCopyCompleted(
            logger,
            succeeded,
            errorType ?? "none");

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal static partial class LocalProfileSettingsEvents
{
    [LoggerMessage(8093, LogLevel.Information, "Local collaboration profile settings saved")]
    internal static partial void Saved(ILogger logger);

    [LoggerMessage(8094, LogLevel.Information,
        "Local collaboration profile settings rejected; reason={Reason}")]
    internal static partial void Rejected(ILogger logger, string reason);

    [LoggerMessage(8095, LogLevel.Warning,
        "Local collaboration profile settings failed; errorType={ErrorType}")]
    internal static partial void Failed(ILogger logger, string errorType);

    [LoggerMessage(8096, LogLevel.Information,
        "Local collaboration identity code copy completed; succeeded={Succeeded}; error={ErrorType}")]
    internal static partial void IdentityCodeCopyCompleted(
        ILogger logger,
        bool succeeded,
        string errorType);
}
