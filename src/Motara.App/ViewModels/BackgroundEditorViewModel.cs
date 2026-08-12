using System.Collections.Immutable;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Backgrounds;
using Motara.Media;
using Motara.Persistence;
using Motara.Scene;

namespace Motara.App.ViewModels;

internal enum BackgroundEditorScopeKind
{
    Global = 0,
    Scene = 1,
}

internal readonly record struct BackgroundEditorScope(
    BackgroundEditorScopeKind Kind,
    SceneId? SceneId)
{
    internal static BackgroundEditorScope Global { get; } =
        new(BackgroundEditorScopeKind.Global, null);

    internal static BackgroundEditorScope ForScene(SceneId sceneId) =>
        new(BackgroundEditorScopeKind.Scene, sceneId);
}

internal enum BackgroundEditorErrorCode
{
    None = 0,
    InvalidDefinition = 1,
    ImportFailed = 2,
    SaveFailed = 3,
    RecentAssetUnavailable = 4,
}

internal sealed class BackgroundEditorViewModel : INotifyPropertyChanged
{
    private readonly IBackgroundAssetStore assetStore;
    private readonly Func<BackgroundDefinition, CancellationToken, Task> saveAsync;
    private readonly Action close;
    private readonly ILogger<BackgroundEditorViewModel> logger;
    private readonly IBackgroundRecentAssetStore recentAssetStore;
    private BackgroundDefinition baseline;
    private BackgroundKind kind;
    private string solidColor;
    private string? imageAssetId;
    private string? videoAssetId;
    private BackgroundLayoutMode layout;
    private BackgroundVideoOptions videoOptions;
    private string? selectedImageDisplayName;
    private string? selectedVideoDisplayName;
    private BackgroundEditorErrorCode errorCode;
    private bool isApplying;
    private ImmutableArray<BackgroundRecentAsset> recentImages = [];
    private ImmutableArray<BackgroundRecentAsset> recentVideos = [];
    private readonly VideoSignalRegistry? signalRegistry;
    private VideoSignalSourceSelection? signalSource;
    private ImmutableDictionary<VideoSignalProtocol, ImmutableArray<VideoSignalSourceDescriptor>> signalSourcesByProtocol =
        ImmutableDictionary<VideoSignalProtocol, ImmutableArray<VideoSignalSourceDescriptor>>.Empty;
    private ImmutableDictionary<VideoSignalProtocol, string> signalSourceErrorsByProtocol =
        ImmutableDictionary<VideoSignalProtocol, string>.Empty;

    internal BackgroundEditorViewModel(
        BackgroundEditorScope scope,
        BackgroundDefinition current,
        IBackgroundAssetStore assetStore,
        Func<BackgroundDefinition, CancellationToken, Task> saveAsync,
        Action close,
        ILogger<BackgroundEditorViewModel>? logger = null,
        IBackgroundRecentAssetStore? recentAssetStore = null,
        VideoSignalRegistry? signalRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        Scope = scope;
        baseline = current;
        this.assetStore = assetStore ?? throw new ArgumentNullException(nameof(assetStore));
        this.saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        this.close = close ?? throw new ArgumentNullException(nameof(close));
        this.logger = logger ?? NullLogger<BackgroundEditorViewModel>.Instance;
        this.recentAssetStore = recentAssetStore ?? NullBackgroundRecentAssetStore.Instance;
        this.signalRegistry = signalRegistry;
        kind = current.Kind;
        solidColor = current.SolidColor;
        imageAssetId = current.ImageAssetId;
        videoAssetId = current.VideoAssetId;
        layout = current.Layout;
        videoOptions = current.VideoOptions;
        signalSource = current.SignalSource;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal BackgroundEditorScope Scope { get; }

    internal BackgroundKind Kind
    {
        get => kind;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            SetField(ref kind, value, nameof(Kind));
        }
    }

