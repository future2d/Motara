using System.Collections.Immutable;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Backgrounds;
using Motara.Media;
using Motara.Persistence;
using Motara.Scene;

namespace Motara.App.ViewModels;

internal enum SceneAttachmentKind
{
    Image = 0,
    Video = 1,
    Live2D = 2,
    Spout2 = 3,
    Ndi = 4,
    VirtualCamera = 5,
}

internal sealed class SceneAttachmentEditorViewModel : INotifyPropertyChanged
{
    private readonly IBackgroundAssetStore assetStore;
    private readonly IBackgroundRecentAssetStore recentAssetStore;
    private readonly VideoSignalRegistry registry;
    private readonly Func<string, string, string, BackgroundVideoOptions, AttachmentPlacement, CancellationToken, Task> applyAsync;
    private readonly Action close;
    private readonly ILogger logger;
    private readonly Dictionary<VideoSignalProtocol, IReadOnlyList<VideoSignalSourceDescriptor>> sourceCache = [];
    private readonly Dictionary<VideoSignalProtocol, string?> sourceErrors = [];
    private IReadOnlyList<VideoSignalSourceDescriptor> sources = [];
    private VideoSignalSourceDescriptor? selectedSource;
    private SceneAttachmentKind kind;
    private string? imageAssetId;
    private string? videoAssetId;
    private string? selectedImageDisplayName;
    private string? selectedVideoDisplayName;
    private BackgroundVideoOptions videoOptions = BackgroundVideoOptions.Default;
    private AttachmentPlacement placement = AttachmentPlacement.AfterMainModel;
    private string? error;
    private bool isLoading;
    private CancellationTokenSource? refreshCancellation;
    private int refreshGeneration;

    internal SceneAttachmentEditorViewModel(
        IBackgroundAssetStore assetStore,
        IBackgroundRecentAssetStore recentAssetStore,
        VideoSignalRegistry registry,
        Func<string, string, string, BackgroundVideoOptions, AttachmentPlacement, CancellationToken, Task> applyAsync,
        Action close,
        ILogger<SceneAttachmentEditorViewModel>? logger = null)
    {
        this.assetStore = assetStore ?? throw new ArgumentNullException(nameof(assetStore));
        this.recentAssetStore = recentAssetStore ?? throw new ArgumentNullException(nameof(recentAssetStore));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.applyAsync = applyAsync ?? throw new ArgumentNullException(nameof(applyAsync));
        this.close = close ?? throw new ArgumentNullException(nameof(close));
        this.logger = logger ?? NullLogger<SceneAttachmentEditorViewModel>.Instance;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal SceneAttachmentKind Kind
    {
        get => kind;
        private set => Set(ref kind, value);
    }

    internal VideoSignalProtocol Protocol => Kind == SceneAttachmentKind.Ndi
        ? VideoSignalProtocol.Ndi
        : VideoSignalProtocol.Spout2;

    internal IReadOnlyList<VideoSignalSourceDescriptor> Sources
    {
        get => sources;
        private set => Set(ref sources, value);
    }

    internal VideoSignalSourceDescriptor? SelectedSource
    {
        get => selectedSource;
        set => Set(ref selectedSource, value);
    }

    internal string? ImageAssetId
    {
        get => imageAssetId;
        private set => Set(ref imageAssetId, value);
    }

    internal string? VideoAssetId
    {
        get => videoAssetId;
        private set => Set(ref videoAssetId, value);
    }

    internal string? SelectedImageDisplayName => selectedImageDisplayName;

    internal string? SelectedVideoDisplayName => selectedVideoDisplayName;

    internal IReadOnlyList<BackgroundRecentAsset> RecentImages { get; private set; } = [];

    internal IReadOnlyList<BackgroundRecentAsset> RecentVideos { get; private set; } = [];

    internal BackgroundVideoOptions VideoOptions
    {
        get => videoOptions;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Set(ref videoOptions, value);
        }
    }

