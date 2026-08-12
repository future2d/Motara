using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Media;
using Motara.Persistence;
using System.Threading.Channels;

namespace Motara.App.Backgrounds;

internal sealed class BackgroundVideoPlaybackFactory : IBackgroundVideoPlaybackFactory
{
    private readonly IBackgroundAssetStore assetStore;
    private readonly IVideoDecoder decoder;
    private readonly ILogger logger;
    private readonly BackgroundPresentationDispatcher dispatcher;

    internal BackgroundVideoPlaybackFactory(
        IBackgroundAssetStore assetStore,
        IVideoDecoder decoder,
        ILogger logger,
        BackgroundPresentationDispatcher dispatcher)
    {
        this.assetStore = assetStore ?? throw new ArgumentNullException(nameof(assetStore));
        this.decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        this.logger = logger ?? NullLogger.Instance;
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async Task<IBackgroundVideoPlayback> StartAsync(
        string assetId,
        CancellationToken cancellationToken)
        => await StartAsync(assetId, BackgroundVideoOptions.Default, cancellationToken).ConfigureAwait(false);

    public async Task<IBackgroundVideoPlayback> StartAsync(
        string assetId,
        BackgroundVideoOptions options,
        CancellationToken cancellationToken)
    {
        BackgroundDefinition.ValidateVideoAssetId(assetId);
        string path = assetStore.GetManagedVideoPath(assetId);
        VideoStreamInfo stream = await decoder.ProbeAsync(path, cancellationToken)
            .ConfigureAwait(false);
        if (stream.Width <= 0
            || stream.Height <= 0
            || (long)stream.Width * stream.Height > 64L * 1024 * 1024
            || !double.IsFinite(stream.FramesPerSecond)
            || stream.FramesPerSecond <= 0)
        {
            throw new InvalidDataException("Video stream dimensions or frame rate are invalid.");
        }

        WriteableBitmap bitmap = await CreateBitmapAsync(
            stream.Width,
            stream.Height,
            cancellationToken).ConfigureAwait(false);
        return new BackgroundVideoPlayback(
            path,
            stream,
            decoder,
            bitmap,
            logger,
            dispatcher,
            options);
    }

    private async Task<WriteableBitmap> CreateBitmapAsync(
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        WriteableBitmap? result = null;
        await dispatcher.InvokeAsync(
            () => result = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul),
            cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("Video bitmap creation did not complete.");
    }
}

internal sealed class BackgroundVideoPlayback : IBackgroundVideoPlayback
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private readonly string path;
    private readonly VideoStreamInfo stream;
    private readonly IVideoDecoder decoder;
    private readonly WriteableBitmap bitmap;
    private readonly ILogger logger;
    private readonly BackgroundPresentationDispatcher dispatcher;
    private readonly BackgroundVideoOptions options;
    private readonly LatestVideoFrameMailbox mailbox = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly object frameGate = new();
    private readonly Task producer;
    private readonly Task consumer;
    private byte[]? currentPixels;
    private int disposed;

    internal BackgroundVideoPlayback(
        string path,
        VideoStreamInfo stream,
        IVideoDecoder decoder,
        WriteableBitmap bitmap,
        ILogger logger,
        BackgroundPresentationDispatcher dispatcher,
        BackgroundVideoOptions options)
    {
        this.path = path;
        this.stream = stream;
        this.decoder = decoder;
        this.bitmap = bitmap;
        this.logger = logger;
        this.dispatcher = dispatcher;
        this.options = options;
        producer = ProduceAsync();
        consumer = ConsumeAsync();
        BackgroundVideoPlaybackLog.Started(logger, stream.Width, stream.Height, stream.FramesPerSecond, stream.HasAlpha);
    }

    public event EventHandler? FrameChanged;

    public Bitmap Bitmap => bitmap;

    public BackgroundVideoFrameSnapshot? CaptureCurrentFrame()
    {
        lock (frameGate)
        {
            return currentPixels is null
                ? null
                : new BackgroundVideoFrameSnapshot(stream.Width, stream.Height, (byte[])currentPixels.Clone());
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        cancellation.Cancel();
        mailbox.Complete();
        try
        {
            await Task.WhenAll(producer, consumer).WaitAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            BackgroundVideoPlaybackLog.StopIncomplete(logger, exception.GetType().Name);
        }

        await dispatcher.InvokeAsync(bitmap.Dispose, CancellationToken.None).ConfigureAwait(false);
        mailbox.Dispose();
        cancellation.Dispose();
        lock (frameGate)
        {
            currentPixels = null;
        }

        BackgroundVideoPlaybackLog.Stopped(logger);
    }

    private async Task ProduceAsync()
    {
        try
        {
            await foreach (VideoFrame frame in decoder.DecodeLoopAsync(path, stream, options, cancellation.Token)
                .WithCancellation(cancellation.Token)
                .ConfigureAwait(false))
            {
                VideoFrame? pending = frame;
                try
                {
                    await mailbox.PublishAsync(frame, cancellation.Token).ConfigureAwait(false);
                    pending = null;
                }
                finally
                {
                    pending?.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            BackgroundVideoPlaybackLog.DecodeFailed(logger, exception.GetType().Name);
            mailbox.Complete();
        }
    }

    private async Task ConsumeAsync()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                using VideoFrame frame = await mailbox.ReadAsync(cancellation.Token).ConfigureAwait(false);
                byte[] pixels = frame.Pixels.ToArray();
                lock (frameGate)
                {
                    currentPixels = pixels;
                }

                await dispatcher.InvokeAsync(
                    () =>
                    {
                        using ILockedFramebuffer locked = bitmap.Lock();
                        int rowBytes = stream.Width * 4;
                        for (int row = 0; row < stream.Height; row++)
                        {
                            Marshal.Copy(
                                pixels,
                                row * rowBytes,
                                locked.Address + row * locked.RowBytes,
                                rowBytes);
                        }

                        FrameChanged?.Invoke(this, EventArgs.Empty);
                    },
                    cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }
}

internal static partial class BackgroundVideoPlaybackLog
{
    [LoggerMessage(6790, LogLevel.Information, "Background video playback started at {Width}x{Height}, {FramesPerSecond} FPS, alpha={HasAlpha}")]
    internal static partial void Started(ILogger logger, int width, int height, double framesPerSecond, bool hasAlpha);

    [LoggerMessage(6791, LogLevel.Warning, "Background video decode failed with {ErrorType}")]
    internal static partial void DecodeFailed(ILogger logger, string errorType);

    [LoggerMessage(6792, LogLevel.Warning, "Background video playback stop was incomplete because of {ErrorType}")]
    internal static partial void StopIncomplete(ILogger logger, string errorType);

    [LoggerMessage(6793, LogLevel.Information, "Background video playback stopped")]
    internal static partial void Stopped(ILogger logger);
}
