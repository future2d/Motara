using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Core.Configuration;
using Motara.Tracking.Abstractions;

namespace Motara.Core.Sessions;

/// <summary>Controls one latest-frame processing session and exposes its snapshots.</summary>
public interface ISessionController
{
    /// <summary>Gets the latest immutable session snapshot.</summary>
    SessionSnapshot Current { get; }

    /// <summary>Watches future immutable session snapshots until cancellation.</summary>
    IAsyncEnumerable<SessionSnapshot> WatchSnapshotsAsync(CancellationToken cancellationToken);

    /// <summary>Starts the session scheduler.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Stops the session scheduler.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>Runs a session engine on a 60 Hz, time-provider-driven scheduler.</summary>
public sealed class ProcessingSession : ISessionController, IAsyncDisposable
{
    private static readonly TimeSpan SchedulerInterval = TimeSpan.FromSeconds(1d / 60d);
    private static readonly TimeSpan WatcherPublicationInterval = SchedulerInterval + SchedulerInterval;
    private readonly SessionEngine engine;
    private readonly LatestFrameIngress ingress;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ProcessingSession> logger;
    private readonly long timestampOrigin;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object snapshotGate = new();
    private readonly List<Channel<SessionSnapshot>> watchers = [];
    private SessionSnapshot current;
    private CancellationTokenSource? schedulerCancellation;
    private Task? schedulerTask;
    private ITimer? watcherPublicationTimer;
    private SessionSnapshot? pendingWatcherSnapshot;
    private TimeSpan? lastWatcherPublicationAt;
    private long publishedRevision;
    private int publishing;
    private int disposed;

    /// <summary>Creates a stopped session with an immutable disconnected snapshot.</summary>
    public ProcessingSession(
        SessionEngine engine,
        LatestFrameIngress ingress,
        TimeProvider timeProvider)
        : this(engine, ingress, timeProvider, NullLogger<ProcessingSession>.Instance)
    {
    }

    public ProcessingSession(
        SessionEngine engine,
        LatestFrameIngress ingress,
        TimeProvider timeProvider,
        ILogger<ProcessingSession>? logger)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? NullLogger<ProcessingSession>.Instance;
        timestampOrigin = timeProvider.GetTimestamp();
        current = engine.Tick(GetMonotonicTimestamp(), timeProvider.GetUtcNow());
        publishedRevision = current.Revision;
    }

    /// <inheritdoc />
    public SessionSnapshot Current
    {
        get
        {
            lock (snapshotGate)
            {
                return current;
            }
        }
    }

    /// <summary>
    /// Queues a raw tracking frame for latest-frame processing.
    /// Only one tracking publisher may call this boundary at a time; competing producers are rejected.
    /// </summary>
    public void Publish(RawTrackingFrame frame)
    {
        if (Interlocked.CompareExchange(ref publishing, 1, 0) != 0)
        {
            throw new InvalidOperationException("A processing session accepts frames from only one active publisher.");
        }

        try
        {
            ingress.Publish(frame);
        }
        finally
        {
            Volatile.Write(ref publishing, 0);
        }
    }

    /// <summary>Replaces source slot routing and publishes a neutral snapshot before a new publisher starts.</summary>
    public int ReplaceConfiguration(PipelineConfiguration configuration)
    {
        int discardedFrameCount = engine.ReplaceConfiguration(configuration);
        PublishNeutralSnapshot(ModuleState.Disconnected);
        ProcessingSessionLog.InputReset(logger, discardedFrameCount);
        return discardedFrameCount;
    }

