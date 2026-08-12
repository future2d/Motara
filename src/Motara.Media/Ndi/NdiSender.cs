using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Motara.Media.Ndi;

internal sealed class NdiSender : IVideoSignalSender
{
    private readonly INdiInterop interop;
    private readonly ILogger logger;
    private INdiSenderSession? session;
    private int state = (int)VideoSignalState.Stopped;
    private int disposed;

    internal NdiSender(INdiInterop interop, ILogger? logger = null)
    {
        this.interop = interop ?? throw new ArgumentNullException(nameof(interop));
        this.logger = logger ?? NullLogger.Instance;
    }

    public VideoSignalState State => (VideoSignalState)Volatile.Read(ref state);
    public event EventHandler<VideoSignalStateChangedEventArgs>? StateChanged;

    public Task StartAsync(VideoSignalOutputOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        SetState(VideoSignalState.Starting);
        if (!interop.TryOpenSender(options, out INdiSenderSession sender, out string? errorType))
        {
            SetState(VideoSignalState.Faulted, errorType);
            NdiLog.SenderFailed(logger, errorType ?? "OpenFailed");
            throw new InvalidOperationException($"NDI sender could not be opened: {errorType ?? "OpenFailed"}.");
        }

        session = sender;
        SetState(VideoSignalState.Ready);
        return Task.CompletedTask;
    }

    public ValueTask PublishAsync(SignalFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        if (State != VideoSignalState.Ready || Volatile.Read(ref session) is not { } active)
        {
            return ValueTask.CompletedTask;
        }

        if (!active.TrySend(frame))
        {
            NdiLog.SenderFailed(logger, "SendFailed");
            SetState(VideoSignalState.Faulted, "SendFailed");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref session, null)?.Dispose();
        SetState(VideoSignalState.Stopped);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
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
