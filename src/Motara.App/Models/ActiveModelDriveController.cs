using System.Threading.Channels;
using System.Collections.Immutable;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Animation;
using Motara.App.Parameters;
using Motara.App.Physics;
using Motara.Core.Sessions;
using Motara.ModelRuntime.Abstractions;
using Motara.Persistence;

namespace Motara.App.Models;

internal interface IActiveModelSource
{
    event EventHandler? ActiveChanged;

    ActiveModel? Active { get; }
}

internal sealed class ActiveModelDriveController : IAsyncDisposable
{
    private readonly ISessionController sessionController;
    private readonly IActiveModelSource modelSource;
    private readonly ILogger<ActiveModelDriveController> logger;
    private readonly ActiveModelParameterBindingSource? bindingSource;
    private readonly ParameterPriorityProfileSource prioritySource;
    private readonly TimeProvider timeProvider;
    private readonly ModelParameterObservationSource? observationSource;
    private readonly ActiveModelPhysicsSource? physicsSource;
    private readonly ActiveModelDragPhysicsSource? dragPhysicsSource;
    private readonly ActiveModelAnimationSource? animationSource;
    private readonly ActiveModelMotionExpansionSource? motionExpansionSource;
    private readonly Func<FrameRateMode> applicationFrameRateModeProvider;
    private readonly long startTimestamp;
    private readonly Channel<SessionSnapshot> snapshots;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task watchTask;
    private readonly Task driveTask;
    private readonly Task automationTask;
    private readonly object disposalGate = new();
    private Task? disposalTask;
    private bool automationEnabled;

