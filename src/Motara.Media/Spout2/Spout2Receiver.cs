using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Motara.Media.Spout2;

internal sealed class Spout2Receiver : IVideoSignalReceiver
{
    private readonly ISpout2Interop interop;
    private readonly ILogger logger;
    private readonly LatestSignalFrameMailbox mailbox = new();
    private readonly CancellationTokenSource cancellation = new();
    private ISpout2ReceiverSession? session;
    private Task? receiveTask;
    private int state = (int)VideoSignalState.Stopped;
    private long sequence;
    private int disposed;

    internal Spout2Receiver(ISpout2Interop interop, ILogger? logger = null)
    {
        this.interop = interop ?? throw new ArgumentNullException(nameof(interop));
        this.logger = logger ?? NullLogger.Instance;
    }

    public VideoSignalState State => (VideoSignalState)Volatile.Read(ref state);
    public event EventHandler<VideoSignalStateChangedEventArgs>? StateChanged;

    public Task StartAsync(VideoSignalSourceDescriptor source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        return StartCoreAsync(source, cancellationToken);
    }

    public SignalFrame? ReadLatest() => mailbox.ReadLatest();

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        cancellation.Cancel();
        mailbox.Complete();
        if (receiveTask is not null)
        {
            await receiveTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        }

        Interlocked.Exchange(ref session, null)?.Dispose();
        SetState(VideoSignalState.Stopped);
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
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            Spout2Log.ReceiverFailed(logger, exception.GetType().Name);
        }

        cancellation.Dispose();
        mailbox.Dispose();
    }

    private async Task StartCoreAsync(VideoSignalSourceDescriptor source, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        SetState(VideoSignalState.Starting);
        if (!interop.TryOpenReceiver(source.Id, out ISpout2ReceiverSession receiver, out string? errorType))
        {
            SetState(VideoSignalState.Faulted, errorType);
            Spout2Log.ReceiverFailed(logger, errorType ?? "OpenFailed");
            return;
        }

        session = receiver;
        SetState(VideoSignalState.Ready);
        receiveTask = ReceiveLoopAsync(cancellationToken);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken startCancellation)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, startCancellation);
        CancellationToken token = linked.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                ISpout2ReceiverSession? active = Volatile.Read(ref session);
                if (active is null)
                {
                    return;
                }

                if (active.TryReceive(out Spout2ReceivedFrame frame))
                {
                    SignalFrame next = SignalFrame.CopyFrom(
                        frame.Width,
                        frame.Height,
                        SignalPixelFormat.Bgra8888,
                        frame.Pixels.Span,
                        Interlocked.Increment(ref sequence),
                        frame.Timestamp,
                        frame.HasAlpha);
                    if (!mailbox.Publish(next))
                    {
                        return;
                    }
                }
                else
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(2), token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetState(VideoSignalState.Faulted, exception.GetType().Name);
            Spout2Log.ReceiverFailed(logger, exception.GetType().Name);
        }
    }

    private void SetState(VideoSignalState next, string? errorType = null)
    {
        Interlocked.Exchange(ref state, (int)next);
        StateChanged?.Invoke(this, new VideoSignalStateChangedEventArgs(
            next,
            errorType is null ? null : new InvalidOperationException(errorType)));
    }
}
