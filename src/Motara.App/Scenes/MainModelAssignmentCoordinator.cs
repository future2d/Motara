using System.Diagnostics;
using Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Media;
using Motara.ModelLibrary;
using Motara.ModelRuntime.Abstractions;
using Motara.Persistence;
using Motara.Scene;

namespace Motara.App.Scenes;

internal interface IMainModelRuntimeAdapter
{
    Task ClearAsync(CancellationToken cancellationToken);

    Task<MainModelRuntimeLoadResult> LoadAsync(ModelId modelId, CancellationToken cancellationToken);
}

internal sealed record MainModelRuntimeLoadResult(
    bool IsLoaded,
    ModelRenderFrame? Frame,
    PixelSize PixelSize,
    ModelRasterTransform RasterTransform);

internal sealed class MainModelAssignmentStateChangedEventArgs(
    SceneWorkspace workspace,
    SceneId? presentedSceneId,
    ModelId? pendingModelId,
    bool isRuntimeReady) : EventArgs
{
    internal SceneWorkspace Workspace { get; } = workspace;

    internal SceneId? PresentedSceneId { get; } = presentedSceneId;

    internal SceneDocument? PresentedScene => PresentedSceneId is SceneId sceneId
        ? Workspace.Scenes.SingleOrDefault(scene => scene.Id == sceneId)
        : null;

    internal ModelId? PendingModelId { get; } = pendingModelId;

    internal bool IsRuntimeReady { get; } = isRuntimeReady;
}

internal sealed class MainModelAssignmentCoordinator : IDisposable, IAsyncDisposable
{
    private readonly ISceneRepository repository;
    private readonly IMainModelRuntimeAdapter runtime;
    private readonly ILogger<MainModelAssignmentCoordinator> logger;
    private readonly SemaphoreSlim assignmentGate = new(1, 1);
    private readonly object stateGate = new();
    private SceneWorkspace workspace;
    private SceneId? presentedSceneId;
    private ModelId? pendingModelId;
    private bool isRuntimeReady;
    private long requestGeneration;
    private int disposed;

    internal MainModelAssignmentCoordinator(
        ISceneRepository repository,
        IMainModelRuntimeAdapter runtime,
        SceneWorkspace workspace,
        ILogger<MainModelAssignmentCoordinator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(workspace);
        this.repository = repository;
        this.runtime = runtime;
        this.workspace = workspace;
        this.logger = logger ?? NullLogger<MainModelAssignmentCoordinator>.Instance;
    }

    internal event EventHandler<MainModelAssignmentStateChangedEventArgs>? StateChanged;

    internal SceneWorkspace CurrentWorkspace
    {
        get
        {
            lock (stateGate)
            {
                return workspace;
            }
        }
    }

    internal SceneDocument CurrentScene => CurrentWorkspace.ActiveScene;

    internal SceneId? PresentedSceneId
    {
        get
        {
            lock (stateGate)
            {
                return presentedSceneId;
            }
        }
    }

    internal ModelId? PendingModelId
    {
        get
        {
            lock (stateGate)
            {
                return pendingModelId;
            }
        }
    }

    internal bool IsRuntimeReady
    {
        get
        {
            lock (stateGate)
            {
                return isRuntimeReady;
            }
        }
    }

    internal async Task<bool> AssignAsync(
        ModelId modelId,
        CancellationToken cancellationToken) =>
        await AssignAsync(
            modelId,
            new Dictionary<Guid, SceneTransform>(),
            cancellationToken).ConfigureAwait(false);

    internal async Task<bool> AssignAsync(
        ModelId modelId,
        IReadOnlyDictionary<Guid, SceneTransform> attachmentWorldTransforms,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachmentWorldTransforms);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        long generation = Interlocked.Increment(ref requestGeneration);
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (generation != Volatile.Read(ref requestGeneration))
            {
                return false;
            }

            MainModelAssignmentLog.Started(logger);
            SetRuntimeReady(false);
            SceneWorkspace original = CurrentWorkspace;
            MainModelInstance? originalModel = original.ActiveScene.MainModel;
            MainModelAssignmentLog.AttachmentWorldSnapshotCaptured(
                logger,
                attachmentWorldTransforms.Count);

            await runtime.ClearAsync(cancellationToken).ConfigureAwait(false);
            MainModelAssignmentLog.RuntimeReleased(logger);
            if (generation != Volatile.Read(ref requestGeneration))
            {
                return false;
            }

