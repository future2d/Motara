namespace Motara.App.Rendering;

internal sealed record GpuRenderedFrame(
    int OutputIndex,
    long Generation,
    long ProducedAt,
    object Image);

internal sealed class CompositionFramePresenter : IDisposable
{
    private readonly GpuCompositionFrameMailbox<GpuRenderedFrame> mailbox = new();
    private int disposed;

    internal void Publish(GpuRenderedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        mailbox.Publish(frame);
    }

    internal GpuRenderedFrame ReadCurrent()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return mailbox.ReadCurrent().Value
            ?? throw new InvalidOperationException("No GPU frame has been published.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            mailbox.Dispose();
        }
    }
}