    internal AttachmentPlacement Placement
    {
        get => placement;
        set => Set(ref placement, value);
    }

    internal string? Error
    {
        get => error;
        private set => Set(ref error, value);
    }

    internal bool IsLoading
    {
        get => isLoading;
        private set => Set(ref isLoading, value);
    }

    internal bool HasSelectedContent => Kind switch
    {
        SceneAttachmentKind.Image => ImageAssetId is not null,
        SceneAttachmentKind.Video => VideoAssetId is not null,
        SceneAttachmentKind.Spout2 or SceneAttachmentKind.Ndi => SelectedSource is not null,
        _ => false,
    };

    internal void SelectKind(SceneAttachmentKind value)
    {
        if (Kind == value)
        {
            return;
        }

        Kind = value;
        SelectedSource = null;
        Error = null;
        if (value is SceneAttachmentKind.Spout2 or SceneAttachmentKind.Ndi)
        {
            VideoSignalProtocol protocol = value == SceneAttachmentKind.Ndi
                ? VideoSignalProtocol.Ndi
                : VideoSignalProtocol.Spout2;
            refreshCancellation?.Cancel();
            if (sourceCache.TryGetValue(protocol, out IReadOnlyList<VideoSignalSourceDescriptor>? cached))
            {
                Sources = cached;
            }
        }
        else
        {
            Sources = [];
        }

        SceneAttachmentEditorLog.KindChanged(logger, value);
        OnPropertyChanged(nameof(Protocol));
        OnPropertyChanged(nameof(HasSelectedContent));
    }

    internal async Task LoadRecentAssetsAsync(CancellationToken cancellationToken)
    {
        try
        {
            BackgroundRecentAssets recent = await recentAssetStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            RecentImages = recent.Images;
            RecentVideos = recent.Videos;
            OnPropertyChanged(nameof(RecentImages));
            OnPropertyChanged(nameof(RecentVideos));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SceneAttachmentEditorLog.RecentLoadFailed(logger, exception.GetType().Name);
        }
    }