            SetPresentedScene(original.ActiveSceneId);
            SetPending(modelId);
            MainModelRuntimeLoadResult load;
            try
            {
                load = await runtime.LoadAsync(modelId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                MainModelAssignmentLog.Cancelled(logger);
                throw;
            }
            catch (Exception exception)
            {
                await RestoreOriginalRuntimeAsync(originalModel).ConfigureAwait(false);
                MainModelAssignmentLog.Failed(logger, exception.GetType().Name);
                throw;
            }

            if (!load.IsLoaded)
            {
                MainModelAssignmentLog.Failed(logger, "RuntimeRejected");
                await RestoreOriginalRuntimeAsync(originalModel).ConfigureAwait(false);
                return false;
            }

            if (generation != Volatile.Read(ref requestGeneration))
            {
                await runtime.ClearAsync(CancellationToken.None).ConfigureAwait(false);
                MainModelAssignmentLog.Superseded(logger);
                return false;
            }

            long rebuildStarted = Stopwatch.GetTimestamp();
            SceneWorkspace assigned = RebuildAttachmentBindings(
                original.AssignMainModel(modelId.Value),
                original.ActiveScene,
                load,
                attachmentWorldTransforms,
                out int artMeshCount,
                out int planeCount);
            await repository.SaveAsync(assigned, cancellationToken).ConfigureAwait(false);
            if (generation != Volatile.Read(ref requestGeneration))
            {
                MainModelAssignmentLog.Superseded(logger);
                return false;
            }

            SetState(assigned, pending: null);
            SetRuntimeReady(true, clearPending: true);
            MainModelAssignmentLog.AttachmentBindingsRebuilt(
                logger,
                artMeshCount,
                planeCount,
                Stopwatch.GetElapsedTime(rebuildStarted).TotalMilliseconds);
            MainModelAssignmentLog.Committed(logger);
            return true;
        }
        finally
        {
            if (generation == Volatile.Read(ref requestGeneration))
            {
                SetPending(null);
            }

            assignmentGate.Release();
        }
    }

    private static SceneWorkspace RebuildAttachmentBindings(
        SceneWorkspace replacement,
        SceneDocument originalScene,
        MainModelRuntimeLoadResult load,
        IReadOnlyDictionary<Guid, SceneTransform> attachmentWorldTransforms,
        out int artMeshCount,
        out int planeCount)
    {
        artMeshCount = 0;
        planeCount = 0;
        MainModelInstance mainModel = replacement.ActiveScene.MainModel!;
        Size bounds = new(load.PixelSize.Width, load.PixelSize.Height);
        foreach (AttachmentInstance attachment in originalScene.Attachments)
        {
            if (attachment.MountMode != AttachmentMountMode.MainModelAnchor)
            {
                continue;
            }

            SceneTransform world = attachmentWorldTransforms.TryGetValue(
                attachment.SourceId,
                out SceneTransform? captured)
                && captured is not null
                ? captured
                : AttachmentMountTransform.ResolveWorld(attachment, originalScene.MainModel);
            Point surfacePoint = new(
                bounds.Width / 2d + world.X / originalScene.ReferenceHeight * bounds.Height,
                bounds.Height / 2d + world.Y / originalScene.ReferenceHeight * bounds.Height);
            if (!AttachmentModelBindingResolver.TryCreate(
                    load.Frame,
                    surfacePoint,
                    bounds,
                    originalScene.ReferenceHeight,
                    mainModel.Transform,
                    load.RasterTransform,
                    out AttachmentModelBinding binding))
            {
                throw new InvalidOperationException("The replacement model binding geometry is invalid.");
            }

            SceneTransform local = AttachmentMountTransform.RelativeTo(world, binding.AnchorParent);
            replacement = replacement
                .SetActiveAttachmentTransform(attachment.SourceId, local)
                .SetActiveAttachmentMountMode(
                    attachment.SourceId,
                    AttachmentMountMode.MainModelAnchor,
                    mainModel.SourceId.ToString("N"))
                .SetActiveAttachmentModelAnchor(attachment.SourceId, binding.Anchor);
            if (binding.Anchor.Kind == AttachmentModelAnchorKind.ArtMesh)
            {
                artMeshCount++;
            }
            else
            {
                planeCount++;
            }
        }

        return replacement;
    }

    private async Task RestoreOriginalRuntimeAsync(MainModelInstance? originalModel)
    {
        try
        {
            await runtime.ClearAsync(CancellationToken.None).ConfigureAwait(false);
            if (originalModel is null)
            {
                SetRuntimeReady(true, clearPending: true);
                MainModelAssignmentLog.OriginalRuntimeRestored(logger, true, "NoOriginalModel");
                return;
            }

            MainModelRuntimeLoadResult restored = await runtime.LoadAsync(
                ModelId.Create(originalModel.ModelAssetId),
                CancellationToken.None).ConfigureAwait(false);
            SetRuntimeReady(restored.IsLoaded, clearPending: true);
            MainModelAssignmentLog.OriginalRuntimeRestored(
                logger,
                restored.IsLoaded,
                restored.IsLoaded ? "Restored" : "RuntimeRejected");
        }
        catch (Exception exception)
        {
            SetRuntimeReady(false, clearPending: true);
            MainModelAssignmentLog.OriginalRuntimeRestored(
                logger,
                false,
                exception.GetType().Name);
        }
    }

    internal async Task<SceneId> CreateSceneAsync(
        string displayName,
        CancellationToken cancellationToken)
    {
        long generation = BeginRequest();
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrent(generation);
            SceneWorkspace created = CurrentWorkspace.CreateScene(displayName);
            await PersistStateAsync(created, cancellationToken).ConfigureAwait(false);
            _ = await ReplaceRuntimeForActiveSceneAsync(
                created,
                generation,
                cancellationToken).ConfigureAwait(false);
            return created.ActiveSceneId;
        }
        finally
        {
            ClearPendingForCurrent(generation);
            assignmentGate.Release();
        }
    }

    internal async Task<bool> RestoreActiveSceneAsync(CancellationToken cancellationToken)
    {
        long generation = BeginRequest();
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrent(generation);
            return await ReplaceRuntimeForActiveSceneAsync(
                CurrentWorkspace,
                generation,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ClearPendingForCurrent(generation);
            assignmentGate.Release();
        }
    }

    internal async Task<bool> ActivateSceneAsync(
        SceneId sceneId,
        CancellationToken cancellationToken)
    {
        long generation = BeginRequest();
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrent(generation);
            SceneWorkspace current = CurrentWorkspace;
            if (current.ActiveSceneId == sceneId
                && PresentedSceneId == sceneId)
            {
                return true;
            }

            SceneWorkspace activated = current.ActiveSceneId == sceneId
                ? current
                : current.Activate(sceneId);
            if (!ReferenceEquals(activated, current))
            {
                await PersistStateAsync(activated, cancellationToken).ConfigureAwait(false);
            }
            return await ReplaceRuntimeForActiveSceneAsync(
                activated,
                generation,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ClearPendingForCurrent(generation);
            assignmentGate.Release();
        }
    }

    internal async Task DeactivateSceneAsync(CancellationToken cancellationToken)
    {
        long generation = BeginRequest();
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrent(generation);
            MainModelAssignmentLog.SceneDeactivationStarted(logger);
            SetRuntimeReady(false);
            await runtime.ClearAsync(cancellationToken).ConfigureAwait(false);
            MainModelAssignmentLog.RuntimeReleased(logger);
            EnsureCurrent(generation);
            SetPresentedScene(null);
            MainModelAssignmentLog.SceneDeactivationCommitted(logger);
        }
        finally
        {
            ClearPendingForCurrent(generation);
            assignmentGate.Release();
        }
    }

    internal async Task RenameSceneAsync(
        SceneId sceneId,
        string displayName,
        CancellationToken cancellationToken)
    {
        long generation = BeginRequest();
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrent(generation);
            await PersistStateAsync(
                CurrentWorkspace.Rename(sceneId, displayName),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ClearPendingForCurrent(generation);
            assignmentGate.Release();
        }
    }

    internal Task SetMainModelVisibilityAsync(
        bool isVisible,
        CancellationToken cancellationToken) =>
        PersistMainModelSourceStateAsync(
            workspace => workspace.SetActiveMainModelVisibility(isVisible),
            isVisible,
            isLocked: null,
            cancellationToken: cancellationToken);

    internal Task SetMainModelLockAsync(
        bool isLocked,
        CancellationToken cancellationToken) =>
        PersistMainModelSourceStateAsync(
            workspace => workspace.SetActiveMainModelLock(isLocked),
            isVisible: null,
            isLocked: isLocked,
            cancellationToken: cancellationToken);

    internal async Task<bool> SetMainModelTransformAsync(
        Guid sourceId,
        SceneTransform transform,
        CancellationToken cancellationToken)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("Source ID cannot be empty.", nameof(sourceId));
        }

        ArgumentNullException.ThrowIfNull(transform);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MainModelInstance? current = CurrentWorkspace.ActiveScene.MainModel;
            if (current is null || current.SourceId != sourceId)
            {
                MainModelAssignmentLog.TransformIgnored(logger);
                return false;
            }

            SceneWorkspace currentWorkspace = CurrentWorkspace;
            SceneWorkspace next = currentWorkspace.SetActiveMainModelTransform(transform);
            if (ReferenceEquals(next, currentWorkspace))
            {
                return true;
            }

            await PersistStateAsync(next, cancellationToken).ConfigureAwait(false);
            MainModelAssignmentLog.TransformChanged(
                logger,
                transform.X,
                transform.Y,
                transform.Scale,
                transform.RotationDegrees);
            return true;
        }
        finally
        {
            assignmentGate.Release();
        }
    }

    internal async Task SetMainModelTrackingAsync(
        MainModelTrackingMode trackingMode,
        TrackingChannelBindings trackingChannels,
        string? idleAnimationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trackingChannels);
        await PersistMainModelSourceStateAsync(
            workspace => workspace.SetActiveMainModelTracking(
                trackingMode,
                trackingChannels,
                idleAnimationId),
            isVisible: null,
            isLocked: null,
            cancellationToken).ConfigureAwait(false);
        MainModelAssignmentLog.TrackingChanged(logger, trackingMode, trackingChannels.HasAny);
    }

    internal async Task SetActiveSceneBlurEffectAsync(
        SceneEffectInstance? effect,
        CancellationToken cancellationToken)
    {
        long generation = BeginRequest();
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrent(generation);
            SceneDocument scene = CurrentWorkspace.ActiveScene;
            SceneEffectInstance? existing = scene.Effects.FirstOrDefault(
                candidate => candidate.EffectId == "builtin.blur");
            SceneDocument next = effect is null
                ? existing is null ? scene : scene.RemoveEffect(existing.SourceId)
                : existing is null ? scene.AddEffect(effect) : scene.UpdateEffect(
                    new SceneEffectInstance(
                        existing.SourceId,
                        effect.EffectId,
                        effect.IsEnabled,
                        effect.Blur));
            if (!ReferenceEquals(scene, next))
            {
                await PersistStateAsync(CurrentWorkspace.ReplaceActive(next), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ClearPendingForCurrent(generation);
            assignmentGate.Release();
        }
    }

    internal async Task SetActiveBackgroundOverrideAsync(
        BackgroundDefinition? backgroundOverride,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SceneWorkspace current = CurrentWorkspace;
            SceneWorkspace next = current.SetActiveBackgroundOverride(backgroundOverride);
            if (ReferenceEquals(current, next))
            {
                return;
            }

            await PersistStateAsync(next, cancellationToken).ConfigureAwait(false);
            MainModelAssignmentLog.BackgroundChanged(logger, backgroundOverride is not null);
        }
        finally
        {
            assignmentGate.Release();
        }
    }

    internal async Task AddSignalAttachmentAsync(
        VideoSignalProtocol protocol,
        string sourceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        string sourceTypeId = protocol switch
        {
            VideoSignalProtocol.Spout2 => "attachment.spout2",
            VideoSignalProtocol.Ndi => "attachment.ndi",
            _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
        };

        await AddAttachmentAsync(
            sourceTypeId,
            sourceId,
            sourceId,
            BackgroundVideoOptions.Default,
            AttachmentPlacement.AfterMainModel,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task AddAttachmentAsync(
        string sourceTypeId,
        string resourceReference,
        string displayName,
        BackgroundVideoOptions videoOptions,
        AttachmentPlacement placement,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTypeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(videoOptions);
        if (!Enum.IsDefined(placement))
        {
            throw new ArgumentOutOfRangeException(nameof(placement));
        }

        long generation = BeginRequest();
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrent(generation);
            SceneWorkspace next = CurrentWorkspace.AddAttachment(
                AttachmentInstance.Create(sourceTypeId, resourceReference, placement, videoOptions, displayName));
            await PersistStateAsync(next, cancellationToken).ConfigureAwait(false);
            MainModelAssignmentLog.AttachmentAdded(logger, sourceTypeId, resourceReference);
        }
        finally
        {
            ClearPendingForCurrent(generation);
            assignmentGate.Release();
        }
    }

    internal async Task<bool> SetAttachmentVisibilityAsync(
        Guid sourceId,
        bool isVisible,
        CancellationToken cancellationToken)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("Attachment source ID cannot be empty.", nameof(sourceId));
        }
        long generation = BeginRequest();
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrent(generation);
            SceneWorkspace current = CurrentWorkspace;
            SceneWorkspace next = current.SetActiveAttachmentVisibility(sourceId, isVisible);
            if (ReferenceEquals(current, next))
            {
                return true;
            }

            await PersistStateAsync(next, cancellationToken).ConfigureAwait(false);
            MainModelAssignmentLog.AttachmentVisibilityChanged(logger, sourceId, isVisible);
            return true;
        }
        finally
        {
            ClearPendingForCurrent(generation);
            assignmentGate.Release();
        }
    }

    internal async Task<bool> SetAttachmentLockAsync(
        Guid sourceId,
        bool isLocked,
        CancellationToken cancellationToken)
    {
        long generation = BeginRequest();
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrent(generation);
            SceneWorkspace current = CurrentWorkspace;
            SceneWorkspace next = current.SetActiveAttachmentLock(sourceId, isLocked);
            if (ReferenceEquals(current, next))
            {
                return true;
            }

            await PersistStateAsync(next, cancellationToken).ConfigureAwait(false);
            MainModelAssignmentLog.AttachmentLockChanged(logger, sourceId, isLocked);
            return true;
        }
        finally
        {
            ClearPendingForCurrent(generation);
            assignmentGate.Release();
        }
    }

    internal async Task<bool> SetAttachmentTransformAsync(
        Guid sourceId,
        SceneTransform transform,
        CancellationToken cancellationToken)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("Attachment source ID cannot be empty.", nameof(sourceId));
        }

        ArgumentNullException.ThrowIfNull(transform);
        long generation = BeginRequest();
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrent(generation);
            SceneWorkspace current = CurrentWorkspace;
            SceneWorkspace next = current.SetActiveAttachmentTransform(sourceId, transform);
            if (ReferenceEquals(current, next))
            {
                return true;
            }

            await PersistStateAsync(next, cancellationToken).ConfigureAwait(false);
            MainModelAssignmentLog.AttachmentTransformChanged(
                logger,
                sourceId,
                transform.X,
                transform.Y,
                transform.Scale,
                transform.RotationDegrees);
            return true;
        }
        finally
        {
            ClearPendingForCurrent(generation);
            assignmentGate.Release();
        }
    }

    internal async Task<bool> SetAttachmentMountModeAsync(
        Guid sourceId,
        AttachmentMountMode mountMode,
        CancellationToken cancellationToken) =>
        await SetAttachmentMountModeAsync(
            sourceId,
            mountMode,
            presentedWorldTransform: null,
            cancellationToken).ConfigureAwait(false);

    internal async Task<bool> SetAttachmentMountModeAsync(
        Guid sourceId,
        AttachmentMountMode mountMode,
        SceneTransform? presentedWorldTransform,
        CancellationToken cancellationToken) =>
        await SetAttachmentMountModeAsync(
            sourceId,
            mountMode,
            presentedWorldTransform,
            initialAnchor: null,
            initialLocalTransform: null,
            cancellationToken).ConfigureAwait(false);

    internal async Task<bool> SetAttachmentMountModeAsync(
        Guid sourceId,
        AttachmentMountMode mountMode,
        SceneTransform? presentedWorldTransform,
        AttachmentModelAnchor? initialAnchor,
        SceneTransform? initialLocalTransform,
        CancellationToken cancellationToken)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("Attachment source ID cannot be empty.", nameof(sourceId));
        }

        if (!Enum.IsDefined(mountMode))
        {
            throw new ArgumentOutOfRangeException(nameof(mountMode));
        }

        long generation = BeginRequest();
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrent(generation);
            SceneDocument scene = CurrentWorkspace.ActiveScene;
            AttachmentInstance currentAttachment = scene.Attachments
                .Single(attachment => attachment.SourceId == sourceId);
            if (currentAttachment.MountMode == mountMode)
            {
                return true;
            }

            MainModelInstance? mainModel = scene.MainModel;
            if (mountMode == AttachmentMountMode.MainModelAnchor && mainModel is null)
            {
                MainModelAssignmentLog.AttachmentMountModeRejected(logger, sourceId, "NoMainModel");
                return false;
            }

            if (mountMode == AttachmentMountMode.MainModelAnchor
                && (initialAnchor is null || initialLocalTransform is null))
            {
                MainModelAssignmentLog.AttachmentMountModeRejected(
                    logger,
                    sourceId,
                    "InitialBindingRequired");
                return false;
            }

            bool usedPresentedWorldTransform = presentedWorldTransform is not null;
            SceneTransform world = presentedWorldTransform
                ?? AttachmentMountTransform.ResolveWorld(currentAttachment, mainModel);
            string? anchorId = mountMode == AttachmentMountMode.MainModelAnchor
                ? mainModel!.SourceId.ToString("N")
                : null;
            SceneTransform stored = mountMode == AttachmentMountMode.MainModelAnchor
                ? initialLocalTransform!
                : world;
            SceneWorkspace next = CurrentWorkspace
                .SetActiveAttachmentTransform(sourceId, stored)
                .SetActiveAttachmentMountMode(sourceId, mountMode, anchorId);
            if (mountMode == AttachmentMountMode.MainModelAnchor)
            {
                next = next.SetActiveAttachmentModelAnchor(sourceId, initialAnchor);
            }
            if (ReferenceEquals(next, CurrentWorkspace))
            {
                return true;
            }

            await PersistStateAsync(next, cancellationToken).ConfigureAwait(false);
            MainModelAssignmentLog.AttachmentMountModeChanged(logger, sourceId, mountMode);
            MainModelAssignmentLog.AttachmentMountModeTransformPreserved(
                logger,
                sourceId,
                usedPresentedWorldTransform ? "PresentedWorld" : "StoredTransform",
                world.X,
                world.Y);
            if (initialAnchor is not null)
            {
                MainModelAssignmentLog.AttachmentModelBindingChanged(
                    logger,
                    sourceId,
                    initialAnchor.Kind,
                    initialAnchor.ArtMeshId ?? string.Empty,
                    initialAnchor.TriangleIndex,
                    initialAnchor.PlaneX,
                    initialAnchor.PlaneY);
            }
            return true;
        }
        finally
        {
            ClearPendingForCurrent(generation);
            assignmentGate.Release();
        }
    }

    internal async Task<bool> SetAttachmentModelBindingAsync(
        Guid sourceId,
        AttachmentModelAnchor anchor,
        SceneTransform localTransform,
        CancellationToken cancellationToken)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("Attachment source ID cannot be empty.", nameof(sourceId));
        }

        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(localTransform);
        long generation = BeginRequest();
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrent(generation);
            SceneDocument scene = CurrentWorkspace.ActiveScene;
            AttachmentInstance current = scene.Attachments
                .Single(attachment => attachment.SourceId == sourceId);
            if (current.MountMode != AttachmentMountMode.MainModelAnchor
                || scene.MainModel is null)
            {
                MainModelAssignmentLog.AttachmentModelAnchorRejected(
                    logger,
                    sourceId,
                    "MainModelAnchorRequired");
                return false;
            }

            SceneWorkspace next = CurrentWorkspace
                .SetActiveAttachmentTransform(sourceId, localTransform)
                .SetActiveAttachmentModelAnchor(sourceId, anchor);
            if (ReferenceEquals(next, CurrentWorkspace))
            {
                return true;
            }

            await PersistStateAsync(next, cancellationToken).ConfigureAwait(false);
            MainModelAssignmentLog.AttachmentModelBindingChanged(
                logger,
                sourceId,
                anchor.Kind,
                anchor.ArtMeshId ?? string.Empty,
                anchor.TriangleIndex,
                anchor.PlaneX,
                anchor.PlaneY);
            return true;
        }
        finally
        {
            ClearPendingForCurrent(generation);
            assignmentGate.Release();
        }
    }

    internal async Task MoveAttachmentToAsync(
        Guid sourceId,
        AttachmentPlacement placement,
        int destinationIndex,
        CancellationToken cancellationToken)
    {
        long generation = BeginRequest();
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrent(generation);
            SceneWorkspace current = CurrentWorkspace;
            SceneWorkspace next = current.MoveActiveAttachmentTo(sourceId, placement, destinationIndex);
            if (ReferenceEquals(current, next))
            {
                return;
            }

            await PersistStateAsync(next, cancellationToken).ConfigureAwait(false);
            MainModelAssignmentLog.AttachmentMoved(logger, sourceId, placement, destinationIndex);
        }
        finally
        {
            ClearPendingForCurrent(generation);
            assignmentGate.Release();
        }
    }

    internal async Task MoveMainModelToAsync(
        int frontAttachmentCount,
        CancellationToken cancellationToken)
    {
        long generation = BeginRequest();
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrent(generation);
            SceneWorkspace next = CurrentWorkspace.MoveActiveMainModelTo(frontAttachmentCount);
            await PersistStateAsync(next, cancellationToken).ConfigureAwait(false);
            MainModelAssignmentLog.MainModelMoved(logger, frontAttachmentCount);
        }
        finally
        {
            ClearPendingForCurrent(generation);
            assignmentGate.Release();
        }
    }

    internal async Task SetAttachmentDisplayNameAsync(
        Guid sourceId,
        string displayName,
        CancellationToken cancellationToken)
    {
        long generation = BeginRequest();
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrent(generation);
            SceneWorkspace next = CurrentWorkspace.SetActiveAttachmentDisplayName(sourceId, displayName);
            await PersistStateAsync(next, cancellationToken).ConfigureAwait(false);
            MainModelAssignmentLog.AttachmentRenamed(logger, sourceId, displayName);
        }
        finally
        {
            ClearPendingForCurrent(generation);
            assignmentGate.Release();
        }
    }

    internal async Task RemoveAttachmentAsync(
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        long generation = BeginRequest();
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrent(generation);
            SceneWorkspace current = CurrentWorkspace;
            SceneWorkspace next = current.RemoveAttachment(sourceId);
            if (ReferenceEquals(current, next))
            {
                return;
            }

            await PersistStateAsync(next, cancellationToken).ConfigureAwait(false);
            MainModelAssignmentLog.AttachmentRemoved(logger, sourceId);
        }
        finally
        {
            ClearPendingForCurrent(generation);
            assignmentGate.Release();
        }
    }

    internal async Task DeleteSceneAsync(
        SceneId sceneId,
        CancellationToken cancellationToken)
    {
        long generation = BeginRequest();
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrent(generation);
            SceneWorkspace current = CurrentWorkspace;
            SceneWorkspace remaining = current.Delete(sceneId);
            await PersistStateAsync(remaining, cancellationToken).ConfigureAwait(false);
            if (current.ActiveSceneId == sceneId && PresentedSceneId == sceneId)
            {
                _ = await ReplaceRuntimeForActiveSceneAsync(
                    remaining,
                    generation,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ClearPendingForCurrent(generation);
            assignmentGate.Release();
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await assignmentGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        assignmentGate.Release();
        assignmentGate.Dispose();
    }

    private long BeginRequest()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return Interlocked.Increment(ref requestGeneration);
    }

    private void EnsureCurrent(long generation)
    {
        if (generation != Volatile.Read(ref requestGeneration))
        {
            throw new OperationCanceledException("The scene request was superseded.");
        }
    }

    private async Task PersistStateAsync(
        SceneWorkspace nextWorkspace,
        CancellationToken cancellationToken)
    {
        await repository.SaveAsync(nextWorkspace, cancellationToken).ConfigureAwait(false);
        SetState(nextWorkspace, pending: null);
        MainModelAssignmentLog.SceneStateSaved(logger, nextWorkspace.Scenes.Length);
    }

    private async Task PersistMainModelSourceStateAsync(
        Func<SceneWorkspace, SceneWorkspace> mutation,
        bool? isVisible,
        bool? isLocked,
        CancellationToken cancellationToken)
    {
        long generation = BeginRequest();
        await assignmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrent(generation);
            SceneWorkspace current = CurrentWorkspace;
            SceneWorkspace next = mutation(current);
            if (ReferenceEquals(current, next))
            {
                return;
            }

            await PersistStateAsync(next, cancellationToken).ConfigureAwait(false);
            if (isVisible is bool visible)
            {
                MainModelAssignmentLog.VisibilityChanged(logger, visible);
            }

            if (isLocked is bool locked)
            {
                MainModelAssignmentLog.LockChanged(logger, locked);
            }
        }
        finally
        {
            ClearPendingForCurrent(generation);
            assignmentGate.Release();
        }
    }

    private async Task<bool> ReplaceRuntimeForActiveSceneAsync(
        SceneWorkspace nextWorkspace,
        long generation,
        CancellationToken cancellationToken)
    {
        SetPresentedScene(nextWorkspace.ActiveSceneId);
        SetRuntimeReady(false);
        MainModelAssignmentLog.SceneActivationStarted(logger);
        await runtime.ClearAsync(cancellationToken).ConfigureAwait(false);
        MainModelAssignmentLog.RuntimeReleased(logger);
        if (generation != Volatile.Read(ref requestGeneration))
        {
            MainModelAssignmentLog.Superseded(logger);
            return false;
        }

        if (nextWorkspace.ActiveScene.MainModel is not { ModelAssetId: string assetId })
        {
            SetRuntimeReady(true);
            MainModelAssignmentLog.SceneActivationCommitted(logger, hasMainModel: false);
            return true;
        }

        ModelId modelId = ModelId.Create(assetId);
        SetPending(modelId);
        MainModelRuntimeLoadResult load;
        try
        {
            load = await runtime.LoadAsync(modelId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            MainModelAssignmentLog.Cancelled(logger);
            throw;
        }
        catch (Exception exception)
        {
            await runtime.ClearAsync(CancellationToken.None).ConfigureAwait(false);
            MainModelAssignmentLog.Failed(logger, exception.GetType().Name);
            throw;
        }

        if (!load.IsLoaded)
        {
            MainModelAssignmentLog.Failed(logger, "RuntimeRejected");
            return false;
        }

        if (generation != Volatile.Read(ref requestGeneration))
        {
            await runtime.ClearAsync(CancellationToken.None).ConfigureAwait(false);
            MainModelAssignmentLog.Superseded(logger);
            return false;
        }

        SetRuntimeReady(true, clearPending: true);
        MainModelAssignmentLog.SceneActivationCommitted(logger, hasMainModel: true);
        return true;
    }

    private void ClearPendingForCurrent(long generation)
    {
        if (generation == Volatile.Read(ref requestGeneration))
        {
            SetPending(null);
        }
    }

    private void SetPending(ModelId? pending)
    {
        bool changed;
        lock (stateGate)
        {
            changed = pendingModelId != pending;
            pendingModelId = pending;
        }

        if (changed)
        {
            PublishStateChanged();
        }
    }

    private void SetRuntimeReady(bool ready, bool clearPending = false)
    {
        bool changed;
        lock (stateGate)
        {
            changed = isRuntimeReady != ready || (clearPending && pendingModelId is not null);
            isRuntimeReady = ready;
            if (clearPending)
            {
                pendingModelId = null;
            }
        }

        if (changed)
        {
            PublishStateChanged();
        }
    }

    private void SetState(SceneWorkspace nextWorkspace, ModelId? pending)
    {
        lock (stateGate)
        {
            workspace = nextWorkspace;
            pendingModelId = pending;
        }

        PublishStateChanged();
    }

    private void SetPresentedScene(SceneId? sceneId)
    {
        bool changed;
        lock (stateGate)
        {
            changed = presentedSceneId != sceneId;
            presentedSceneId = sceneId;
        }

        if (changed)
        {
            PublishStateChanged();
        }
    }

    private void PublishStateChanged()
    {
        MainModelAssignmentStateChangedEventArgs args;
        lock (stateGate)
        {
            args = new MainModelAssignmentStateChangedEventArgs(
                workspace,
                presentedSceneId,
                pendingModelId,
                isRuntimeReady);
        }

        StateChanged?.Invoke(this, args);
    }
}