    internal string SolidColor
    {
        get => solidColor;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            SetField(ref solidColor, value, nameof(SolidColor));
        }
    }

    internal string? ImageAssetId
    {
        get => imageAssetId;
        private set => SetField(ref imageAssetId, value, nameof(ImageAssetId));
    }

    internal string? VideoAssetId
    {
        get => videoAssetId;
        private set => SetField(ref videoAssetId, value, nameof(VideoAssetId));
    }

    internal BackgroundLayoutMode Layout
    {
        get => layout;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            SetField(ref layout, value, nameof(Layout));
        }
    }

    internal BackgroundVideoOptions VideoOptions
    {
        get => videoOptions;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            SetField(ref videoOptions, value, nameof(VideoOptions));
        }
    }

    internal VideoSignalSourceSelection? SignalSource
    {
        get => signalSource;
        private set => SetField(ref signalSource, value, nameof(SignalSource));
    }

    internal IReadOnlyList<VideoSignalSourceDescriptor> SignalSources =>
        signalSourcesByProtocol.Values.SelectMany(static sources => sources).ToArray();

    internal IReadOnlyList<VideoSignalSourceDescriptor> GetSignalSources(VideoSignalProtocol protocol) =>
        signalSourcesByProtocol.TryGetValue(protocol, out ImmutableArray<VideoSignalSourceDescriptor> sources)
            ? sources
            : [];

    internal string? GetSignalSourceError(VideoSignalProtocol protocol) =>
        signalSourceErrorsByProtocol.TryGetValue(protocol, out string? error) ? error : null;

    internal void SelectSignalSource(VideoSignalSourceDescriptor source)
    {
        ArgumentNullException.ThrowIfNull(source);
        SignalSource = new VideoSignalSourceSelection(source.Protocol, source.Id);
        Kind = BackgroundKind.Signal;
        ErrorCode = BackgroundEditorErrorCode.None;
    }

    internal async Task LoadSignalSourcesAsync(
        VideoSignalProtocol protocol,
        CancellationToken cancellationToken)
    {
        if (signalRegistry is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<VideoSignalSourceDescriptor> discovered =
                await signalRegistry.GetRequiredAdapter(protocol)
                    .DiscoverAsync(cancellationToken)
                    .ConfigureAwait(false);
            ImmutableArray<VideoSignalSourceDescriptor> sources = discovered
                .Where(source => source.Protocol == protocol)
                .ToImmutableArray();
            signalSourcesByProtocol = signalSourcesByProtocol.SetItem(protocol, sources);
            signalSourceErrorsByProtocol = signalSourceErrorsByProtocol.Remove(protocol);
            OnPropertyChanged(nameof(SignalSources));
            BackgroundEditorLog.SignalSourcesLoaded(logger, protocol, sources.Length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            signalSourceErrorsByProtocol = signalSourceErrorsByProtocol.SetItem(protocol, exception.GetType().Name);
            OnPropertyChanged(nameof(SignalSources));
            BackgroundEditorLog.SignalSourcesLoadFailed(logger, protocol, exception.GetType().Name);
        }
    }

    internal string? SelectedImageDisplayName => selectedImageDisplayName;

    internal string? SelectedVideoDisplayName => selectedVideoDisplayName;

    internal BackgroundEditorErrorCode ErrorCode
    {
        get => errorCode;
        private set => SetField(ref errorCode, value, nameof(ErrorCode), notifyHasChanges: false);
    }

    internal bool IsApplying
    {
        get => isApplying;
        private set => SetField(ref isApplying, value, nameof(IsApplying), notifyHasChanges: false);
    }

    internal bool HasChanges => kind != baseline.Kind
        || !StringComparer.Ordinal.Equals(solidColor, baseline.SolidColor)
        || !StringComparer.Ordinal.Equals(imageAssetId, baseline.ImageAssetId)
        || !StringComparer.Ordinal.Equals(videoAssetId, baseline.VideoAssetId)
        || signalSource != baseline.SignalSource
        || videoOptions != baseline.VideoOptions
        || layout != baseline.Layout;

    internal IReadOnlyList<BackgroundRecentAsset> RecentImages => recentImages;

    internal IReadOnlyList<BackgroundRecentAsset> RecentVideos => recentVideos;

    internal async Task LoadRecentAssetsAsync(CancellationToken cancellationToken)
    {
        try
        {
            BackgroundRecentAssets recent = await recentAssetStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            SetRecentAssets(recent);
            SetSelectedDisplayNames(recent);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            BackgroundEditorLog.RecentLoadFailed(logger, exception.GetType().Name);
        }
    }

    internal async Task ImportImageAsync(string sourcePath, CancellationToken cancellationToken)
    {
        try
        {
            BackgroundAssetImportResult imported = await assetStore.ImportAsync(
                sourcePath,
                cancellationToken);
            ImageAssetId = imported.AssetId;
            VideoAssetId = null;
            selectedImageDisplayName = Path.GetFileName(sourcePath);
            OnPropertyChanged(nameof(SelectedImageDisplayName));
            Kind = BackgroundKind.Image;
            ErrorCode = BackgroundEditorErrorCode.None;
            await RememberRecentAsync(BackgroundRecentAssetKind.Image, imported.AssetId, sourcePath, cancellationToken).ConfigureAwait(false);
            BackgroundEditorLog.ImageSelected(logger, imported.AssetId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ErrorCode = BackgroundEditorErrorCode.ImportFailed;
            BackgroundEditorLog.OperationFailed(logger, "Import", exception.GetType().Name);
            throw;
        }
    }

    internal async Task ImportVideoAsync(string sourcePath, CancellationToken cancellationToken)
    {
        try
        {
            BackgroundAssetImportResult imported = await assetStore.ImportVideoAsync(sourcePath, cancellationToken);
            VideoAssetId = imported.AssetId;
            selectedVideoDisplayName = Path.GetFileName(sourcePath);
            OnPropertyChanged(nameof(SelectedVideoDisplayName));
            ImageAssetId = null;
            Kind = BackgroundKind.Video;
            ErrorCode = BackgroundEditorErrorCode.None;
            await RememberRecentAsync(BackgroundRecentAssetKind.Video, imported.AssetId, sourcePath, cancellationToken).ConfigureAwait(false);
            BackgroundEditorLog.VideoSelected(logger, imported.AssetId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            ErrorCode = BackgroundEditorErrorCode.ImportFailed;
            BackgroundEditorLog.OperationFailed(logger, "ImportVideo", exception.GetType().Name);
            throw;
        }
    }

    internal async Task SelectRecentAssetAsync(
        BackgroundRecentAssetKind recentKind,
        BackgroundRecentAsset asset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        string managedPath;
        try
        {
            managedPath = recentKind == BackgroundRecentAssetKind.Image
                ? assetStore.GetManagedPath(asset.AssetId)
                : assetStore.GetManagedVideoPath(asset.AssetId);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            await RemoveRecentAssetAsync(recentKind, asset.AssetId, cancellationToken).ConfigureAwait(false);
            ErrorCode = BackgroundEditorErrorCode.RecentAssetUnavailable;
            return;
        }

        if (!File.Exists(managedPath))
        {
            await RemoveRecentAssetAsync(recentKind, asset.AssetId, cancellationToken).ConfigureAwait(false);
            ErrorCode = BackgroundEditorErrorCode.RecentAssetUnavailable;
            return;
        }

        if (recentKind == BackgroundRecentAssetKind.Image)
        {
            ImageAssetId = asset.AssetId;
            selectedImageDisplayName = asset.DisplayName;
            OnPropertyChanged(nameof(SelectedImageDisplayName));
            VideoAssetId = null;
            Kind = BackgroundKind.Image;
        }
        else
        {
            VideoAssetId = asset.AssetId;
            selectedVideoDisplayName = asset.DisplayName;
            OnPropertyChanged(nameof(SelectedVideoDisplayName));
            ImageAssetId = null;
            Kind = BackgroundKind.Video;
        }

        ErrorCode = BackgroundEditorErrorCode.None;
        await RememberRecentAsync(recentKind, asset.AssetId, asset.DisplayName, cancellationToken).ConfigureAwait(false);
    }

    internal async Task ApplyAsync(CancellationToken cancellationToken)
    {
        if (IsApplying)
        {
            return;
        }

        BackgroundDefinition definition;
        try
        {
            definition = Kind switch
            {
                BackgroundKind.Solid => BackgroundDefinition.Solid(SolidColor),
                BackgroundKind.Image => BackgroundDefinition.Image(ImageAssetId ?? throw new ArgumentException("An image asset is required."), Layout),
                BackgroundKind.Video => BackgroundDefinition.Video(VideoAssetId ?? throw new ArgumentException("A video asset is required."), Layout, VideoOptions),
                BackgroundKind.Signal => BackgroundDefinition.Signal(SignalSource ?? throw new ArgumentException("A signal source is required."), Layout),
                _ => throw new InvalidOperationException("The background kind is unsupported."),
            };
        }
        catch (ArgumentException exception)
        {
            ErrorCode = BackgroundEditorErrorCode.InvalidDefinition;
            BackgroundEditorLog.OperationFailed(logger, "Validate", exception.GetType().Name);
            throw;
        }

        IsApplying = true;
        try
        {
            await saveAsync(definition, cancellationToken);
            baseline = definition;
            ResetDraft(definition);
            ErrorCode = BackgroundEditorErrorCode.None;
            BackgroundEditorLog.Applied(logger, Scope.Kind, definition.Kind);
            close();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ErrorCode = BackgroundEditorErrorCode.SaveFailed;
            BackgroundEditorLog.OperationFailed(logger, "Save", exception.GetType().Name);
            throw;
        }
        finally
        {
            IsApplying = false;
        }
    }

    internal void Cancel()
    {
        ResetDraft(baseline);
        ErrorCode = BackgroundEditorErrorCode.None;
        BackgroundEditorLog.Cancelled(logger, Scope.Kind);
        close();
    }

    internal void RestoreDefault()
    {
        ResetDraft(BackgroundDefinition.Solid(UiSettings.DefaultCanvasBackgroundColor));
        ErrorCode = BackgroundEditorErrorCode.None;
        BackgroundEditorLog.DefaultRestored(logger, Scope.Kind);
    }

    private void ResetDraft(BackgroundDefinition definition)
    {
        kind = definition.Kind;
        solidColor = definition.SolidColor;
        imageAssetId = definition.ImageAssetId;
        videoAssetId = definition.VideoAssetId;
        videoOptions = definition.VideoOptions;
        signalSource = definition.SignalSource;
        selectedImageDisplayName = recentImages.FirstOrDefault(item => StringComparer.Ordinal.Equals(item.AssetId, imageAssetId))?.DisplayName;
        selectedVideoDisplayName = recentVideos.FirstOrDefault(item => StringComparer.Ordinal.Equals(item.AssetId, videoAssetId))?.DisplayName;
        layout = definition.Layout;
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(SolidColor));
        OnPropertyChanged(nameof(ImageAssetId));
        OnPropertyChanged(nameof(VideoAssetId));
        OnPropertyChanged(nameof(Layout));
        OnPropertyChanged(nameof(VideoOptions));
        OnPropertyChanged(nameof(SignalSource));
        OnPropertyChanged(nameof(SelectedImageDisplayName));
        OnPropertyChanged(nameof(SelectedVideoDisplayName));
        OnPropertyChanged(nameof(HasChanges));
    }

    private void SetField<T>(
        ref T field,
        T value,
        string propertyName,
        bool notifyHasChanges = true)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
        if (notifyHasChanges)
        {
            OnPropertyChanged(nameof(HasChanges));
        }
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private async Task RememberRecentAsync(
        BackgroundRecentAssetKind kind,
        string assetId,
        string displayName,
        CancellationToken cancellationToken)
    {
        try
        {
            BackgroundRecentAssets recent = await recentAssetStore.RememberAsync(
                kind,
                assetId,
                displayName,
                cancellationToken).ConfigureAwait(false);
            SetRecentAssets(recent);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            BackgroundEditorLog.RecentSaveFailed(logger, exception.GetType().Name);
        }
    }

    private async Task RemoveRecentAssetAsync(
        BackgroundRecentAssetKind kind,
        string assetId,
        CancellationToken cancellationToken)
    {
        try
        {
            BackgroundRecentAssets recent = await recentAssetStore.RemoveAsync(
                kind,
                assetId,
                cancellationToken).ConfigureAwait(false);
            SetRecentAssets(recent);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            BackgroundEditorLog.RecentSaveFailed(logger, exception.GetType().Name);
            SetRecentAssets(kind == BackgroundRecentAssetKind.Image
                ? new BackgroundRecentAssets(
                    recentImages.Where(item => !StringComparer.Ordinal.Equals(item.AssetId, assetId)).ToImmutableArray(),
                    recentVideos)
                : new BackgroundRecentAssets(
                    recentImages,
                    recentVideos.Where(item => !StringComparer.Ordinal.Equals(item.AssetId, assetId)).ToImmutableArray()));
        }
    }

    private void SetRecentAssets(BackgroundRecentAssets recent)
    {
        recentImages = recent.Images;
        recentVideos = recent.Videos;
        OnPropertyChanged(nameof(RecentImages));
        OnPropertyChanged(nameof(RecentVideos));
        SetSelectedDisplayNames(recent);
    }

    private void SetSelectedDisplayNames(BackgroundRecentAssets recent)
    {
        string? imageName = recent.Images.FirstOrDefault(item => StringComparer.Ordinal.Equals(item.AssetId, imageAssetId))?.DisplayName;
        string? videoName = recent.Videos.FirstOrDefault(item => StringComparer.Ordinal.Equals(item.AssetId, videoAssetId))?.DisplayName;
        if (!StringComparer.Ordinal.Equals(selectedImageDisplayName, imageName))
        {
            selectedImageDisplayName = imageName;
            OnPropertyChanged(nameof(SelectedImageDisplayName));
        }
        if (!StringComparer.Ordinal.Equals(selectedVideoDisplayName, videoName))
        {
            selectedVideoDisplayName = videoName;
            OnPropertyChanged(nameof(SelectedVideoDisplayName));
        }
    }
}

internal sealed class NullBackgroundRecentAssetStore : IBackgroundRecentAssetStore
{
    internal static NullBackgroundRecentAssetStore Instance { get; } = new();

    public Task<BackgroundRecentAssets> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(BackgroundRecentAssets.Empty);

    public Task<BackgroundRecentAssets> RememberAsync(BackgroundRecentAssetKind kind, string assetId, string displayName, CancellationToken cancellationToken) =>
        Task.FromResult(BackgroundRecentAssets.Empty);

    public Task<BackgroundRecentAssets> RemoveAsync(BackgroundRecentAssetKind kind, string assetId, CancellationToken cancellationToken) =>
        Task.FromResult(BackgroundRecentAssets.Empty);
}

internal static partial class BackgroundEditorLog
{
    [LoggerMessage(6775, LogLevel.Information, "Background editor selected managed asset {AssetId}")]
    internal static partial void ImageSelected(ILogger logger, string assetId);

    [LoggerMessage(6780, LogLevel.Information, "Background editor selected video asset {AssetId}")]
    internal static partial void VideoSelected(ILogger logger, string assetId);

    [LoggerMessage(6776, LogLevel.Information, "Background editor applied {Scope} {Kind} background")]
    internal static partial void Applied(
        ILogger logger,
        BackgroundEditorScopeKind scope,
        BackgroundKind kind);

    [LoggerMessage(6777, LogLevel.Debug, "Background editor cancelled for {Scope}")]
    internal static partial void Cancelled(ILogger logger, BackgroundEditorScopeKind scope);

    [LoggerMessage(6778, LogLevel.Warning, "Background editor {Operation} failed with {ErrorType}")]
    internal static partial void OperationFailed(
        ILogger logger,
        string operation,
        string errorType);

    [LoggerMessage(6779, LogLevel.Information, "Background editor restored default for {Scope}")]
    internal static partial void DefaultRestored(ILogger logger, BackgroundEditorScopeKind scope);

    [LoggerMessage(6781, LogLevel.Warning, "Background editor recent assets load failed with {ErrorType}")]
    internal static partial void RecentLoadFailed(ILogger logger, string errorType);

    [LoggerMessage(6782, LogLevel.Warning, "Background editor recent asset save failed with {ErrorType}")]
    internal static partial void RecentSaveFailed(ILogger logger, string errorType);

    [LoggerMessage(6783, LogLevel.Information, "Background editor loaded {SourceCount} {Protocol} sources")]
    internal static partial void SignalSourcesLoaded(ILogger logger, VideoSignalProtocol protocol, int sourceCount);

    [LoggerMessage(6784, LogLevel.Warning, "Background editor {Protocol} source load failed with {ErrorType}")]
    internal static partial void SignalSourcesLoadFailed(ILogger logger, VideoSignalProtocol protocol, string errorType);
}
