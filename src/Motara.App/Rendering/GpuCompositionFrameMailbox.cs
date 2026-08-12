namespace Motara.App.Rendering;

internal readonly record struct GpuCompositionFrameSnapshot<T>(
    long Generation,
    long PresentationEpoch,
    T? Value);

internal sealed class GpuCompositionFrameMailbox<T> : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim signal = new(0, 1);
    private long generation;
    private long presentationEpoch;
    private T? latest;
    private int signalPending;
    private int disposed;

    internal bool HasPendingSignal => Volatile.Read(ref signalPending) != 0;

    internal long Publish(T? value, bool invalidatePresentation = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        long next;
        lock (gate)
        {
            next = ++generation;
            if (invalidatePresentation)
            {
                presentationEpoch++;
            }
            latest = value;
        }

        Wake();

        return next;
    }

    internal long Clear() => Publish(default, invalidatePresentation: true);

    internal void Wake()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref signalPending, 1) == 0)
        {
            signal.Release();
        }
    }

    internal async Task<GpuCompositionFrameSnapshot<T>> ReadLatestAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref signalPending, 0);
        lock (gate)
        {
            return new GpuCompositionFrameSnapshot<T>(generation, presentationEpoch, latest);
        }
    }

    internal GpuCompositionFrameSnapshot<T> ReadCurrent()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        lock (gate)
        {
            return new GpuCompositionFrameSnapshot<T>(generation, presentationEpoch, latest);
        }
    }

    internal bool IsCurrent(long expectedGeneration)
    {
        lock (gate)
        {
            return expectedGeneration == generation;
        }
    }

    internal bool IsCurrentPresentation(long expectedEpoch)
    {
        lock (gate)
        {
            return expectedEpoch == presentationEpoch;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        signal.Dispose();
    }
}