internal static partial class MainModelAssignmentLog
{
    [LoggerMessage(6260, LogLevel.Information, "Main model assignment started")]
    internal static partial void Started(ILogger logger);

    [LoggerMessage(6261, LogLevel.Debug, "Previous main model runtime released")]
    internal static partial void RuntimeReleased(ILogger logger);

    [LoggerMessage(6262, LogLevel.Information, "Main model assignment committed")]
    internal static partial void Committed(ILogger logger);

    [LoggerMessage(6263, LogLevel.Information, "Main model assignment cancelled")]
    internal static partial void Cancelled(ILogger logger);

    [LoggerMessage(6264, LogLevel.Warning, "Main model assignment failed with {ErrorCode}")]
    internal static partial void Failed(ILogger logger, string errorCode);

    [LoggerMessage(6265, LogLevel.Debug, "Main model assignment superseded by a newer request")]
    internal static partial void Superseded(ILogger logger);

    [LoggerMessage(6266, LogLevel.Information, "Captured {AttachmentCount} followed attachment world transforms before main model replacement")]
    internal static partial void AttachmentWorldSnapshotCaptured(
        ILogger logger,
        int attachmentCount);

    [LoggerMessage(6267, LogLevel.Information, "Rebuilt attachment bindings after main model replacement: ArtMesh={ArtMeshCount}, plane={PlaneCount}, duration={DurationMs} ms")]
    internal static partial void AttachmentBindingsRebuilt(
        ILogger logger,
        int artMeshCount,
        int planeCount,
        double durationMs);

