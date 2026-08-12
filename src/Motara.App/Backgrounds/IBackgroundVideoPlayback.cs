using Avalonia.Media.Imaging;
using Motara.Media;

namespace Motara.App.Backgrounds;

internal sealed record BackgroundVideoFrameSnapshot(
    int Width,
    int Height,
    byte[] BgraPixels);

internal interface IBackgroundVideoPlayback : IAsyncDisposable
{
    event EventHandler? FrameChanged;

    Bitmap Bitmap { get; }

    BackgroundVideoFrameSnapshot? CaptureCurrentFrame();
}

internal interface IBackgroundVideoPlaybackFactory
{
    Task<IBackgroundVideoPlayback> StartAsync(
        string assetId,
        CancellationToken cancellationToken);

    Task<IBackgroundVideoPlayback> StartAsync(
        string assetId,
        BackgroundVideoOptions options,
        CancellationToken cancellationToken) =>
        StartAsync(assetId, cancellationToken);
}

internal sealed class UnsupportedBackgroundVideoPlaybackFactory : IBackgroundVideoPlaybackFactory
{
    internal static UnsupportedBackgroundVideoPlaybackFactory Instance { get; } = new();

    private UnsupportedBackgroundVideoPlaybackFactory()
    {
    }

    public Task<IBackgroundVideoPlayback> StartAsync(
        string assetId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<IBackgroundVideoPlayback>(
            new NotSupportedException("Background video playback is unavailable."));
    }
}
