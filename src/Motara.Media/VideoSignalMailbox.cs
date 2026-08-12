namespace Motara.Media;

public sealed class LatestSignalFrameMailbox : IDisposable
{
    private SignalFrame? current;
    private int completed;
    private long droppedFrameCount;

    public long DroppedFrameCount => Interlocked.Read(ref droppedFrameCount);

    public bool Publish(SignalFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (Volatile.Read(ref completed) != 0)
        {
            frame.Dispose();
            return false;
        }

        SignalFrame? replaced = Interlocked.Exchange(ref current, frame);
        if (replaced is not null)
        {
            Interlocked.Increment(ref droppedFrameCount);
            replaced.Dispose();
        }

        if (Volatile.Read(ref completed) == 0)
        {
            return true;
        }

        SignalFrame? completedFrame = Interlocked.Exchange(ref current, null);
        if (completedFrame is not null)
        {
            completedFrame.Dispose();
        }

        return false;
    }

    public SignalFrame? ReadLatest() => Interlocked.Exchange(ref current, null);

    public void Complete()
    {
        if (Interlocked.Exchange(ref completed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref current, null)?.Dispose();
    }

    public void Dispose() => Complete();
}