    [LoggerMessage(6268, LogLevel.Information, "Original main model runtime restoration completed: success={Succeeded}, result={Result}")]
    internal static partial void OriginalRuntimeRestored(
        ILogger logger,
        bool succeeded,
        string result);

    [LoggerMessage(6270, LogLevel.Debug, "Scene workspace saved with {SceneCount} scenes")]
    internal static partial void SceneStateSaved(ILogger logger, int sceneCount);

    [LoggerMessage(6271, LogLevel.Information, "Scene activation started")]
    internal static partial void SceneActivationStarted(ILogger logger);

    [LoggerMessage(6285, LogLevel.Information, "Active scene background changed; custom={IsCustom}")]
    internal static partial void BackgroundChanged(ILogger logger, bool isCustom);

    [LoggerMessage(6286, LogLevel.Information, "Scene attachment added for {SourceTypeId}:{ResourceReference}")]
    internal static partial void AttachmentAdded(
        ILogger logger,
        string sourceTypeId,
        string resourceReference);

    [LoggerMessage(6287, LogLevel.Information, "Scene attachment visibility changed for {SourceId}: {IsVisible}")]
    internal static partial void AttachmentVisibilityChanged(ILogger logger, Guid sourceId, bool isVisible);

    [LoggerMessage(6288, LogLevel.Information, "Scene attachment lock changed for {SourceId}: {IsLocked}")]
    internal static partial void AttachmentLockChanged(ILogger logger, Guid sourceId, bool isLocked);

