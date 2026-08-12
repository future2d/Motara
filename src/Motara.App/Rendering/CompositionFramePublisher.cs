using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Media;

namespace Motara.App.Rendering;

internal sealed class CompositionFramePublisher : IAsyncDisposable
{
    private readonly Dictionary<string, Target> targets = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly ILogger logger;
    private int disposed;

    internal CompositionFramePublisher(ILogger<CompositionFramePublisher>? logger = null)
    {
        this.logger = logger ?? NullLogger<CompositionFramePublisher>.Instance;
    }

    internal bool HasTargets
    {
        get
        {
            lock (gate)
            {
                return targets.Count != 0;
            }
        }
    }

    internal async Task AddTargetAsync(
        string targetId,
        IVideoSignalSender sender,
        VideoSignalOutputOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var target = new Target(targetId, sender, logger);
        lock (gate)
        {
            if (targets.ContainsKey(targetId))
            {
                throw new ArgumentException("A composition output target with this ID already exists.", nameof(targetId));
            }

            targets.Add(targetId, target);
        }

        try
        {
            await target.StartAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (gate)
            {
                targets.Remove(targetId);
            }
            await target.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task RemoveTargetAsync(string targetId, CancellationToken cancellationToken)
    {
        Target? target;
        lock (gate)
        {
            targets.Remove(targetId, out target);
        }

        if (target is not null)
        {
            await target.StopAsync(cancellationToken).ConfigureAwait(false);
            await target.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal void Publish(SignalFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Target[] current;
        lock (gate)
        {
            current = [.. targets.Values];
        }

        if (current.Length == 0 || frame.Pixels.IsEmpty)
        {
            return;
        }

        foreach (Target target in current)
        {
            SignalFrame copy = SignalFrame.CopyFrom(
                frame.Metadata.Width,
                frame.Metadata.Height,
                frame.Metadata.PixelFormat,
                frame.Pixels.Span,
                frame.Metadata.Sequence,
                frame.Metadata.Timestamp,
                frame.Metadata.HasAlpha);
            if (!target.Publish(copy))
            {
                copy.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Target[] current;
        lock (gate)
        {
            current = [.. targets.Values];
            targets.Clear();
        }

        foreach (Target target in current)
        {
            await target.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Target : IAsyncDisposable
    {
        private readonly IVideoSignalSender sender;
        private readonly LatestSignalFrameMailbox mailbox = new();
        private readonly SemaphoreSlim wake = new(0);
        private readonly CancellationTokenSource cancellation = new();
        private readonly ILogger logger;
        private Task? worker;
        private int stopped;

        internal Target(string id, IVideoSignalSender sender, ILogger logger)
        {
            Id = id;
            this.sender = sender;
            this.logger = logger;
        }

        private string Id { get; }

        internal async Task StartAsync(VideoSignalOutputOptions options, CancellationToken cancellationToken)
        {
            try
            {
                await sender.StartAsync(options, cancellationToken).ConfigureAwait(false);
                if (sender.State != VideoSignalState.Ready)
                {
                    throw new InvalidOperationException(
                        $"Video signal sender did not become ready; state={sender.State}.");
                }

                worker = Task.Run(ConsumeAsync, CancellationToken.None);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                CompositionFramePublisherLog.TargetStartFailed(logger, Id, exception.GetType().Name);
                throw;
            }
        }

        internal bool Publish(SignalFrame frame)
        {
            if (Volatile.Read(ref stopped) != 0)
            {
                return false;
            }

            bool accepted = mailbox.Publish(frame);
            if (accepted)
            {
                wake.Release();
            }
            return accepted;
        }

        internal async Task StopAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0)
            {
                return;
            }

            cancellation.Cancel();
            wake.Release();
            if (worker is not null)
            {
                await worker.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }

            mailbox.Complete();
            await sender.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                mailbox.Dispose();
                wake.Dispose();
                cancellation.Dispose();
                await sender.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task ConsumeAsync()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    await wake.WaitAsync(cancellation.Token).ConfigureAwait(false);
                    while (mailbox.ReadLatest() is { } frame)
                    {
                        using (frame)
                        {
                            await sender.PublishAsync(frame, cancellation.Token).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                CompositionFramePublisherLog.TargetFailed(logger, Id, exception.GetType().Name);
            }
        }
    }
}

internal static partial class CompositionFramePublisherLog
{
    [LoggerMessage(6850, LogLevel.Warning, "Composition output target {TargetId} failed with {ErrorType}")]
    internal static partial void TargetFailed(ILogger logger, string targetId, string errorType);

    [LoggerMessage(6851, LogLevel.Warning, "Composition output target {TargetId} could not start: {ErrorType}")]
    internal static partial void TargetStartFailed(ILogger logger, string targetId, string errorType);
}
