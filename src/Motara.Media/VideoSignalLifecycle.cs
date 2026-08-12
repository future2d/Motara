using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Motara.Media;

public sealed record VideoSignalConnectionSnapshot(
    VideoSignalState State,
    VideoSignalSourceDescriptor? SelectedSource,
    string? ErrorType,
    DateTimeOffset ChangedAt);

public sealed class VideoSignalReceiverLifecycle : IAsyncDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);
    private readonly VideoSignalRegistry registry;
    private readonly ILogger logger;
    private readonly SemaphoreSlim transitionGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object reconnectGate = new();
    private IVideoSignalReceiver? receiver;
    private VideoSignalSourceDescriptor? selectedSource;
    private VideoSignalConnectionSnapshot snapshot = new(VideoSignalState.Stopped, null, null, DateTimeOffset.UtcNow);
    private Task? reconnectTask;
    private int disposed;

    public VideoSignalReceiverLifecycle(
        VideoSignalRegistry registry,
        ILogger<VideoSignalReceiverLifecycle>? logger = null)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.logger = logger ?? NullLogger<VideoSignalReceiverLifecycle>.Instance;
    }

    public event EventHandler<VideoSignalConnectionSnapshot>? SnapshotChanged;

    public VideoSignalConnectionSnapshot Snapshot => Volatile.Read(ref snapshot);

    public async Task<VideoSignalConnectionSnapshot> StartAsync(
        VideoSignalSourceDescriptor source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        await transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReleaseReceiverAsync().ConfigureAwait(false);
            selectedSource = source;
            return await ConnectSelectedAsync(source, VideoSignalState.Starting, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            transitionGate.Release();
        }
    }

    public async Task<VideoSignalConnectionSnapshot> ReconnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (selectedSource is not { } source)
            {
                return SetSnapshot(VideoSignalState.Stopped, null);
            }

            await ReleaseReceiverAsync().ConfigureAwait(false);
            return await ConnectSelectedAsync(source, VideoSignalState.Reconnecting, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            transitionGate.Release();
        }
    }

    public async Task<VideoSignalConnectionSnapshot> StopAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            selectedSource = null;
            await ReleaseReceiverAsync().ConfigureAwait(false);
            return SetSnapshot(VideoSignalState.Stopped, null);
        }
        finally
        {
            transitionGate.Release();
        }
    }

    public SignalFrame? ReadLatest() => Volatile.Read(ref receiver)?.ReadLatest();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetime.Cancel();
        try
        {
            Task? pendingReconnect;
            lock (reconnectGate)
            {
                pendingReconnect = reconnectTask;
            }

            if (pendingReconnect is not null)
            {
                await pendingReconnect.WaitAsync(StopTimeout).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            VideoSignalLog.StopIncomplete(logger, exception.GetType().Name);
        }

        await transitionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            selectedSource = null;
            await ReleaseReceiverAsync().ConfigureAwait(false);
            SetSnapshot(VideoSignalState.Stopped, null);
        }
        finally
        {
            transitionGate.Release();
            transitionGate.Dispose();
            lifetime.Dispose();
        }
    }

    private async Task<VideoSignalConnectionSnapshot> ConnectSelectedAsync(
        VideoSignalSourceDescriptor source,
        VideoSignalState startingState,
        CancellationToken cancellationToken)
    {
        SetSnapshot(startingState, null);
        try
        {
            IVideoSignalReceiver created = registry.GetRequiredAdapter(source.Protocol).CreateReceiver();
            created.StateChanged += OnReceiverStateChanged;
            Volatile.Write(ref receiver, created);
            await created.StartAsync(source, cancellationToken).ConfigureAwait(false);
            return SetSnapshot(VideoSignalState.Ready, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseReceiverAsync().ConfigureAwait(false);
            return SetSnapshot(VideoSignalState.Stopped, null);
        }
        catch (Exception exception)
        {
            await ReleaseReceiverAsync().ConfigureAwait(false);
            return SetSnapshot(VideoSignalState.Faulted, exception.GetType().Name);
        }
    }

    private async Task ReleaseReceiverAsync()
    {
        IVideoSignalReceiver? active = Interlocked.Exchange(ref receiver, null);
        if (active is null)
        {
            return;
        }

        active.StateChanged -= OnReceiverStateChanged;
        try
        {
            await active.StopAsync(CancellationToken.None).AsTask().WaitAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            VideoSignalLog.StopIncomplete(logger, exception.GetType().Name);
        }
        finally
        {
            await active.DisposeAsync().AsTask().WaitAsync(StopTimeout).ConfigureAwait(false);
        }
    }

    private void OnReceiverStateChanged(object? sender, VideoSignalStateChangedEventArgs args)
    {
        if (args.State is not (VideoSignalState.Faulted or VideoSignalState.Stopped)
            || Volatile.Read(ref disposed) != 0
            || selectedSource is null)
        {
            return;
        }

        SetSnapshot(VideoSignalState.Reconnecting, args.Failure?.GetType().Name);
        lock (reconnectGate)
        {
            if (reconnectTask is null || reconnectTask.IsCompleted)
            {
                reconnectTask = ReconnectAfterDelayAsync(lifetime.Token);
            }
        }
    }

    private async Task ReconnectAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            VideoSignalLog.ReconnectScheduled(logger, ReconnectDelay.TotalMilliseconds);
            await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
            await ReconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            VideoSignalLog.ReconnectFailed(logger, exception.GetType().Name);
        }
    }

    private VideoSignalConnectionSnapshot SetSnapshot(VideoSignalState state, string? errorType)
    {
        var next = new VideoSignalConnectionSnapshot(state, selectedSource, errorType, DateTimeOffset.UtcNow);
        Volatile.Write(ref snapshot, next);
        VideoSignalLog.ReceiverStateChanged(logger, state, selectedSource?.Protocol.ToString() ?? "None", errorType ?? "None");
        SnapshotChanged?.Invoke(this, next);
        return next;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }
}