    [LoggerMessage(6289, LogLevel.Information, "Scene attachment moved for {SourceId} to {Placement} index {DestinationIndex}")]
    internal static partial void AttachmentMoved(
        ILogger logger,
        Guid sourceId,
        AttachmentPlacement placement,
        int destinationIndex);

    [LoggerMessage(6290, LogLevel.Information, "Scene attachment removed: {SourceId}")]
    internal static partial void AttachmentRemoved(ILogger logger, Guid sourceId);

    [LoggerMessage(6297, LogLevel.Information, "Main model moved across {FrontAttachmentCount} front attachments")]
    internal static partial void MainModelMoved(ILogger logger, int frontAttachmentCount);

    [LoggerMessage(6298, LogLevel.Information, "Scene attachment renamed: {SourceId} to {DisplayName}")]
    internal static partial void AttachmentRenamed(ILogger logger, Guid sourceId, string displayName);

    [LoggerMessage(6299, LogLevel.Information, "Scene attachment transform changed for {SourceId}: x={X}, y={Y}, scale={Scale}, rotation={RotationDegrees}")]
    internal static partial void AttachmentTransformChanged(
        ILogger logger,
        Guid sourceId,
        double x,
        double y,
        double scale,
        double rotationDegrees);

    [LoggerMessage(6300, LogLevel.Information, "Scene attachment mount mode changed for {SourceId}: {MountMode}")]
    internal static partial void AttachmentMountModeChanged(
        ILogger logger,
        Guid sourceId,
        AttachmentMountMode mountMode);

