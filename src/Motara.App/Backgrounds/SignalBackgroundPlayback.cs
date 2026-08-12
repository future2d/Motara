using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Media;
using Motara.Persistence;

namespace Motara.App.Backgrounds;

internal sealed class SignalBackgroundPlayback : IBackgroundVideoPlayback
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private readonly VideoSignalReceiverLifecycle lifecycle;
    private readonly ILogger logger;
    private readonly object frameGate = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly BackgroundPresentationDispatcher dispatcher;
    private Task? consumer;
    private WriteableBitmap? bitmap;
    private int bitmapWidth;
    private int bitmapHeight;
    private BackgroundVideoFrameSnapshot? current;
    private int disposed;

    internal SignalBackgroundPlayback(
        VideoSignalReceiverLifecycle lifecycle,
        BackgroundPresentationDispatcher dispatcher,
        ILogger? logger = null)
    {
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.logger = logger ?? NullLogger<SignalBackgroundPlayback>.Instance;
    }

    public event EventHandler? FrameChanged;

    public Bitmap Bitmap => bitmap ?? throw new InvalidOperationException("Signal background bitmap is not initialized.");

    public BackgroundVideoFrameSnapshot? CaptureCurrentFrame()
    {
        lock (frameGate)
        {
            return current is null
                ? null
                : current with { BgraPixels = (byte[])current.BgraPixels.Clone() };
        }
    }

    internal async Task<VideoSignalConnectionSnapshot> StartAsync(
        VideoSignalSourceDescriptor source,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        VideoSignalConnectionSnapshot snapshot = await lifecycle.StartAsync(source, cancellationToken).ConfigureAwait(false);
        if (snapshot.State == VideoSignalState.Ready)
        {
            int initialWidth = source.Width > 0 ? source.Width : 1;
            int initialHeight = source.Height > 0 ? source.Height : 1;
            await dispatcher.InvokeAsync(
                ()
                =>
                {
                    bitmap = CreateBitmap(initialWidth, initialHeight);
                    bitmapWidth = initialWidth;
                    bitmapHeight = initialHeight;
                },
                cancellationToken).ConfigureAwait(false);
            consumer = ConsumeAsync(cancellation.Token);
        }

        SignalBackgroundPlaybackLog.Started(logger, source.Protocol, source.Id, snapshot.State);
        return snapshot;
    }

    internal async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        cancellation.Cancel();
        if (consumer is not null)
        {
            try
            {
                await consumer.WaitAsync(StopTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
                SignalBackgroundPlaybackLog.StopIncomplete(logger, exception.GetType().Name);
            }
        }

        await lifecycle.StopAsync(CancellationToken.None).ConfigureAwait(false);
        WriteableBitmap? releasedBitmap = Interlocked.Exchange(ref bitmap, null);
        bitmapWidth = 0;
        bitmapHeight = 0;
        if (releasedBitmap is not null)
        {
            await dispatcher.InvokeAsync(releasedBitmap.Dispose, CancellationToken.None).ConfigureAwait(false);
        }
        lock (frameGate)
        {
            current = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await lifecycle.DisposeAsync().ConfigureAwait(false);
            cancellation.Dispose();
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using SignalFrame? frame = lifecycle.ReadLatest();
                if (frame is not null)
                {
                    BackgroundVideoFrameSnapshot next = new(
                        frame.Metadata.Width,
                        frame.Metadata.Height,
                        frame.Pixels.ToArray());
                    lock (frameGate)
                    {
                        current = next;
                    }

                    byte[] pixels = next.BgraPixels;
                    await dispatcher.InvokeAsync(
                        () =>
                        {
                            WriteableBitmap? target = Volatile.Read(ref bitmap);
                            if (target is null || bitmapWidth != next.Width || bitmapHeight != next.Height)
                            {
                                WriteableBitmap replacement = CreateBitmap(next.Width, next.Height);
                                WriteableBitmap? previous = Interlocked.Exchange(ref bitmap, replacement);
                                previous?.Dispose();
                                bitmapWidth = next.Width;
                                bitmapHeight = next.Height;
                                target = replacement;
                            }

                            using ILockedFramebuffer locked = target.Lock();
                            int rowBytes = next.Width * 4;
                            for (int row = 0; row < next.Height; row++)
                            {
                                Marshal.Copy(pixels, row * rowBytes, locked.Address + row * locked.RowBytes, rowBytes);
                            }
                            FrameChanged?.Invoke(this, EventArgs.Empty);
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(4), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SignalBackgroundPlaybackLog.ConsumeFailed(logger, exception.GetType().Name);
        }
    }

    private static WriteableBitmap CreateBitmap(int width, int height) => new(
        new PixelSize(width, height),
        new Avalonia.Vector(96, 96),
        PixelFormat.Bgra8888,
        AlphaFormat.Unpremul);
}

internal interface IBackgroundSignalPlaybackFactory
{
    Task<IBackgroundVideoPlayback> StartAsync(
        VideoSignalSourceSelection selection,
        CancellationToken cancellationToken);
}

internal sealed class UnsupportedBackgroundSignalPlaybackFactory : IBackgroundSignalPlaybackFactory
{
    internal static UnsupportedBackgroundSignalPlaybackFactory Instance { get; } = new();

    private UnsupportedBackgroundSignalPlaybackFactory()
    {
    }

    public Task<IBackgroundVideoPlayback> StartAsync(
        VideoSignalSourceSelection selection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<IBackgroundVideoPlayback>(
            new NotSupportedException("Background video signal playback is unavailable."));
    }
}

internal sealed class BackgroundSignalPlaybackFactory : IBackgroundSignalPlaybackFactory
{
    private readonly VideoSignalRegistry registry;
    private readonly BackgroundPresentationDispatcher dispatcher;
    private readonly ILogger<BackgroundSignalPlaybackFactory> logger;

    internal BackgroundSignalPlaybackFactory(
        VideoSignalRegistry registry,
        BackgroundPresentationDispatcher dispatcher,
        ILogger<BackgroundSignalPlaybackFactory>? logger = null)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.logger = logger ?? NullLogger<BackgroundSignalPlaybackFactory>.Instance;
    }

    public async Task<IBackgroundVideoPlayback> StartAsync(
        VideoSignalSourceSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        IVideoSignalProtocolAdapter adapter = registry.GetRequiredAdapter(selection.Protocol);
        IReadOnlyList<VideoSignalSourceDescriptor> sources = await adapter.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        VideoSignalSourceDescriptor source = sources.FirstOrDefault(candidate =>
            StringComparer.Ordinal.Equals(candidate.Id, selection.SourceId))
            ?? throw new InvalidOperationException("The selected video signal source is unavailable.");
        var playback = new SignalBackgroundPlayback(
            new VideoSignalReceiverLifecycle(registry),
            dispatcher,
            logger);
        VideoSignalConnectionSnapshot snapshot = await playback.StartAsync(source, cancellationToken).ConfigureAwait(false);
        if (snapshot.State != VideoSignalState.Ready)
        {
            await playback.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"The video signal source could not start: {snapshot.ErrorType ?? "Unknown"}.");
        }

        return playback;
    }
}

internal static partial class SignalBackgroundPlaybackLog
{
    [LoggerMessage(6830, LogLevel.Information, "Signal background playback started for {Protocol}:{SourceId}; state={State}")]
    internal static partial void Started(ILogger logger, VideoSignalProtocol protocol, string sourceId, VideoSignalState state);

    [LoggerMessage(6831, LogLevel.Warning, "Signal background frame consumer failed with {ErrorType}")]
    internal static partial void ConsumeFailed(ILogger logger, string errorType);

    [LoggerMessage(6832, LogLevel.Warning, "Signal background playback stop incomplete because of {ErrorType}")]
    internal static partial void StopIncomplete(ILogger logger, string errorType);
}
