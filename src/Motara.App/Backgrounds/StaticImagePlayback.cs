using Avalonia.Media.Imaging;

namespace Motara.App.Backgrounds;

internal sealed class StaticImagePlayback : IBackgroundVideoPlayback
{
    private BackgroundImageResource? resource;

    internal StaticImagePlayback(BackgroundImageResource resource)
    {
        this.resource = resource ?? throw new ArgumentNullException(nameof(resource));
    }

    public event EventHandler? FrameChanged
    {
        add { }
        remove { }
    }

    public Bitmap Bitmap => resource?.Bitmap ?? throw new ObjectDisposedException(nameof(StaticImagePlayback));

    public BackgroundVideoFrameSnapshot? CaptureCurrentFrame() => null;

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref resource, null)?.Dispose();
        return ValueTask.CompletedTask;
    }
}