    [LoggerMessage(6301, LogLevel.Warning, "Scene attachment mount mode rejected for {SourceId}: {Reason}")]
    internal static partial void AttachmentMountModeRejected(
        ILogger logger,
        Guid sourceId,
        string reason);

    [LoggerMessage(6305, LogLevel.Debug, "Scene attachment mount mode preserved {SourceId} from {TransformSource}: x={X}, y={Y}")]
    internal static partial void AttachmentMountModeTransformPreserved(
        ILogger logger,
        Guid sourceId,
        string transformSource,
        double x,
        double y);

    [LoggerMessage(6304, LogLevel.Information, "Scene attachment model binding changed for {SourceId}: kind={Kind}, ArtMesh={ArtMeshId}, triangle={TriangleIndex}, plane=({PlaneX},{PlaneY})")]
    internal static partial void AttachmentModelBindingChanged(
        ILogger logger,
        Guid sourceId,
        AttachmentModelAnchorKind kind,
        string artMeshId,
        int triangleIndex,
        double planeX,
        double planeY);

    [LoggerMessage(6303, LogLevel.Warning, "Scene attachment model anchor rejected for {SourceId}: {Reason}")]
    internal static partial void AttachmentModelAnchorRejected(
        ILogger logger,
        Guid sourceId,
        string reason);