    internal ActiveModelDriveController(
        ISessionController sessionController,
        IActiveModelSource modelSource,
        ILogger<ActiveModelDriveController>? logger = null,
        ActiveModelParameterBindingSource? bindingSource = null,
        ParameterPriorityProfileSource? prioritySource = null,
        TimeProvider? timeProvider = null,
        ModelParameterObservationSource? observationSource = null,
        ActiveModelPhysicsSource? physicsSource = null,
        Func<FrameRateMode>? applicationFrameRateModeProvider = null,
        ActiveModelMotionExpansionSource? motionExpansionSource = null,
        ActiveModelAnimationSource? animationSource = null,
        ActiveModelDragPhysicsSource? dragPhysicsSource = null)
    {
        this.sessionController = sessionController
            ?? throw new ArgumentNullException(nameof(sessionController));
        this.modelSource = modelSource ?? throw new ArgumentNullException(nameof(modelSource));
        this.logger = logger ?? NullLogger<ActiveModelDriveController>.Instance;
        this.bindingSource = bindingSource;
        this.prioritySource = prioritySource ?? new ParameterPriorityProfileSource();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.observationSource = observationSource;
        this.physicsSource = physicsSource;
        this.dragPhysicsSource = dragPhysicsSource;
        this.animationSource = animationSource;
        this.motionExpansionSource = motionExpansionSource;
        this.applicationFrameRateModeProvider = applicationFrameRateModeProvider
            ?? (static () => FrameRateMode.FramesPerSecond60);
        startTimestamp = this.timeProvider.GetTimestamp();
        snapshots = Channel.CreateBounded<SessionSnapshot>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            });
        modelSource.ActiveChanged += OnActiveModelChanged;
        if (bindingSource is not null)
        {
            bindingSource.Changed += OnBindingChanged;
        }
        this.prioritySource.Changed += OnPriorityChanged;
        if (physicsSource is not null) physicsSource.Changed += OnPhysicsChanged;
        if (dragPhysicsSource is not null) dragPhysicsSource.Changed += OnDragPhysicsChanged;
        if (animationSource is not null) animationSource.Changed += OnAnimationChanged;
        watchTask = WatchSnapshotsAsync(cancellation.Token);
        driveTask = DriveModelAsync(cancellation.Token);
        automationTask = WakeAutomationAsync(cancellation.Token);
        QueueLatestSnapshot();
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            disposalTask ??= DisposeCoreAsync();
            return new ValueTask(disposalTask);
        }
    }

    private void OnActiveModelChanged(object? sender, EventArgs args)
    {
        QueueLatestSnapshot();
        if (modelSource.Active is { } active)
        {
            if (bindingSource is not null) _ = bindingSource.ReloadAsync(active, cancellation.Token);
            if (physicsSource is not null) _ = physicsSource.ReloadAsync(active, cancellation.Token);
            if (animationSource is not null) _ = animationSource.ReloadAsync(active, cancellation.Token);
        }
    }

    private void OnBindingChanged(object? sender, EventArgs args) => QueueLatestSnapshot();

    private void OnPriorityChanged(object? sender, EventArgs args) => QueueLatestSnapshot();

    private void OnPhysicsChanged(object? sender, EventArgs args) => QueueLatestSnapshot();

    private void OnDragPhysicsChanged(object? sender, EventArgs args) => QueueLatestSnapshot();

    private void OnAnimationChanged(object? sender, EventArgs args) => QueueLatestSnapshot();

    private void QueueLatestSnapshot() => snapshots.Writer.TryWrite(sessionController.Current);

    private static void ApplyAnimationCommand(
        CubismAnimationEvaluator evaluator,
        ActiveModelAnimationCommand? command)
    {
        if (command is null) return;
        switch (command.Kind)
        {
            case ActiveModelAnimationCommandKind.Play:
                evaluator.PlayAsset(command.AssetId!);
                break;
            case ActiveModelAnimationCommandKind.SetIdle:
                evaluator.SetIdleAsset(command.AssetId!);
                break;
            case ActiveModelAnimationCommandKind.ClearIdle:
                evaluator.ClearIdle();
                break;
            case ActiveModelAnimationCommandKind.ToggleExpression:
                evaluator.ToggleExpressionAsset(command.AssetId!);
                break;
            case ActiveModelAnimationCommandKind.ClearExpressions:
                evaluator.ClearExpression();
                break;
            default:
                throw new InvalidOperationException("The animation shortcut command is unsupported.");
        }
    }

    private async Task WatchSnapshotsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (SessionSnapshot snapshot in sessionController
                .WatchSnapshotsAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                snapshots.Writer.TryWrite(snapshot);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task DriveModelAsync(CancellationToken cancellationToken)
    {
        ActiveModel? boundModel = null;
        ModelParameterBinding? binding = null;
        long boundMappingVersion = -1;
        ParameterPriorityProfile? boundProfile = null;
        ParameterArbitrator? arbitrator = null;
        CubismPhysicsEvaluator? physicsEvaluator = null;
        long boundPhysicsVersion = -1;
        TimeSpan lastPhysicsElapsed = TimeSpan.Zero;
        CubismAnimationEvaluator? animationEvaluator = null;
        long boundAnimationVersion = -1;
        long boundAnimationCommandVersion = -1;
        TimeSpan lastAnimationElapsed = TimeSpan.Zero;
        long lastApplyFailureLogTimestamp = 0;
        try
        {
            await foreach (SessionSnapshot snapshot in snapshots.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                ActiveModel? active = modelSource.Active;
                if (active?.Runtime.Capabilities is not ModelCapabilities capabilities)
                {
                    boundModel = null;
                    binding = null;
                    animationEvaluator = null;
                    boundAnimationVersion = -1;
                    boundAnimationCommandVersion = -1;
                    Volatile.Write(ref automationEnabled, false);
                    continue;
                }

                long mappingVersion = bindingSource?.TryGet(active.Id, out ActiveModelParameterBindingSnapshot bindingSnapshot) == true
                    ? bindingSnapshot.Version
                    : 0;
                ParameterPriorityProfile profile = prioritySource.Current;
                if (!ReferenceEquals(active, boundModel) || mappingVersion != boundMappingVersion)
                {
                    boundModel = active;
                    binding = bindingSource?.TryGet(active.Id, out bindingSnapshot) == true
                        ? ModelParameterBinding.Create(capabilities, snapshot.Parameters,
                            bindingSnapshot.Settings)
                        : ModelParameterBinding.Create(capabilities, snapshot.Parameters);
                    boundMappingVersion = mappingVersion;
                    Volatile.Write(ref automationEnabled, binding.HasAutomaticProviders);
                    ActiveModelDriveLog.MappingCompiled(
                        logger,
                        binding.RouteCount,
                        binding.Issues.Length);
                }

                if (!ReferenceEquals(profile, boundProfile))
                {
                    boundProfile = profile;
                    arbitrator = new ParameterArbitrator(profile);
                }

                TimeSpan elapsed = timeProvider.GetElapsedTime(startTimestamp);
                ImmutableArray<ParameterContribution> animation = [];
                ImmutableArray<ModelPartOpacity> partOpacities = [];
                bool hasActiveAnimation = false;
                ActiveModelAnimationSnapshot selectedAnimationSnapshot = null!;
                bool hasAnimationSnapshot = animationSource is not null
                    && animationSource.TryGet(active.Id, out selectedAnimationSnapshot);
                if (hasAnimationSnapshot)
                {
                    if (selectedAnimationSnapshot.DefinitionVersion != boundAnimationVersion)
                    {
                        animationEvaluator = new CubismAnimationEvaluator(
                            selectedAnimationSnapshot.Definitions,
                            logger);
                        animationEvaluator.ConfigureIdle(
                            selectedAnimationSnapshot.IdleMotion,
                            selectedAnimationSnapshot.LostTrackingIdleMotion);
                        boundAnimationVersion = selectedAnimationSnapshot.DefinitionVersion;
                        boundAnimationCommandVersion = -1;
                        lastAnimationElapsed = elapsed;
                    }

                    if (animationEvaluator is not null
                        && selectedAnimationSnapshot.CommandVersion != boundAnimationCommandVersion)
                    {
                        ApplyAnimationCommand(animationEvaluator, selectedAnimationSnapshot.Command);
                        boundAnimationCommandVersion = selectedAnimationSnapshot.CommandVersion;
                    }

                    if (animationEvaluator is not null)
                    {
                        animationEvaluator.SetTrackingPresence(snapshot.TrackingPresence);
                        CubismAnimationFrame frame = animationEvaluator.Advance(
                            elapsed - lastAnimationElapsed,
                            binding!.GetBaselineValues(snapshot, elapsed, arbitrator!).AsSpan());
                        lastAnimationElapsed = elapsed;
                        animation = frame.Contributions;
                        partOpacities = frame.PartOpacities;
                        hasActiveAnimation = frame.IsActive;
                    }
                }
                else
                {
                    animationEvaluator = null;
                    boundAnimationVersion = -1;
                    boundAnimationCommandVersion = -1;
                }

                ImmutableArray<ParameterContribution> physics = [];
                ActiveModelPhysicsSnapshot? physicsSnapshot = null;
                ActiveModelPhysicsSnapshot selectedPhysicsSnapshot = null!;
                bool hasPhysicsSnapshot = physicsSource is not null
                    && physicsSource.TryGet(
                        active.Id,
                        out selectedPhysicsSnapshot);
                if (hasPhysicsSnapshot)
                {
                    physicsSnapshot = selectedPhysicsSnapshot;
                }
                if (hasPhysicsSnapshot)
                {
                    motionExpansionSource?.Publish(active.Id, physicsSnapshot!.Configuration, snapshot);
                }
                else
                {
                    motionExpansionSource?.Publish(
                        active.Id,
                        ModelPhysicsConfiguration.Disabled,
                        snapshot);
                }

                bool hasActivePhysics = hasPhysicsSnapshot
                    && physicsSnapshot!.Configuration.Enabled
                    && physicsSnapshot.Definition is not null;
                Vector2 dragDisplacement = Vector2.Zero;
                dragPhysicsSource?.TryConsume(active.Id, out dragDisplacement);
                if (hasActivePhysics)
                {
                    ActiveModelPhysicsSnapshot activePhysics = physicsSnapshot!;
                    ModelPhysicsConfiguration physicsConfiguration = activePhysics.Configuration;
                    if (activePhysics.Version != boundPhysicsVersion)
                    {
                        physicsEvaluator = new CubismPhysicsEvaluator(activePhysics.Definition!, capabilities);
                        boundPhysicsVersion = activePhysics.Version;
                        lastPhysicsElapsed = elapsed;
                        ActiveModelDriveLog.PhysicsConfigured(
                            logger,
                            physicsConfiguration.Strength,
                            physicsConfiguration.WindSimulation,
                            physicsConfiguration.DragPhysics,
                            physicsConfiguration.ResolveCalculationFramesPerSecond(
                                applicationFrameRateModeProvider()));
                    }
                    if (physicsEvaluator is not null)
                    {
                        TimeSpan delta = elapsed - lastPhysicsElapsed;
                        lastPhysicsElapsed = elapsed;
                        physics = physicsEvaluator.Evaluate(
                            binding!.GetBaselineValues(snapshot, elapsed, arbitrator!, animation).AsSpan(),
                            delta,
                            physicsConfiguration.ResolveWind(elapsed),
                            physicsConfiguration.ResolveStrength(),
                            physicsConfiguration.ResolveCalculationFramesPerSecond(
                                applicationFrameRateModeProvider()),
                            physicsConfiguration.ResolveDragParameterOffset(dragDisplacement));
                    }
                }
                else
                {
                    physicsEvaluator = null;
                    boundPhysicsVersion = -1;
                }

                Volatile.Write(
                    ref automationEnabled,
                    ShouldAdvanceAutomatically(
                        binding!.HasAutomaticProviders,
                        hasActiveAnimation,
                        hasActivePhysics));

                ModelParameterUpdate update = binding!.Bind(
                    snapshot,
                    elapsed,
                    arbitrator!,
                    animation,
                    physics,
                    partOpacities);
                if (update.Values.Length == 0 && update.PartOpacities.Length == 0)
                {
                    continue;
                }

                bool applied;
                try
                {
                    applied = await active.Runtime.ApplyParametersAsync(update, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    long now = timeProvider.GetTimestamp();
                    if (lastApplyFailureLogTimestamp == 0
                        || timeProvider.GetElapsedTime(lastApplyFailureLogTimestamp, now)
                            >= TimeSpan.FromSeconds(5))
                    {
                        lastApplyFailureLogTimestamp = now;
                        ActiveModelDriveLog.ParameterApplicationFailed(
                            logger,
                            active.Id.Value,
                            exception.GetType().Name);
                    }

                    continue;
                }

                if (applied)
                {
                    observationSource?.Publish(active.Id, binding.Observe(snapshot, update));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task WakeAutomationAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000d / 60d), timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Volatile.Read(ref automationEnabled))
                {
                    QueueLatestSnapshot();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal static bool ShouldAdvanceAutomatically(
        bool hasAutomaticParameters,
        bool hasActiveAnimation,
        bool hasActivePhysics) =>
        hasAutomaticParameters || hasActiveAnimation || hasActivePhysics;

    private async Task DisposeCoreAsync()
    {
        modelSource.ActiveChanged -= OnActiveModelChanged;
        if (bindingSource is not null)
        {
            bindingSource.Changed -= OnBindingChanged;
        }
        prioritySource.Changed -= OnPriorityChanged;
        if (physicsSource is not null) physicsSource.Changed -= OnPhysicsChanged;
        if (dragPhysicsSource is not null) dragPhysicsSource.Changed -= OnDragPhysicsChanged;
        if (animationSource is not null) animationSource.Changed -= OnAnimationChanged;
        Volatile.Write(ref automationEnabled, false);
        cancellation.Cancel();
        snapshots.Writer.TryComplete();
        try
        {
            await Task.WhenAll(watchTask, driveTask, automationTask).ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}

internal static partial class ActiveModelDriveLog
{
    [LoggerMessage(6500, LogLevel.Debug, "Model parameter mapping compiled with {RouteCount} routes and {IssueCount} issues")]
    internal static partial void MappingCompiled(ILogger logger, int routeCount, int issueCount);

    [LoggerMessage(6501, LogLevel.Debug, "Model physics configured with strength {Strength}, wind {WindSimulation}, drag {DragPhysics}, and {CalculationFramesPerSecond} calculation FPS")]
    internal static partial void PhysicsConfigured(
        ILogger logger,
        double strength,
        double windSimulation,
        double dragPhysics,
        int calculationFramesPerSecond);

    [LoggerMessage(6502, LogLevel.Warning, "Model parameter application failed for {ModelId} with {ExceptionType}; the drive loop will continue")]
    internal static partial void ParameterApplicationFailed(
        ILogger logger,
        string modelId,
        string exceptionType);
}
