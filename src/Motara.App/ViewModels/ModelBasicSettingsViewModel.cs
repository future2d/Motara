using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Models;
using Motara.App.Shell;
using Motara.ModelRuntime.Abstractions;

namespace Motara.App.ViewModels;

internal enum ModelBasicSettingsApplyResult
{
    Success,
    ValidationFailed,
    StorageFailure,
}

internal sealed record ModelBasicSettingsUpdate(
    MotaraModelConfiguration Configuration,
    string? PreviewSourcePath);

internal sealed record ModelBasicSettingsDocument(
    MotaraModelConfiguration Configuration,
    string? PreviewPath,
    ImmutableArray<ModelAuxiliaryAsset> Motions);

internal sealed class ModelBasicSettingsViewModel : INotifyPropertyChanged, IWorkspaceCloseGuard
{
    private readonly Func<ModelBasicSettingsUpdate, CancellationToken, Task> saveAsync;
    private readonly ILogger logger;
    private MotaraModelConfiguration baseline;
    private string? baselinePreviewPath;
    private string nickname;
    private string? previewPath;
    private ModelIdleMotionSelection idleMotion;
    private ModelLostTrackingIdleMotionSelection lostTrackingIdleMotion;
    private bool isCloseConfirmationVisible;

    internal ModelBasicSettingsViewModel(
        MotaraModelConfiguration configuration,
        string? previewPath,
        IEnumerable<ModelAuxiliaryAsset> motions,
        Func<ModelBasicSettingsUpdate, CancellationToken, Task> saveAsync,
        ILogger? logger = null)
    {
        baseline = configuration ?? throw new ArgumentNullException(nameof(configuration));
        baseline.Validate();
        this.previewPath = previewPath;
        baselinePreviewPath = previewPath;
        Motions = motions?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(motions));
        this.saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        this.logger = logger ?? NullLogger.Instance;
        nickname = configuration.Nickname ?? string.Empty;
        idleMotion = configuration.IdleMotion;
        lostTrackingIdleMotion = configuration.LostTrackingIdleMotion;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal ImmutableArray<ModelAuxiliaryAsset> Motions { get; }

    internal string Nickname
    {
        get => nickname;
        set => Set(ref nickname, value ?? string.Empty);
    }

    internal string? PreviewPath
    {
        get => previewPath;
        private set => Set(ref previewPath, value);
    }

    internal ModelIdleMotionSelection IdleMotion
    {
        get => idleMotion;
        set => Set(ref idleMotion, value ?? throw new ArgumentNullException(nameof(value)));
    }

    internal ModelLostTrackingIdleMotionSelection LostTrackingIdleMotion
    {
        get => lostTrackingIdleMotion;
        set => Set(ref lostTrackingIdleMotion, value ?? throw new ArgumentNullException(nameof(value)));
    }

    internal bool IsDirty => !CreateConfiguration().Equals(baseline)
        || !StringComparer.Ordinal.Equals(previewPath, baselinePreviewPath);

    internal bool IsCloseConfirmationVisible
    {
        get => isCloseConfirmationVisible;
        private set => Set(ref isCloseConfirmationVisible, value);
    }

    internal void SelectPreview(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PreviewPath = Path.GetFullPath(path);
    }

    internal void RestoreDefaults()
    {
        Nickname = string.Empty;
        IdleMotion = ModelIdleMotionSelection.Automatic;
        LostTrackingIdleMotion = ModelLostTrackingIdleMotionSelection.UseRegularIdle;
    }

    internal async Task<ModelBasicSettingsApplyResult> ApplyAsync(CancellationToken cancellationToken)
    {
        MotaraModelConfiguration configuration;
        try
        {
            configuration = CreateConfiguration();
            configuration.Validate();
            ValidateMotionSelection(configuration.IdleMotion.AssetId);
            ValidateMotionSelection(configuration.LostTrackingIdleMotion.AssetId);
        }
        catch (ArgumentException)
        {
            return ModelBasicSettingsApplyResult.ValidationFailed;
        }

        try
        {
            string? previewSource = StringComparer.Ordinal.Equals(previewPath, baselinePreviewPath)
                ? null
                : previewPath;
            await saveAsync(new ModelBasicSettingsUpdate(configuration, previewSource), cancellationToken)
                .ConfigureAwait(false);
            baseline = configuration;
            baselinePreviewPath = previewPath;
            Raise(nameof(IsDirty));
            ModelBasicSettingsLog.Applied(
                logger,
                configuration.ModelId,
                configuration.Nickname is not null,
                configuration.IdleMotion.Mode,
                configuration.LostTrackingIdleMotion.Mode,
                previewSource is not null);
            return ModelBasicSettingsApplyResult.Success;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            ModelBasicSettingsLog.ApplyFailed(logger, exception, configuration.ModelId);
            return ModelBasicSettingsApplyResult.StorageFailure;
        }
    }

    public Task<bool> RequestCloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsDirty) return Task.FromResult(true);
        IsCloseConfirmationVisible = true;
        return Task.FromResult(false);
    }

    internal void CancelClose() => IsCloseConfirmationVisible = false;

    internal void DiscardAndClose()
    {
        nickname = baseline.Nickname ?? string.Empty;
        idleMotion = baseline.IdleMotion;
        lostTrackingIdleMotion = baseline.LostTrackingIdleMotion;
        previewPath = baselinePreviewPath;
        IsCloseConfirmationVisible = false;
        Raise(nameof(Nickname));
        Raise(nameof(IdleMotion));
        Raise(nameof(LostTrackingIdleMotion));
        Raise(nameof(PreviewPath));
        Raise(nameof(IsDirty));
    }

    private MotaraModelConfiguration CreateConfiguration()
    {
        string trimmedNickname = nickname.Trim();
        return baseline with
        {
            Nickname = trimmedNickname.Length == 0 ? null : trimmedNickname,
            IdleMotion = idleMotion,
            LostTrackingIdleMotion = lostTrackingIdleMotion,
        };
    }

    private void ValidateMotionSelection(string? assetId)
    {
        if (assetId is not null
            && !Motions.Any(motion => StringComparer.Ordinal.Equals(motion.AssetId, assetId)))
        {
            throw new ArgumentException("The selected model motion no longer exists.");
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        Raise(nameof(IsDirty));
        return true;
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal static partial class ModelBasicSettingsLog
{
    [LoggerMessage(6550, LogLevel.Information,
        "Model basic settings applied for {ModelId}; hasNickname={HasNickname}, idle={IdleMode}, lostIdle={LostIdleMode}, previewChanged={PreviewChanged}")]
    internal static partial void Applied(
        ILogger logger,
        string modelId,
        bool hasNickname,
        ModelIdleMotionMode idleMode,
        ModelLostTrackingIdleMotionMode lostIdleMode,
        bool previewChanged);

    [LoggerMessage(6551, LogLevel.Warning,
        "Model basic settings save failed for {ModelId}")]
    internal static partial void ApplyFailed(ILogger logger, Exception exception, string modelId);
}