    [LoggerMessage(6272, LogLevel.Information, "Scene activation committed; main model present: {HasMainModel}")]
    internal static partial void SceneActivationCommitted(ILogger logger, bool hasMainModel);

    [LoggerMessage(6273, LogLevel.Information, "Main model visibility changed to {IsVisible}")]
    internal static partial void VisibilityChanged(ILogger logger, bool isVisible);

    [LoggerMessage(6274, LogLevel.Information, "Main model lock changed to {IsLocked}")]
    internal static partial void LockChanged(ILogger logger, bool isLocked);

    [LoggerMessage(6275, LogLevel.Information, "Main model tracking changed to {TrackingMode}; channels active: {HasChannels}")]
    internal static partial void TrackingChanged(
        ILogger logger,
        MainModelTrackingMode trackingMode,
        bool hasChannels);

    [LoggerMessage(6278, LogLevel.Information, "Main model transform changed to x={X}, y={Y}, scale={Scale}, rotation={RotationDegrees}")]
    internal static partial void TransformChanged(
        ILogger logger,
        double x,
        double y,
        double scale,
        double rotationDegrees);

    [LoggerMessage(6279, LogLevel.Debug, "Main model transform commit ignored because the source is no longer active")]
    internal static partial void TransformIgnored(ILogger logger);

    [LoggerMessage(6276, LogLevel.Information, "Scene deactivation started")]
    internal static partial void SceneDeactivationStarted(ILogger logger);

    [LoggerMessage(6277, LogLevel.Information, "Scene deactivation committed")]
    internal static partial void SceneDeactivationCommitted(ILogger logger);
}
