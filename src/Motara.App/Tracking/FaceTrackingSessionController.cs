using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Core.Configuration;
using Motara.Core.Parameters;
using Motara.Core.Processing;
using Motara.Core.Sessions;
using Motara.Tracking.Abstractions;

namespace Motara.App.Tracking;

internal enum TrackingSourceRunState
{
    None = 0,
    Switching = 1,
    Running = 2,
    Stopping = 3,
    Stopped = 4,
    Faulted = 5,
}

internal sealed record TrackingSourceStatus(
    TrackingSourceRunState State,
    string? IntendedSourceId,
    string? ActiveSourceId,
    string? ErrorCode,
    long ReceivedFrameCount,
    double FramesPerSecond,
    DateTimeOffset? LastFrameReceivedAtUtc)
{
    internal static TrackingSourceStatus Empty { get; } = new(
        TrackingSourceRunState.None,
        null,
        null,
        null,
        0,
        0,
        null);
}

/// <summary>Owns one channel's active source and canonical processing session.</summary>
internal sealed class FaceTrackingSessionController : ISessionController, IAsyncDisposable
{
    private static readonly TimeSpan StatusPublicationInterval = TimeSpan.FromMilliseconds(250);
    private readonly TrackingChannel channel;
    private readonly TrackingSourceRegistry registry;
    private readonly ProcessingSession session;
    private readonly ILogger<FaceTrackingSessionController> logger;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim switchGate = new(1, 1);
    private readonly object statusGate = new();
    private TrackingSourceStatus sourceStatus = TrackingSourceStatus.Empty;
    private ITrackingSource? activeSource;
    private CancellationTokenSource? sourceCancellation;
    private Task? sourceTask;
    private RawTrackingFrame? latestFrame;
    private PipelineConfiguration currentConfiguration;
    private long switchGeneration;
    private int disposed;