    internal async Task ImportImageAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            BackgroundAssetImportResult imported = await assetStore.ImportAsync(path, cancellationToken).ConfigureAwait(false);
            ImageAssetId = imported.AssetId;
            selectedImageDisplayName = Path.GetFileName(path);
            OnPropertyChanged(nameof(SelectedImageDisplayName));
            Kind = SceneAttachmentKind.Image;
            await RememberRecentAsync(BackgroundRecentAssetKind.Image, imported.AssetId, path, cancellationToken).ConfigureAwait(false);
            Error = null;
            OnPropertyChanged(nameof(HasSelectedContent));
            SceneAttachmentEditorLog.ImageImported(logger, imported.AssetId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Error = exception.GetType().Name;
            SceneAttachmentEditorLog.ImportFailed(logger, "Image", exception.GetType().Name);
            throw;
        }
    }

    internal async Task ImportVideoAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            BackgroundAssetImportResult imported = await assetStore.ImportVideoAsync(path, cancellationToken).ConfigureAwait(false);
            VideoAssetId = imported.AssetId;
            selectedVideoDisplayName = Path.GetFileName(path);
            OnPropertyChanged(nameof(SelectedVideoDisplayName));
            Kind = SceneAttachmentKind.Video;
            await RememberRecentAsync(BackgroundRecentAssetKind.Video, imported.AssetId, path, cancellationToken).ConfigureAwait(false);
            Error = null;
            OnPropertyChanged(nameof(HasSelectedContent));
            SceneAttachmentEditorLog.VideoImported(logger, imported.AssetId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Error = exception.GetType().Name;
            SceneAttachmentEditorLog.ImportFailed(logger, "Video", exception.GetType().Name);
            throw;
        }
    }

    internal async Task SelectRecentAssetAsync(
        BackgroundRecentAssetKind assetKind,
        BackgroundRecentAsset asset,
        CancellationToken cancellationToken)
    {
        string path = assetKind == BackgroundRecentAssetKind.Image
            ? assetStore.GetManagedPath(asset.AssetId)
            : assetStore.GetManagedVideoPath(asset.AssetId);
        if (!File.Exists(path))
        {
            Error = "RecentAssetUnavailable";
            return;
        }

        if (assetKind == BackgroundRecentAssetKind.Image)
        {
            ImageAssetId = asset.AssetId;
            selectedImageDisplayName = asset.DisplayName;
            Kind = SceneAttachmentKind.Image;
            OnPropertyChanged(nameof(SelectedImageDisplayName));
        }
        else
        {
            VideoAssetId = asset.AssetId;
            selectedVideoDisplayName = asset.DisplayName;
            Kind = SceneAttachmentKind.Video;
            OnPropertyChanged(nameof(SelectedVideoDisplayName));
        }

        Error = null;
        OnPropertyChanged(nameof(HasSelectedContent));
        await RememberRecentAsync(assetKind, asset.AssetId, asset.DisplayName, cancellationToken).ConfigureAwait(false);
    }

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (Kind is not (SceneAttachmentKind.Spout2 or SceneAttachmentKind.Ndi))
        {
            return;
        }

        refreshCancellation?.Cancel();
        CancellationTokenSource current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        refreshCancellation = current;
        int generation = ++refreshGeneration;
        VideoSignalProtocol protocol = Protocol;
        IsLoading = true;
        Error = null;
        try
        {
            IReadOnlyList<VideoSignalSourceDescriptor> discovered = await registry
                .GetRequiredAdapter(protocol)
                .DiscoverAsync(current.Token)
                .ConfigureAwait(false);
            if (generation != refreshGeneration || Protocol != protocol)
            {
                return;
            }

            sourceCache[protocol] = discovered;
            sourceErrors[protocol] = null;
            Sources = discovered;
            OnPropertyChanged(nameof(HasSelectedContent));
            SceneAttachmentEditorLog.SourcesLoaded(logger, protocol, discovered.Count);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (generation == refreshGeneration && Protocol == protocol)
            {
                sourceErrors[protocol] = exception.GetType().Name;
                Error = exception.GetType().Name;
                SceneAttachmentEditorLog.RefreshFailed(logger, protocol, exception.GetType().Name);
            }
        }
        finally
        {
            if (generation == refreshGeneration)
            {
                IsLoading = false;
            }

            current.Dispose();
            if (ReferenceEquals(refreshCancellation, current))
            {
                refreshCancellation = null;
            }
        }
    }

    internal async Task ApplyAsync(CancellationToken cancellationToken)
    {
        Error = null;
        string? sourceTypeId;
        string? resourceReference;
        switch (Kind)
        {
            case SceneAttachmentKind.Image:
                sourceTypeId = "attachment.image";
                resourceReference = ImageAssetId;
                if (resourceReference is not null) BackgroundDefinition.ValidateImageAssetId(resourceReference);
                break;
            case SceneAttachmentKind.Video:
                sourceTypeId = "attachment.video";
                resourceReference = VideoAssetId;
                if (resourceReference is not null) BackgroundDefinition.ValidateVideoAssetId(resourceReference);
                break;
            case SceneAttachmentKind.Spout2:
                sourceTypeId = "attachment.spout2";
                resourceReference = SelectedSource?.Id;
                break;
            case SceneAttachmentKind.Ndi:
                sourceTypeId = "attachment.ndi";
                resourceReference = SelectedSource?.Id;
                break;
            default:
                sourceTypeId = null;
                resourceReference = null;
                break;
        }

        if (sourceTypeId is null || resourceReference is null)
        {
            Error = "MissingSource";
            return;
        }

        try
        {
            BackgroundVideoOptions options = Kind == SceneAttachmentKind.Video
                ? VideoOptions
                : BackgroundVideoOptions.Default;
            string displayName = Kind switch
            {
                SceneAttachmentKind.Image => selectedImageDisplayName ?? Path.GetFileName(resourceReference),
                SceneAttachmentKind.Video => selectedVideoDisplayName ?? Path.GetFileName(resourceReference),
                SceneAttachmentKind.Spout2 or SceneAttachmentKind.Ndi => SelectedSource?.DisplayName ?? resourceReference,
                _ => resourceReference,
            };
            await applyAsync(
                    sourceTypeId,
                    resourceReference,
                    displayName,
                    options,
                    AttachmentPlacement.AfterMainModel,
                    cancellationToken)
                .ConfigureAwait(true);
            close();
            SceneAttachmentEditorLog.Applied(logger, sourceTypeId, resourceReference);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Error = exception.GetType().Name;
            SceneAttachmentEditorLog.ApplyFailed(logger, sourceTypeId, exception.GetType().Name);
        }
    }

    internal void CancelPendingRefresh()
    {
        refreshCancellation?.Cancel();
        refreshGeneration++;
    }

    internal void Cancel() => close();

    private async Task RememberRecentAsync(
        BackgroundRecentAssetKind kind,
        string assetId,
        string displayName,
        CancellationToken cancellationToken)
    {
        try
        {
            BackgroundRecentAssets recent = await recentAssetStore
                .RememberAsync(kind, assetId, displayName, cancellationToken)
                .ConfigureAwait(false);
            RecentImages = recent.Images;
            RecentVideos = recent.Videos;
            OnPropertyChanged(nameof(RecentImages));
            OnPropertyChanged(nameof(RecentVideos));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SceneAttachmentEditorLog.RecentSaveFailed(logger, exception.GetType().Name);
        }
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

internal static partial class SceneAttachmentEditorLog
{
    [LoggerMessage(6890, LogLevel.Debug, "Scene attachment kind changed to {Kind}")]
    internal static partial void KindChanged(ILogger logger, SceneAttachmentKind kind);

    [LoggerMessage(6891, LogLevel.Information, "Scene attachment image imported: {AssetId}")]
    internal static partial void ImageImported(ILogger logger, string assetId);

    [LoggerMessage(6892, LogLevel.Information, "Scene attachment video imported: {AssetId}")]
    internal static partial void VideoImported(ILogger logger, string assetId);

    [LoggerMessage(6893, LogLevel.Information, "Scene attachment sources loaded for {Protocol}: {SourceCount}")]
    internal static partial void SourcesLoaded(ILogger logger, VideoSignalProtocol protocol, int sourceCount);

    [LoggerMessage(6894, LogLevel.Warning, "Scene attachment source refresh failed for {Protocol}: {ErrorType}")]
    internal static partial void RefreshFailed(ILogger logger, VideoSignalProtocol protocol, string errorType);

    [LoggerMessage(6895, LogLevel.Information, "Scene attachment applied for {SourceTypeId}:{ResourceReference}")]
    internal static partial void Applied(ILogger logger, string sourceTypeId, string resourceReference);

    [LoggerMessage(6896, LogLevel.Warning, "Scene attachment apply failed for {SourceTypeId}: {ErrorType}")]
    internal static partial void ApplyFailed(ILogger logger, string sourceTypeId, string errorType);

    [LoggerMessage(6897, LogLevel.Warning, "Scene attachment recent assets load failed: {ErrorType}")]
    internal static partial void RecentLoadFailed(ILogger logger, string errorType);

    [LoggerMessage(6898, LogLevel.Warning, "Scene attachment recent asset save failed: {ErrorType}")]
    internal static partial void RecentSaveFailed(ILogger logger, string errorType);

    [LoggerMessage(6899, LogLevel.Warning, "Scene attachment import failed for {Kind}: {ErrorType}")]
    internal static partial void ImportFailed(ILogger logger, string kind, string errorType);
}