    /// <summary>Clears the active source input and immediately publishes neutral parameters.</summary>
    public int ResetInput()
    {
        int discardedFrameCount = engine.ResetInput();
        PublishNeutralSnapshot(ModuleState.Disconnected);
        ProcessingSessionLog.InputReset(logger, discardedFrameCount);
        return discardedFrameCount;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SessionSnapshot> WatchSnapshotsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var watcher = Channel.CreateBounded<SessionSnapshot>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            });

        lock (snapshotGate)
        {
            watchers.Add(watcher);
        }

        try
        {
            await foreach (SessionSnapshot snapshot in watcher.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return snapshot;
            }
        }
        finally
        {
            lock (snapshotGate)
            {
                watchers.Remove(watcher);
            }

            watcher.Writer.TryComplete();
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (schedulerTask is not null && !schedulerTask.IsCompleted)
            {
                return;
            }

            if (schedulerTask is not null)
            {
                schedulerCancellation?.Dispose();
                schedulerCancellation = null;
                schedulerTask = null;
                ProcessingSessionLog.Restarted(logger);
            }

            PublishLifecycleSnapshot(ModuleState.Connecting);
            schedulerCancellation = new CancellationTokenSource();
            schedulerTask = RunSchedulerAsync(schedulerCancellation.Token);
            ProcessingSessionLog.Started(logger);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Task? task = schedulerTask;
            CancellationTokenSource? cancellation = schedulerCancellation;
            if (task is null)
            {
                return;
            }

            cancellation!.Cancel();
            await task.ConfigureAwait(false);
            cancellation.Dispose();
            schedulerCancellation = null;
            schedulerTask = null;
            PublishLifecycleSnapshot(ModuleState.Disconnected);
            ProcessingSessionLog.Stopped(logger);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        lock (snapshotGate)
        {
            watcherPublicationTimer?.Dispose();
            watcherPublicationTimer = null;
            pendingWatcherSnapshot = null;
        }

        lifecycleGate.Dispose();
    }

    private async Task RunSchedulerAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(SchedulerInterval, timeProvider);
            long tick = 0;
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                SessionSnapshot snapshot = engine.Tick(GetMonotonicTimestamp(), timeProvider.GetUtcNow());
                PublishEngineSnapshot(snapshot, ++tick % 2 == 0);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            int discardedFrameCount = engine.ResetInput();
            ProcessingSessionLog.Faulted(logger, exception.GetType().Name);
            ProcessingSessionLog.InputReset(logger, discardedFrameCount);
            PublishNeutralSnapshot(ModuleState.Faulted);
        }
    }

    private TimeSpan GetMonotonicTimestamp() => timeProvider.GetElapsedTime(
        timestampOrigin,
        timeProvider.GetTimestamp());

    private void PublishEngineSnapshot(SessionSnapshot snapshot, bool notifyWatchers)
    {
        lock (snapshotGate)
        {
            ModuleState trackingState = current.TrackingState == ModuleState.Connecting
                && snapshot.TrackingState == ModuleState.Disconnected
                ? ModuleState.Connecting
                : snapshot.TrackingState;
            current = snapshot with
            {
                Revision = NextRevision(snapshot.Revision),
                TrackingState = trackingState,
            };
            if (notifyWatchers)
            {
                QueueWatcherSnapshot(current);
            }
        }
    }

    private void PublishLifecycleSnapshot(ModuleState state)
    {
        lock (snapshotGate)
        {
            current = current with { Revision = NextRevision(current.Revision), TrackingState = state };
            QueueWatcherSnapshot(current);
        }
    }

    private void PublishNeutralSnapshot(ModuleState state)
    {
        SessionSnapshot snapshot = engine.Tick(GetMonotonicTimestamp(), timeProvider.GetUtcNow());
        lock (snapshotGate)
        {
            current = snapshot with
            {
                Revision = NextRevision(snapshot.Revision),
                TrackingState = state,
            };
            QueueWatcherSnapshot(current);
        }
    }

    private long NextRevision(long candidate) => publishedRevision = Math.Max(
        checked(publishedRevision + 1),
        candidate);

    private void QueueWatcherSnapshot(SessionSnapshot snapshot)
    {
        TimeSpan now = GetMonotonicTimestamp();
        if (!lastWatcherPublicationAt.HasValue
            || now - lastWatcherPublicationAt.Value >= WatcherPublicationInterval)
        {
            PublishWatcherSnapshot(snapshot, now);
            return;
        }

        pendingWatcherSnapshot = snapshot;
        ScheduleWatcherPublication(now);
    }

    private void ScheduleWatcherPublication(TimeSpan now)
    {
        if (watcherPublicationTimer is not null)
        {
            return;
        }

        TimeSpan dueTime = WatcherPublicationInterval - (now - lastWatcherPublicationAt!.Value);
        watcherPublicationTimer = timeProvider.CreateTimer(
            static state => ((ProcessingSession)state!).FlushPendingWatcherSnapshot(),
            this,
            dueTime,
            Timeout.InfiniteTimeSpan);
    }

    private void FlushPendingWatcherSnapshot()
    {
        lock (snapshotGate)
        {
            if (pendingWatcherSnapshot is null)
            {
                watcherPublicationTimer?.Dispose();
                watcherPublicationTimer = null;
                return;
            }

            TimeSpan now = GetMonotonicTimestamp();
            if (now - lastWatcherPublicationAt!.Value < WatcherPublicationInterval)
            {
                watcherPublicationTimer?.Dispose();
                watcherPublicationTimer = null;
                ScheduleWatcherPublication(now);
                return;
            }

            PublishWatcherSnapshot(pendingWatcherSnapshot, now);
        }
    }

    private void PublishWatcherSnapshot(SessionSnapshot snapshot, TimeSpan publishedAt)
    {
        pendingWatcherSnapshot = null;
        lastWatcherPublicationAt = publishedAt;
        watcherPublicationTimer?.Dispose();
        watcherPublicationTimer = null;

        foreach (Channel<SessionSnapshot> watcher in watchers)
        {
            watcher.Writer.TryWrite(snapshot);
        }
    }
}

internal static partial class ProcessingSessionLog
{
    [LoggerMessage(3000, LogLevel.Information, "Processing session started")]
    internal static partial void Started(ILogger logger);

    [LoggerMessage(3001, LogLevel.Information, "Processing session stopped")]
    internal static partial void Stopped(ILogger logger);

    [LoggerMessage(3002, LogLevel.Error, "Processing session faulted with {ExceptionType}")]
    internal static partial void Faulted(ILogger logger, string exceptionType);

    [LoggerMessage(3003, LogLevel.Information, "Processing session input reset; discarded {DiscardedFrameCount} pending frame(s)")]
    internal static partial void InputReset(ILogger logger, int discardedFrameCount);

    [LoggerMessage(3004, LogLevel.Warning, "Processing session restarted after a completed scheduler")]
    internal static partial void Restarted(ILogger logger);
}
