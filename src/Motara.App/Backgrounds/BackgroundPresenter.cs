using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Motara.Persistence;
using Motara.Media;

namespace Motara.App.Backgrounds;

internal enum BackgroundFallbackReason
{
    None = 0,
    Loading = 1,
    MissingAsset = 2,
    DecodeFailed = 3,
}

internal sealed class BackgroundImageResource : IDisposable
{
    private int disposed;

    internal BackgroundImageResource(Bitmap bitmap)
    {
        Bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
    }

    internal Bitmap Bitmap { get; }

    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            Bitmap.Dispose();
        }
    }
}

internal interface IBackgroundImageDecoder
{
    Task<BackgroundImageResource> DecodeAsync(
        string assetId,
        CancellationToken cancellationToken);
}

internal sealed class BackgroundImageDecoder(IBackgroundAssetStore assetStore) : IBackgroundImageDecoder
{
    public Task<BackgroundImageResource> DecodeAsync(
        string assetId,
        CancellationToken cancellationToken) => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = assetStore.GetManagedPath(assetId);
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            var bitmap = new Bitmap(stream);
            if (cancellationToken.IsCancellationRequested)
            {
                bitmap.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new BackgroundImageResource(bitmap);
        }, CancellationToken.None);
}

internal sealed class BackgroundPresentationDispatcher
{
    private readonly Func<Action, CancellationToken, Task> invokeAsync;

    private BackgroundPresentationDispatcher(Func<Action, CancellationToken, Task> invokeAsync)
    {
        this.invokeAsync = invokeAsync;
    }

    internal static BackgroundPresentationDispatcher Immediate { get; } = new(
        (action, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        });

    internal static BackgroundPresentationDispatcher UiThread { get; } = new(
        async (action, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(action);
            cancellationToken.ThrowIfCancellationRequested();
        });

    internal Task InvokeAsync(Action action, CancellationToken cancellationToken) =>
        invokeAsync(action, cancellationToken);
}

internal sealed record BackgroundVisualSnapshot(
    BackgroundDefinition Definition,
    BackgroundImageResource? Resource,
    BackgroundFallbackReason FallbackReason)
{
    internal IBackgroundVideoPlayback? Video { get; init; }

    internal Bitmap? Image => Resource?.Bitmap ?? Video?.Bitmap;

    internal static BackgroundVisualSnapshot Initial { get; } = new(
        BackgroundDefinition.Solid(UiSettings.DefaultCanvasBackgroundColor),
        null,
        BackgroundFallbackReason.None);
}