    internal FaceTrackingSessionController(
        TimeProvider timeProvider,
        TrackingSourceRegistry registry,
        ILogger<FaceTrackingSessionController>? logger = null,
        ILogger<ProcessingSession>? sessionLogger = null,
        TrackingChannel channel = TrackingChannel.Face)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        this.channel = channel;
        this.timeProvider = timeProvider;
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.logger = logger ?? NullLogger<FaceTrackingSessionController>.Instance;
        ParameterRegistry parameterRegistry = StandardParameterCatalog.Registry;
        var ingress = new LatestFrameIngress();
        var configuration = PipelineConfiguration.Create(
            parameterRegistry,
            sourceSlotCount: 3,
            slots:
            [
                CreateDefaultSlot(0, parameterRegistry.GetRequiredSlot("AngleX")),
                CreateDefaultSlot(1, parameterRegistry.GetRequiredSlot("AngleY")),
                CreateDefaultSlot(2, parameterRegistry.GetRequiredSlot("MouthOpenY")),
            ]);
        currentConfiguration = configuration;
        session = new ProcessingSession(
            new SessionEngine(new ParameterProcessor(configuration), parameterRegistry, ingress),
            ingress,
            timeProvider,
            sessionLogger);
    }

    internal event EventHandler? SourceStatusChanged;

    internal TrackingSourceRegistry Registry => registry;

    internal TrackingChannel Channel => channel;

    internal TrackingSourceStatus SourceStatus
    {
        get
        {
            lock (statusGate)
            {
                return sourceStatus;
            }
        }
    }

    public SessionSnapshot Current => session.Current;

    public IAsyncEnumerable<SessionSnapshot> WatchSnapshotsAsync(CancellationToken cancellationToken) =>
        session.WatchSnapshotsAsync(cancellationToken);

    internal async Task<TrackingCalibrationResult> CalibrateAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        FaceTrackingSourceLog.CalibrationRequested(logger, channel);
        await switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ITrackingSource? source = activeSource;
            if (source is null || SourceStatus.State != TrackingSourceRunState.Running)
            {
                FaceTrackingSourceLog.CalibrationRejected(logger, channel, "tracking.calibration.source_not_running");
                return TrackingCalibrationResult.Failure("tracking.calibration.source_not_running");
            }

            if (source is ITrackingSourceCalibration nativeCalibration)
            {
                TrackingCalibrationResult result = await nativeCalibration
                    .CalibrateAsync(cancellationToken)
                    .ConfigureAwait(false);
                FaceTrackingSourceLog.CalibrationCompleted(
                    logger,
                    channel,
                    nativeCalibration: true,
                    result.Succeeded,
                    result.ReasonCode);
                return result;
            }

            RawTrackingFrame? frame = Volatile.Read(ref latestFrame);
            if (frame is null || !StringComparer.Ordinal.Equals(frame.SourceId, source.SourceId))
            {
                FaceTrackingSourceLog.CalibrationRejected(logger, channel, "tracking.calibration.frame_unavailable");
                return TrackingCalibrationResult.Failure("tracking.calibration.frame_unavailable");
            }

            ParameterSlotConfiguration[] calibratedSlots = currentConfiguration.Slots
                .Select(slot => CalibrateSlot(slot, frame))
                .ToArray();
            if (calibratedSlots.SequenceEqual(currentConfiguration.Slots))
            {
                FaceTrackingSourceLog.CalibrationRejected(logger, channel, "tracking.calibration.frame_invalid");
                return TrackingCalibrationResult.Failure("tracking.calibration.frame_invalid");
            }

            currentConfiguration = PipelineConfiguration.Create(
                currentConfiguration.TargetRegistry,
                currentConfiguration.SourceSlotCount,
                calibratedSlots);
            session.ReplaceConfiguration(currentConfiguration);
            FaceTrackingSourceLog.CalibrationCompleted(
                logger,
                channel,
                nativeCalibration: false,
                succeeded: true,
                reasonCode: null);
            return TrackingCalibrationResult.Success;
        }
        finally
        {
            switchGate.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        TrackingSourceStatus status = SourceStatus;
        if (status.State == TrackingSourceRunState.Running)
        {
            return;
        }

        if (status.IntendedSourceId is string sourceId)
        {
            _ = await SelectSourceAsync(sourceId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        long generation = Interlocked.Increment(ref switchGeneration);
        string? intendedSourceId = SourceStatus.IntendedSourceId;
        SetStatus(SourceStatus with
        {
            State = TrackingSourceRunState.Stopping,
            ActiveSourceId = null,
            ErrorCode = null,
        });
        await switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            await StopActiveSourceAsync().ConfigureAwait(false);
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
            int discardedFrameCount = session.ResetInput();
            FaceTrackingSourceLog.InputReset(logger, channel, discardedFrameCount);
            if (generation == Volatile.Read(ref switchGeneration))
            {
                SetStatus(intendedSourceId is null
                    ? TrackingSourceStatus.Empty
                    : SourceStatus with
                    {
                        State = TrackingSourceRunState.Stopped,
                        IntendedSourceId = intendedSourceId,
                        ActiveSourceId = null,
                        ErrorCode = null,
                    });
                FaceTrackingSourceLog.Stopped(logger, channel);
            }
        }
        finally
        {
            switchGate.Release();
        }
    }

    internal async Task<bool> SelectSourceAsync(
        string? sourceId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        long generation = Interlocked.Increment(ref switchGeneration);
        SetStatus(new TrackingSourceStatus(
            sourceId is null ? TrackingSourceRunState.None : TrackingSourceRunState.Switching,
            sourceId,
            null,
            null,
            0,
            0,
            null));
        FaceTrackingSourceLog.SelectionRequested(logger, channel, sourceId is not null);

        await switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            await StopActiveSourceAsync().ConfigureAwait(false);
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
            int discardedFrameCount = session.ResetInput();
            FaceTrackingSourceLog.InputReset(logger, channel, discardedFrameCount);
            if (generation != Volatile.Read(ref switchGeneration))
            {
                FaceTrackingSourceLog.SelectionSuperseded(logger, channel);
                return false;
            }

            if (sourceId is null)
            {
                SetStatus(TrackingSourceStatus.Empty);
                FaceTrackingSourceLog.Stopped(logger, channel);
                return true;
            }

            if (!registry.TryGetFactory(sourceId, out ITrackingSourceFactory? factory)
                || factory is null
                || !factory.Descriptor.Channels.Contains(channel))
            {
                SetFaulted(sourceId, "tracking.source.unknown");
                return false;
            }

            TrackingSourceAvailability availability = await factory.CheckAvailabilityAsync(
                channel,
                cancellationToken).ConfigureAwait(false);
            if (!availability.IsAvailable)
            {
                SetFaulted(sourceId, availability.ReasonCode ?? "tracking.source.unavailable");
                return false;
            }

            ITrackingSource created = await factory.CreateAsync(
                channel,
                cancellationToken).ConfigureAwait(false);
            if (generation != Volatile.Read(ref switchGeneration))
            {
                await created.DisposeAsync().ConfigureAwait(false);
                return false;
            }

            ConfigureSourceLayout(created);

            await session.StartAsync(cancellationToken).ConfigureAwait(false);
            activeSource = created;
            sourceCancellation = new CancellationTokenSource();
            SetStatus(new TrackingSourceStatus(
                TrackingSourceRunState.Running,
                sourceId,
                sourceId,
                null,
                0,
                0,
                null));
            sourceTask = RunSourceAsync(created, sourceId, generation, sourceCancellation.Token);
            FaceTrackingSourceLog.Started(logger, channel);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0)
        {
            throw;
        }
        catch (Exception exception)
        {
            SetFaulted(sourceId, exception.GetType().Name);
            return false;
        }
        finally
        {
            switchGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref switchGeneration);
        await switchGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopActiveSourceAsync().ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            switchGate.Release();
            switchGate.Dispose();
        }
    }

    private async Task RunSourceAsync(
        ITrackingSource source,
        string sourceId,
        long generation,
        CancellationToken cancellationToken)
    {
        long count = 0;
        TimeSpan? firstTimestamp = null;
        long lastStatusPublicationTimestamp = 0;
        try
        {
            await foreach (RawTrackingFrame frame in source.ReadFramesAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                Volatile.Write(ref latestFrame, frame);
                session.Publish(frame);
                count++;
                firstTimestamp ??= frame.MonotonicTimestamp;
                double framesPerSecond = count > 1
                    ? (count - 1) / Math.Max(
                        (frame.MonotonicTimestamp - firstTimestamp.Value).TotalSeconds,
                        double.Epsilon)
                    : 0;
                long now = timeProvider.GetTimestamp();
                bool publishStatus = lastStatusPublicationTimestamp == 0
                    || timeProvider.GetElapsedTime(lastStatusPublicationTimestamp, now)
                        >= StatusPublicationInterval;
                SetStatus(new TrackingSourceStatus(
                    TrackingSourceRunState.Running,
                    sourceId,
                    sourceId,
                    null,
                    count,
                    framesPerSecond,
                    frame.ReceivedAtUtc),
                    publishStatus);
                if (publishStatus)
                {
                    lastStatusPublicationTimestamp = now;
                }
            }

            if (generation == Volatile.Read(ref switchGeneration))
            {
                SetStatus(SourceStatus with
                {
                    State = TrackingSourceRunState.Stopped,
                    ActiveSourceId = null,
                });
                FaceTrackingSourceLog.Stopped(logger, channel);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref switchGeneration))
            {
                SetFaulted(sourceId, exception.GetType().Name);
                FaceTrackingSourceLog.Faulted(logger, channel, exception.GetType().Name);
            }
        }
        finally
        {
            if (ReferenceEquals(
                Interlocked.CompareExchange(ref activeSource, null, source),
                source))
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task StopActiveSourceAsync()
    {
        CancellationTokenSource? cancellation = sourceCancellation;
        Task? task = sourceTask;
        ITrackingSource? source = Interlocked.Exchange(ref activeSource, null);
        Volatile.Write(ref latestFrame, null);
        sourceCancellation = null;
        sourceTask = null;
        cancellation?.Cancel();
        if (task is not null)
        {
            await task.ConfigureAwait(false);
        }

        cancellation?.Dispose();
        if (source is not null)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ConfigureSourceLayout(ITrackingSource source)
    {
        if (source is not ITrackingSourceOutputLayout layout || layout.OutputDefinitions.Count == 0)
        {
            return;
        }

        var definitions = StandardParameterCatalog.Definitions.ToList();
        var registered = definitions.ToDictionary(definition => definition.Id, StringComparer.Ordinal);
        foreach (TrackingOutputDefinition output in layout.OutputDefinitions)
        {
            if (registered.TryGetValue(output.Id, out ParameterDefinition? existing))
            {
                if (existing.NeutralValue != output.NeutralValue
                    || existing.SuggestedMinimum != output.SuggestedMinimum
                    || existing.SuggestedMaximum != output.SuggestedMaximum)
                {
                    throw new ArgumentException($"Tracking output conflicts with global parameter: {output.Id}");
                }

                continue;
            }

            var added = new ParameterDefinition(
                output.Id,
                output.NeutralValue,
                output.SuggestedMinimum,
                output.SuggestedMaximum,
                $"Parameter.SourceFormula.{output.Id}",
                ParameterDefinitionOrigin.SourceFormula);
            definitions.Add(added);
            registered.Add(added.Id, added);
        }

        ParameterRegistry parameterRegistry = ParameterRegistry.Create(definitions);
        var slots = new List<ParameterSlotConfiguration>(layout.OutputDefinitions.Count);
        for (int sourceSlot = 0; sourceSlot < layout.OutputDefinitions.Count; sourceSlot++)
        {
            int targetSlot = parameterRegistry.GetRequiredSlot(layout.OutputDefinitions[sourceSlot].Id);
            TrackingOutputDefinition output = layout.OutputDefinitions[sourceSlot];
            slots.Add(CreateDeclaredOutputSlot(
                sourceSlot,
                targetSlot,
                output));
        }

        currentConfiguration = PipelineConfiguration.Create(
            parameterRegistry,
            layout.OutputDefinitions.Count,
            slots);
        session.ReplaceConfiguration(currentConfiguration);
        FaceTrackingSourceLog.LayoutConfigured(
            logger,
            source.SourceId,
            layout.OutputDefinitions.Count,
            slots.Count);
    }

    private void SetFaulted(string? sourceId, string errorCode)
    {
        SetStatus(new TrackingSourceStatus(
            TrackingSourceRunState.Faulted,
            sourceId,
            null,
            errorCode,
            0,
            0,
            null));
        FaceTrackingSourceLog.Faulted(logger, channel, errorCode);
    }

    private void SetStatus(TrackingSourceStatus status, bool notify = true)
    {
        bool changed;
        lock (statusGate)
        {
            changed = sourceStatus != status;
            sourceStatus = status;
        }

        if (changed && notify)
        {
            SourceStatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static ParameterSlotConfiguration CreateDefaultSlot(
        int sourceSlot,
        int targetSlot,
        double smoothing = 0) => new(
        sourceSlot,
        targetSlot,
        NeutralOffset: 0,
        InputMinimum: -1,
        InputMaximum: 1,
        Direction: 1,
        DeadZone: 0,
        Clamp: true,
        EmaTimeConstant: smoothing <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(smoothing * 250),
        MaximumRatePerSecond: 0);

    private static ParameterSlotConfiguration CalibrateSlot(
        ParameterSlotConfiguration slot,
        RawTrackingFrame frame)
    {
        if ((uint)slot.SourceSlot >= (uint)frame.Values.Length
            || frame.Validity[slot.SourceSlot] != ParameterValidity.Valid
            || !double.IsFinite(frame.Values[slot.SourceSlot]))
        {
            return slot;
        }

        return slot with
        {
            CalibrationOffset = frame.Values[slot.SourceSlot] - slot.NeutralOffset,
        };
    }

    private static ParameterSlotConfiguration CreateDeclaredOutputSlot(
        int sourceSlot,
        int targetSlot,
        TrackingOutputDefinition output) => new(
        sourceSlot,
        targetSlot,
        output.NeutralValue,
        output.SuggestedMinimum,
        output.SuggestedMaximum,
        Direction: 1,
        DeadZone: 0,
        Clamp: true,
        EmaTimeConstant: output.Smoothing <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(output.Smoothing * 250),
        MaximumRatePerSecond: 0,
        PreserveInputScale: true);
}

internal static partial class FaceTrackingSourceLog
{
    [LoggerMessage(6300, LogLevel.Information, "Tracking source selection requested for {Channel}; source present: {HasSource}")]
    internal static partial void SelectionRequested(ILogger logger, TrackingChannel channel, bool hasSource);

    [LoggerMessage(6301, LogLevel.Information, "Tracking source started for {Channel}")]
    internal static partial void Started(ILogger logger, TrackingChannel channel);

    [LoggerMessage(6302, LogLevel.Information, "Tracking source stopped for {Channel}")]
    internal static partial void Stopped(ILogger logger, TrackingChannel channel);

    [LoggerMessage(6303, LogLevel.Warning, "Tracking source faulted for {Channel} with {ErrorCode}")]
    internal static partial void Faulted(ILogger logger, TrackingChannel channel, string errorCode);

    [LoggerMessage(6304, LogLevel.Debug, "Tracking source selection superseded for {Channel}")]
    internal static partial void SelectionSuperseded(ILogger logger, TrackingChannel channel);

    [LoggerMessage(6305, LogLevel.Information, "Tracking source layout configured for {SourceId}; outputs={OutputCount}, routes={RouteCount}")]
    internal static partial void LayoutConfigured(
        ILogger logger,
        string sourceId,
        int outputCount,
        int routeCount);

    [LoggerMessage(6306, LogLevel.Information, "Tracking source input reset for {Channel}; discarded {DiscardedFrameCount} pending frame(s)")]
    internal static partial void InputReset(ILogger logger, TrackingChannel channel, int discardedFrameCount);

    [LoggerMessage(6307, LogLevel.Information, "Tracking calibration requested for {Channel}")]
    internal static partial void CalibrationRequested(ILogger logger, TrackingChannel channel);

    [LoggerMessage(6308, LogLevel.Warning, "Tracking calibration rejected for {Channel} with {ReasonCode}")]
    internal static partial void CalibrationRejected(ILogger logger, TrackingChannel channel, string reasonCode);

    [LoggerMessage(6309, LogLevel.Information, "Tracking calibration completed for {Channel}; native={NativeCalibration}, succeeded={Succeeded}, reason={ReasonCode}")]
    internal static partial void CalibrationCompleted(
        ILogger logger,
        TrackingChannel channel,
        bool nativeCalibration,
        bool succeeded,
        string? reasonCode);
}
