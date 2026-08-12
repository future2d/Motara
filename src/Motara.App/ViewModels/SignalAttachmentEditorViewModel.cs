using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Media;

namespace Motara.App.ViewModels;

internal sealed class SignalAttachmentEditorViewModel : INotifyPropertyChanged
{
    private readonly VideoSignalRegistry registry;
    private readonly Func<VideoSignalProtocol, VideoSignalSourceDescriptor, CancellationToken, Task> applyAsync;
    private readonly Action close;
    private readonly ILogger logger;
    private readonly Dictionary<VideoSignalProtocol, IReadOnlyList<VideoSignalSourceDescriptor>> sourceCache = [];
    private IReadOnlyList<VideoSignalSourceDescriptor> sources = [];
    private VideoSignalSourceDescriptor? selectedSource;
    private string? error;
    private bool isLoading;
    private CancellationTokenSource? refreshCancellation;
    private int refreshGeneration;

    internal SignalAttachmentEditorViewModel(
        VideoSignalProtocol protocol,
        VideoSignalRegistry registry,
        Func<VideoSignalProtocol, VideoSignalSourceDescriptor, CancellationToken, Task> applyAsync,
        Action close,
        ILogger<SignalAttachmentEditorViewModel>? logger = null)
    {
        if (protocol is not (VideoSignalProtocol.Spout2 or VideoSignalProtocol.Ndi))
        {
            throw new ArgumentOutOfRangeException(nameof(protocol));
        }

        Protocol = protocol;
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.applyAsync = applyAsync ?? throw new ArgumentNullException(nameof(applyAsync));
        this.close = close ?? throw new ArgumentNullException(nameof(close));
        this.logger = logger ?? NullLogger<SignalAttachmentEditorViewModel>.Instance;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal VideoSignalProtocol Protocol { get; private set; }

    internal void SelectProtocol(VideoSignalProtocol protocol)
    {
        if (protocol is not (VideoSignalProtocol.Spout2 or VideoSignalProtocol.Ndi)
            || Protocol == protocol)
        {
            return;
        }

        Protocol = protocol;
        refreshCancellation?.Cancel();
        if (sourceCache.TryGetValue(protocol, out IReadOnlyList<VideoSignalSourceDescriptor>? cached))
        {
            Sources = cached;
        }
        SelectedSource = null;
        Error = null;
        SignalAttachmentEditorLog.ProtocolChanged(logger, protocol);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Protocol)));
    }

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

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        refreshCancellation?.Cancel();
        CancellationTokenSource currentRefresh = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        refreshCancellation = currentRefresh;
        int generation = ++refreshGeneration;
        VideoSignalProtocol protocol = Protocol;
        IsLoading = true;
        Error = null;
        try
        {
            IReadOnlyList<VideoSignalSourceDescriptor> discovered =
                await registry.GetRequiredAdapter(protocol)
                    .DiscoverAsync(currentRefresh.Token)
                    .ConfigureAwait(false);
            if (generation != refreshGeneration || Protocol != protocol)
            {
                return;
            }

            sourceCache[protocol] = discovered;
            Sources = discovered;
            if (selectedSource is not null)
            {
                SelectedSource = discovered.FirstOrDefault(source =>
                    source.Protocol == protocol
                    && StringComparer.Ordinal.Equals(source.Id, selectedSource.Id));
            }
        }
        catch (OperationCanceledException) when (currentRefresh.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (generation == refreshGeneration && Protocol == protocol)
            {
                Error = exception.GetType().Name;
                SignalAttachmentEditorLog.RefreshFailed(logger, protocol, exception.GetType().Name);
            }
        }
        finally
        {
            if (generation == refreshGeneration)
            {
                IsLoading = false;
            }

            currentRefresh.Dispose();
            if (ReferenceEquals(refreshCancellation, currentRefresh))
            {
                refreshCancellation = null;
            }
        }
    }

    internal async Task ApplyAsync(CancellationToken cancellationToken)
    {
        Error = null;
        if (SelectedSource is not { } source)
        {
            Error = "MissingSource";
            return;
        }

        try
        {
            await applyAsync(Protocol, source, cancellationToken).ConfigureAwait(true);
            close();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Error = exception.GetType().Name;
            SignalAttachmentEditorLog.ApplyFailed(logger, Protocol, exception.GetType().Name);
        }
    }

    internal void Cancel() => close();

    internal void CancelPendingRefresh()
    {
        refreshCancellation?.Cancel();
        refreshGeneration++;
    }

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

internal static partial class SignalAttachmentEditorLog
{
    [LoggerMessage(6880, LogLevel.Warning, "Signal attachment source discovery failed for {Protocol}: {ErrorType}")]
    internal static partial void RefreshFailed(ILogger logger, VideoSignalProtocol protocol, string errorType);

    [LoggerMessage(6881, LogLevel.Warning, "Signal attachment apply failed for {Protocol}: {ErrorType}")]
    internal static partial void ApplyFailed(ILogger logger, VideoSignalProtocol protocol, string errorType);

    [LoggerMessage(6882, LogLevel.Debug, "Signal attachment protocol changed to {Protocol}")]
    internal static partial void ProtocolChanged(ILogger logger, VideoSignalProtocol protocol);
}