internal sealed class BackgroundPresenter : IAsyncDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private readonly object gate = new();
    private readonly IBackgroundImageDecoder decoder;
    private readonly IBackgroundVideoPlaybackFactory videoFactory;
    private readonly IBackgroundSignalPlaybackFactory signalFactory;
    private readonly ILogger<BackgroundPresenter> logger;
    private readonly BackgroundPresentationDispatcher dispatcher;
    private readonly HashSet<Task> operations = [];
    private BackgroundVisualSnapshot current = BackgroundVisualSnapshot.Initial;
    private CancellationTokenSource? currentRequestCancellation;
    private long requestVersion;
    private int disposed;

    internal BackgroundPresenter(
        IBackgroundImageDecoder decoder,
        ILogger<BackgroundPresenter> logger,
        BackgroundPresentationDispatcher dispatcher)
        : this(
            decoder,
            UnsupportedBackgroundVideoPlaybackFactory.Instance,
            logger,
            dispatcher,
            UnsupportedBackgroundSignalPlaybackFactory.Instance)
    {
    }

    internal BackgroundPresenter(
        IBackgroundImageDecoder decoder,
        IBackgroundVideoPlaybackFactory videoFactory,
        ILogger<BackgroundPresenter> logger,
        BackgroundPresentationDispatcher dispatcher,
        IBackgroundSignalPlaybackFactory? signalFactory = null)
    {
        this.decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        this.videoFactory = videoFactory ?? throw new ArgumentNullException(nameof(videoFactory));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.signalFactory = signalFactory ?? UnsupportedBackgroundSignalPlaybackFactory.Instance;
    }

    internal event EventHandler<BackgroundVisualSnapshot>? SnapshotChanged;

    internal BackgroundVisualSnapshot Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    internal Task<BackgroundVisualSnapshot> ApplyAsync(
        ResolvedBackground background,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(background);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        Task<BackgroundVisualSnapshot> operation = ApplyCoreAsync(background, cancellationToken);
        lock (gate)
        {
            operations.Add(operation);
        }

        _ = operation.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                lock (gate)
                {
                    operations.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return operation;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Task[] active;
        BackgroundVisualSnapshot released;
        lock (gate)
        {
            requestVersion++;
            currentRequestCancellation?.Cancel();
            active = operations.ToArray();
            released = current;
            current = BackgroundVisualSnapshot.Initial;
        }

        try
        {
            await Task.WhenAll(active).WaitAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            _ = exception;
        }

        await DisposeSnapshotAsync(released).ConfigureAwait(false);
        currentRequestCancellation?.Dispose();
        BackgroundPresenterLog.Stopped(logger);
    }

    private async Task<BackgroundVisualSnapshot> ApplyCoreAsync(
        ResolvedBackground background,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        long version;
        lock (gate)
        {
            currentRequestCancellation?.Cancel();
            currentRequestCancellation = requestCancellation;
            version = ++requestVersion;
        }

        BackgroundPresenterLog.ApplyStarted(logger, version, background.Definition.Kind.ToString());
        try
        {
            if (background.Definition.Kind == BackgroundKind.Solid)
            {
                var solid = new BackgroundVisualSnapshot(
                    background.Definition,
                    null,
                    BackgroundFallbackReason.None);
                BackgroundVisualSnapshot result = await SwapIfCurrentAsync(
                    version,
                    solid,
                    requestCancellation.Token).ConfigureAwait(false);
                BackgroundPresenterLog.ApplyCompleted(logger, version, hasImage: false);
                return result;
            }

            if (background.Definition.Kind == BackgroundKind.Video)
            {
                return await ApplyVideoAsync(
                    background,
                    version,
                    requestCancellation.Token,
                    cancellationToken).ConfigureAwait(false);
            }

            if (background.Definition.Kind == BackgroundKind.Signal)
            {
                return await ApplySignalAsync(
                    background,
                    version,
                    requestCancellation.Token,
                    cancellationToken).ConfigureAwait(false);
            }

            var loading = new BackgroundVisualSnapshot(
                background.Definition,
                null,
                BackgroundFallbackReason.Loading);
            _ = await SwapIfCurrentAsync(
                version,
                loading,
                requestCancellation.Token).ConfigureAwait(false);
            BackgroundImageResource resource;
            try
            {
                resource = await decoder.DecodeAsync(
                    background.Definition.ImageAssetId!,
                    requestCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                BackgroundPresenterLog.StaleResult(logger, version);
                return Current;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                BackgroundFallbackReason reason = exception is FileNotFoundException
                    or DirectoryNotFoundException
                    ? BackgroundFallbackReason.MissingAsset
                    : BackgroundFallbackReason.DecodeFailed;
                var fallback = new BackgroundVisualSnapshot(background.Definition, null, reason);
                BackgroundVisualSnapshot result = await SwapIfCurrentAsync(
                    version,
                    fallback,
                    CancellationToken.None).ConfigureAwait(false);
                BackgroundPresenterLog.Fallback(logger, version, reason);
                return result;
            }

            var decoded = new BackgroundVisualSnapshot(
                background.Definition,
                resource,
                BackgroundFallbackReason.None);
            BackgroundVisualSnapshot completed = await SwapIfCurrentAsync(
                version,
                decoded,
                CancellationToken.None).ConfigureAwait(false);
            BackgroundPresenterLog.ApplyCompleted(logger, version, completed.Image is not null);
            return completed;
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(currentRequestCancellation, requestCancellation))
                {
                    currentRequestCancellation = null;
                }
            }

            requestCancellation.Dispose();
        }
    }

    private async Task<BackgroundVisualSnapshot> SwapIfCurrentAsync(
        long version,
        BackgroundVisualSnapshot next,
        CancellationToken cancellationToken)
    {
        BackgroundVisualSnapshot? previous = null;
        bool accepted = false;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                lock (gate)
                {
                    if (disposed != 0 || version != requestVersion)
                    {
                        return;
                    }

                    previous = current;
                    current = next;
                    accepted = true;
                }

                SnapshotChanged?.Invoke(this, next);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!accepted)
            {
                await DisposeSnapshotAsync(next).ConfigureAwait(false);
            }
        }

        if (!accepted)
        {
            BackgroundPresenterLog.StaleResult(logger, version);
            return Current;
        }

        if (previous is not null)
        {
            await DisposeSnapshotAsync(previous).ConfigureAwait(false);
        }

        return next;
    }

    private async Task<BackgroundVisualSnapshot> ApplyVideoAsync(
        ResolvedBackground background,
        long version,
        CancellationToken requestCancellation,
        CancellationToken callerCancellation)
    {
        var loading = new BackgroundVisualSnapshot(
            background.Definition,
            null,
            BackgroundFallbackReason.Loading);
        _ = await SwapIfCurrentAsync(version, loading, requestCancellation)
            .ConfigureAwait(false);
        try
        {
            IBackgroundVideoPlayback playback = await videoFactory.StartAsync(
                background.Definition.VideoAssetId!,
                background.Definition.VideoOptions,
                requestCancellation).ConfigureAwait(false);
            var ready = new BackgroundVisualSnapshot(
                background.Definition,
                null,
                BackgroundFallbackReason.None)
            {
                Video = playback,
            };
            BackgroundVisualSnapshot completed = await SwapIfCurrentAsync(
                version,
                ready,
                CancellationToken.None).ConfigureAwait(false);
            BackgroundPresenterLog.ApplyCompleted(logger, version, completed.Image is not null);
            return completed;
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested)
        {
            BackgroundPresenterLog.StaleResult(logger, version);
            return Current;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            BackgroundFallbackReason reason = exception is FileNotFoundException
                or DirectoryNotFoundException
                ? BackgroundFallbackReason.MissingAsset
                : BackgroundFallbackReason.DecodeFailed;
            var fallback = new BackgroundVisualSnapshot(background.Definition, null, reason);
            BackgroundVisualSnapshot result = await SwapIfCurrentAsync(
                version,
                fallback,
                CancellationToken.None).ConfigureAwait(false);
            BackgroundPresenterLog.Fallback(logger, version, reason);
            return result;
        }
    }

    private async Task<BackgroundVisualSnapshot> ApplySignalAsync(
        ResolvedBackground background,
        long version,
        CancellationToken requestCancellation,
        CancellationToken callerCancellation)
    {
        var loading = new BackgroundVisualSnapshot(
            background.Definition,
            null,
            BackgroundFallbackReason.Loading);
        _ = await SwapIfCurrentAsync(version, loading, requestCancellation).ConfigureAwait(false);
        try
        {
            IBackgroundVideoPlayback playback = await signalFactory.StartAsync(
                background.Definition.SignalSource!,
                requestCancellation).ConfigureAwait(false);
            var ready = new BackgroundVisualSnapshot(
                background.Definition,
                null,
                BackgroundFallbackReason.None)
            {
                Video = playback,
            };
            return await SwapIfCurrentAsync(version, ready, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested)
        {
            BackgroundPresenterLog.StaleResult(logger, version);
            return Current;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var fallback = new BackgroundVisualSnapshot(
                background.Definition,
                null,
                BackgroundFallbackReason.DecodeFailed);
            BackgroundVisualSnapshot result = await SwapIfCurrentAsync(
                version,
                fallback,
                CancellationToken.None).ConfigureAwait(false);
            BackgroundPresenterLog.Fallback(logger, version, BackgroundFallbackReason.DecodeFailed);
            _ = exception;
            return result;
        }
    }

    private static async ValueTask DisposeSnapshotAsync(BackgroundVisualSnapshot snapshot)
    {
        snapshot.Resource?.Dispose();
        if (snapshot.Video is not null)
        {
            await snapshot.Video.DisposeAsync().ConfigureAwait(false);
        }
    }
}
