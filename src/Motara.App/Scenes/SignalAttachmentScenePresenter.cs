using Avalonia;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Backgrounds;
using Motara.App.Controls;
using Motara.Media;
using Motara.Persistence;
using Motara.ModelRuntime.Abstractions;
using Motara.Scene;

namespace Motara.App.Scenes;

internal readonly record struct AttachmentAnchorSelector(
    Guid SourceId,
    Point Point,
    string Label,
    AttachmentModelAnchorKind Kind);

internal sealed class SignalAttachmentScenePresenter : IAsyncDisposable
{
    private readonly BackgroundSignalPlaybackFactory playbackFactory;
    private readonly IBackgroundImageDecoder? imageDecoder;
    private readonly IBackgroundVideoPlaybackFactory? videoFactory;
    private readonly ILogger logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private List<SignalAttachmentVisual> visuals = [];
    private SceneDocument? presentedScene;
    private ModelRenderFrame? mainModelFrame;
    private SceneTransform? mainModelTransform;
    private ModelRasterTransform mainModelRasterTransform = ModelRasterTransform.Identity;
    private PixelSize mainModelPixelSize;
    private double mainModelReferenceHeight = 1080;
    private int disposed;

    internal SignalAttachmentScenePresenter(
        BackgroundSignalPlaybackFactory playbackFactory,
        ILogger<SignalAttachmentScenePresenter>? logger = null)
        : this(playbackFactory, null, null, logger)
    {
    }

    internal SignalAttachmentScenePresenter(
        BackgroundSignalPlaybackFactory playbackFactory,
        IBackgroundImageDecoder? imageDecoder,
        IBackgroundVideoPlaybackFactory? videoFactory,
        ILogger<SignalAttachmentScenePresenter>? logger = null)
    {
        this.playbackFactory = playbackFactory ?? throw new ArgumentNullException(nameof(playbackFactory));
        this.imageDecoder = imageDecoder;
        this.videoFactory = videoFactory;
        this.logger = logger ?? NullLogger<SignalAttachmentScenePresenter>.Instance;
    }

    internal event EventHandler? Changed;

    internal IReadOnlyList<SignalAttachmentVisual> BeforeModel =>
        visuals.Where(static visual => visual.Placement == AttachmentPlacement.BeforeMainModel).ToArray();

    internal IReadOnlyList<SignalAttachmentVisual> AfterModel =>
        visuals.Where(static visual => visual.Placement == AttachmentPlacement.AfterMainModel).ToArray();

    internal IReadOnlyDictionary<Guid, SceneTransform> CaptureFollowingWorldTransforms() =>
        visuals
            .Where(static visual => visual.MountMode == AttachmentMountMode.MainModelAnchor)
            .ToDictionary(static visual => visual.SourceId, static visual => visual.Transform);

    internal bool TryGetTopmostVisual(
        Point point,
        Size bounds,
        out SignalAttachmentVisual? visual,
        bool includeBehindMainModel = true)
    {
        IEnumerable<SignalAttachmentVisual> candidates = visuals
            .Where(static candidate => candidate.Placement == AttachmentPlacement.AfterMainModel)
            .Reverse();
        if (includeBehindMainModel)
        {
            candidates = candidates.Concat(
                visuals.Where(static candidate => candidate.Placement == AttachmentPlacement.BeforeMainModel)
                    .Reverse());
        }

        foreach (SignalAttachmentVisual candidate in candidates)
        {
            if (SignalAttachmentVisualControl.ContainsVisual(candidate, point, bounds))
            {
                visual = candidate;
                return true;
            }
        }

        visual = null;
        return false;
    }

    internal bool TryGetVisual(Guid sourceId, out SignalAttachmentVisual? visual)
    {
        visual = visuals.FirstOrDefault(candidate => candidate.SourceId == sourceId);
        return visual is not null;
    }

    internal bool TryGetAttachmentAnchorPoint(
        Guid sourceId,
        Size bounds,
        out Point point)
    {
        point = default;
        if (bounds.Width <= 0
            || bounds.Height <= 0
            || !TryGetVisual(sourceId, out SignalAttachmentVisual? visual)
            || visual is null)
        {
            return false;
        }

        double referenceHeight = visual.ReferenceHeight > 0 ? visual.ReferenceHeight : 1080;
        double centerX = bounds.Width / 2 + visual.Transform.X / referenceHeight * bounds.Height;
        double centerY = bounds.Height / 2 + visual.Transform.Y / referenceHeight * bounds.Height;
        if (visual.ModelAnchor is { } anchor
            && AttachmentModelBindingResolver.TryResolveParent(
                mainModelFrame,
                anchor,
                mainModelPixelSize,
                mainModelReferenceHeight,
                mainModelTransform ?? presentedScene?.MainModel?.Transform ?? SceneTransform.Default,
                mainModelRasterTransform,
                out SceneTransform anchorParent))
        {
            point = new Point(
                bounds.Width / 2 + anchorParent.X / referenceHeight * bounds.Height,
                bounds.Height / 2 + anchorParent.Y / referenceHeight * bounds.Height);
            return true;
        }

        point = new Point(centerX, centerY);
        return true;
    }

    internal bool TryGetTopmostAttachmentAnchorSelector(
        Point point,
        Size bounds,
        double hitRadiusSquared,
        out Guid sourceId,
        out Point selector)
    {
        sourceId = Guid.Empty;
        selector = default;
        if (!double.IsFinite(hitRadiusSquared) || hitRadiusSquared < 0)
        {
            return false;
        }

        IEnumerable<SignalAttachmentVisual> candidates = visuals
            .Where(static visual => visual.Placement == AttachmentPlacement.AfterMainModel)
            .Reverse()
            .Concat(visuals
                .Where(static visual => visual.Placement == AttachmentPlacement.BeforeMainModel)
                .Reverse());
        foreach (SignalAttachmentVisual visual in candidates)
        {
            if (visual.IsLocked
                || visual.MountMode != AttachmentMountMode.MainModelAnchor
                || !TryGetAttachmentAnchorPoint(visual.SourceId, bounds, out Point candidate)
                || DistanceSquared(point, candidate) > hitRadiusSquared)
            {
                continue;
            }

            sourceId = visual.SourceId;
            selector = candidate;
            return true;
        }

        return false;
    }

    internal IReadOnlyList<AttachmentAnchorSelector> GetAttachmentAnchorSelectors(Size bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return [];
        }

        var selectors = new List<AttachmentAnchorSelector>();
        foreach (SignalAttachmentVisual visual in visuals)
        {
            if (visual.MountMode == AttachmentMountMode.MainModelAnchor
                && TryGetAttachmentAnchorPoint(visual.SourceId, bounds, out Point point))
            {
                selectors.Add(new AttachmentAnchorSelector(
                    visual.SourceId,
                    point,
                    string.IsNullOrWhiteSpace(visual.DisplayName)
                        ? visual.ResourceReference
                        : visual.DisplayName,
                    visual.ModelAnchor?.Kind ?? AttachmentModelAnchorKind.ModelPlane));
            }
        }

        return selectors;
    }

    internal bool TryCreateModelBinding(
        Guid sourceId,
        Point surfacePoint,
        Size bounds,
        double referenceHeight,
        ModelRasterTransform rasterTransform,
        out AttachmentModelAnchor? anchor,
        out SceneTransform? localTransform)
    {
        anchor = null;
        localTransform = null;
        SceneDocument? scene = presentedScene;
        if (scene?.MainModel is not { } mainModel)
        {
            return false;
        }

        // The scene apply runs asynchronously. During that short window there
        // may be no visual yet, but the persisted attachment still provides the
        // current world transform needed to create a stable local binding.
        SignalAttachmentVisual? visual = null;
        TryGetVisual(sourceId, out visual);
        AttachmentInstance? attachment = scene.Attachments.FirstOrDefault(
            candidate => candidate.SourceId == sourceId);
        if ((visual is not null && visual.MountMode != AttachmentMountMode.MainModelAnchor)
            || (visual is null && attachment?.MountMode != AttachmentMountMode.MainModelAnchor))
        {
            return false;
        }

        SceneTransform parent = mainModelTransform ?? mainModel.Transform;
        if (!AttachmentModelBindingResolver.TryCreate(
                mainModelFrame,
                surfacePoint,
                bounds,
                referenceHeight,
                parent,
                rasterTransform,
                out AttachmentModelBinding binding))
        {
            return false;
        }

        anchor = binding.Anchor;
        SceneTransform worldTransform = visual?.Transform
            ?? (attachment is not null
                ? AttachmentMountTransform.ResolveWorld(attachment, mainModel)
                : AttachmentMountTransform.Compose(parent, SceneTransform.Default));
        localTransform = AttachmentMountTransform.RelativeTo(worldTransform, binding.AnchorParent);
        return true;
    }

    internal bool TryCreateModelBindingAtVisualCenter(
        Guid sourceId,
        Size bounds,
        double referenceHeight,
        ModelRasterTransform rasterTransform,
        out AttachmentModelAnchor? anchor,
        out SceneTransform? localTransform)
    {
        long started = Stopwatch.GetTimestamp();
        anchor = null;
        localTransform = null;
        if (bounds.Width <= 0
            || bounds.Height <= 0
            || !TryGetVisual(sourceId, out SignalAttachmentVisual? visual)
            || visual is null)
        {
            SignalAttachmentScenePresenterLog.BindingPreparationRejected(
                logger,
                sourceId,
                mainModelFrame is not null,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return false;
        }

        Point center = new(
            bounds.Width / 2d + visual.Transform.X / referenceHeight * bounds.Height,
            bounds.Height / 2d + visual.Transform.Y / referenceHeight * bounds.Height);
        SceneDocument? scene = presentedScene;
        if (scene?.MainModel is not { } mainModel
            || !AttachmentModelBindingResolver.TryCreate(
                mainModelFrame,
                center,
                bounds,
                referenceHeight,
                mainModelTransform ?? mainModel.Transform,
                rasterTransform,
                out AttachmentModelBinding binding))
        {
            SignalAttachmentScenePresenterLog.BindingPreparationRejected(
                logger,
                sourceId,
                mainModelFrame is not null,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return false;
        }

        anchor = binding.Anchor;
        localTransform = AttachmentMountTransform.RelativeTo(
            visual.Transform,
            binding.AnchorParent);
        SignalAttachmentScenePresenterLog.BindingPrepared(
            logger,
            sourceId,
            binding.Anchor.Kind,
            mainModelFrame is not null,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return true;
    }

    internal bool UpdateAttachmentTransformPreview(
        Guid sourceId,
        SceneTransform worldTransform)
    {
        ArgumentNullException.ThrowIfNull(worldTransform);
        SceneDocument? scene = presentedScene;
        if (scene is null)
        {
            return false;
        }

        List<SignalAttachmentVisual>? updated = null;
        for (int index = 0; index < visuals.Count; index++)
        {
            SignalAttachmentVisual visual = visuals[index];
            if (visual.SourceId != sourceId || visual.Transform == worldTransform)
            {
                continue;
            }

            SceneTransform local = visual.MountMode == AttachmentMountMode.MainModelAnchor
                && scene.MainModel is { } mainModel
                && StringComparer.Ordinal.Equals(
                    visual.AnchorId,
                    mainModel.SourceId.ToString("N"))
                && TryGetAttachmentTransformParent(visual, mainModel, out SceneTransform parent)
                ? AttachmentMountTransform.RelativeTo(worldTransform, parent)
                : worldTransform;
            updated ??= [.. visuals];
            updated[index] = visual with
            {
                Transform = worldTransform,
                LocalTransform = local,
            };
        }

        if (updated is null)
        {
            return false;
        }

        visuals = updated;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal bool TryGetAttachmentTransformParent(
        Guid sourceId,
        out SceneTransform parent)
    {
        parent = mainModelTransform ?? SceneTransform.Default;
        if (!TryGetVisual(sourceId, out SignalAttachmentVisual? visual)
            || visual is null
            || presentedScene?.MainModel is not { } mainModel)
        {
            return false;
        }

        return TryGetAttachmentTransformParent(visual, mainModel, out parent);
    }

    private bool TryGetAttachmentTransformParent(
        SignalAttachmentVisual visual,
        MainModelInstance mainModel,
        out SceneTransform parent)
    {
        parent = mainModelTransform ?? mainModel.Transform;
        return visual.ModelAnchor is { } anchor
            && AttachmentModelBindingResolver.TryResolveParent(
                mainModelFrame,
                anchor,
                mainModelPixelSize,
                mainModelReferenceHeight,
                parent,
                mainModelRasterTransform,
                out parent);
    }

    internal bool UpdateAttachmentModelBindingPreview(
        Guid sourceId,
        AttachmentModelAnchor anchor,
        SceneTransform localTransform)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(localTransform);
        List<SignalAttachmentVisual>? updated = null;
        for (int index = 0; index < visuals.Count; index++)
        {
            SignalAttachmentVisual visual = visuals[index];
            if (visual.SourceId != sourceId
                || visual.MountMode != AttachmentMountMode.MainModelAnchor)
            {
                continue;
            }

            SceneTransform world = ResolveWorldTransform(
                mainModelTransform ?? SceneTransform.Default,
                localTransform,
                anchor,
                mainModelFrame,
                mainModelPixelSize,
                mainModelReferenceHeight,
                mainModelRasterTransform);
            updated ??= [.. visuals];
            updated[index] = visual with
            {
                ModelAnchor = anchor,
                LocalTransform = localTransform,
                Transform = world,
            };
        }

        if (updated is null)
        {
            return false;
        }

        visuals = updated;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal async Task ApplyAsync(
        SceneDocument? scene,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (scene is null)
            {
                List<SignalAttachmentVisual> previousVisuals = visuals;
                visuals = [];
                presentedScene = null;
                mainModelTransform = null;
                mainModelFrame = null;
                mainModelPixelSize = default;
                mainModelRasterTransform = ModelRasterTransform.Identity;
                Changed?.Invoke(this, EventArgs.Empty);
                await DisposeVisualsAsync(previousVisuals).ConfigureAwait(false);
                return;
            }

            var next = new List<SignalAttachmentVisual>();
            var started = new List<SignalAttachmentVisual>();
            bool mainModelChanged = presentedScene?.MainModel?.SourceId != scene.MainModel?.SourceId;
            mainModelTransform = scene.MainModel?.Transform;
            mainModelReferenceHeight = scene.ReferenceHeight;
            if (mainModelChanged)
            {
                mainModelFrame = null;
                mainModelPixelSize = default;
                mainModelRasterTransform = ModelRasterTransform.Identity;
            }
            Dictionary<Guid, SignalAttachmentVisual> previous = visuals.ToDictionary(
                static visual => visual.SourceId);
            try
            {
                foreach (AttachmentInstance attachment in scene.Attachments)
                {
                    if (!attachment.IsVisible)
                    {
                        continue;
                    }

                    try
                    {
                        if (previous.TryGetValue(attachment.SourceId, out SignalAttachmentVisual? existing)
                            && CanReusePlayback(existing, attachment))
                        {
                            previous.Remove(attachment.SourceId);
                            next.Add(CreateVisual(attachment, scene, existing.Playback));
                            continue;
                        }

                        IBackgroundVideoPlayback playback = await StartPlaybackAsync(attachment, cancellationToken)
                            .ConfigureAwait(false);
                        SignalAttachmentVisual created = CreateVisual(attachment, scene, playback);
                        started.Add(created);
                        next.Add(created);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        SignalAttachmentScenePresenterLog.StartFailed(
                            logger,
                            attachment.SourceTypeId,
                            attachment.ResourceReference,
                            exception.GetType().Name);
                    }
                }

                visuals = next;
                presentedScene = scene;
                Changed?.Invoke(this, EventArgs.Empty);
                await DisposeVisualsAsync(previous.Values).ConfigureAwait(false);
            }
            finally
            {
                if (!ReferenceEquals(visuals, next))
                {
                    await DisposeVisualsAsync(started).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            List<SignalAttachmentVisual> previous = visuals;
            visuals = [];
            Changed?.Invoke(this, EventArgs.Empty);
            await DisposeVisualsAsync(previous).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    internal void UpdateMainModelTransformPreview(Guid sourceId, SceneTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        SceneDocument? scene = presentedScene;
        if (scene?.MainModel is not { } mainModel || mainModel.SourceId != sourceId)
        {
            return;
        }

        mainModelTransform = transform;
        List<SignalAttachmentVisual>? updated = null;
        for (int index = 0; index < visuals.Count; index++)
        {
            SignalAttachmentVisual visual = visuals[index];
            if (visual.MountMode != AttachmentMountMode.MainModelAnchor
                || !StringComparer.Ordinal.Equals(visual.AnchorId, sourceId.ToString("N")))
            {
                continue;
            }

            updated ??= [.. visuals];
            updated[index] = visual with
            {
                Transform = ResolveWorldTransform(
                    transform,
                    visual.LocalTransform,
                    visual.ModelAnchor,
                    mainModelFrame,
                    mainModelPixelSize,
                    mainModelReferenceHeight,
                    mainModelRasterTransform),
            };
        }

        if (updated is not null)
        {
            visuals = updated;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void UpdateMainModelFrameState(
        Guid sourceId,
        ModelRenderFrame? frame,
        PixelSize pixelSize,
        double referenceHeight,
        ModelRasterTransform rasterTransform)
    {
        if (sourceId == Guid.Empty
            || !double.IsFinite(referenceHeight)
            || referenceHeight <= 0)
        {
            return;
        }

        SceneDocument? scene = presentedScene;
        if (scene?.MainModel is not { } mainModel || mainModel.SourceId != sourceId)
        {
            return;
        }

        mainModelFrame = frame;
        mainModelPixelSize = pixelSize;
        mainModelReferenceHeight = referenceHeight;
        mainModelRasterTransform = rasterTransform;

        List<SignalAttachmentVisual>? updated = null;
        for (int index = 0; index < visuals.Count; index++)
        {
            SignalAttachmentVisual visual = visuals[index];
            if (visual.MountMode != AttachmentMountMode.MainModelAnchor
                || !StringComparer.Ordinal.Equals(visual.AnchorId, sourceId.ToString("N")))
            {
                continue;
            }

            SceneTransform world = ResolveWorldTransform(
                mainModelTransform ?? mainModel.Transform,
                visual.LocalTransform,
                visual.ModelAnchor,
                frame,
                pixelSize,
                referenceHeight,
                rasterTransform);
            if (visual.Transform == world)
            {
                continue;
            }

            updated ??= [.. visuals];
            updated[index] = visual with { Transform = world };
        }

        if (updated is not null)
        {
            visuals = updated;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static async Task DisposeVisualsAsync(IEnumerable<SignalAttachmentVisual> values)
    {
        foreach (SignalAttachmentVisual visual in values)
        {
            await visual.Playback.DisposeAsync().ConfigureAwait(false);
        }
    }

    private SignalAttachmentVisual CreateVisual(
        AttachmentInstance attachment,
        SceneDocument scene,
        IBackgroundVideoPlayback playback)
    {
        SceneTransform world = AttachmentMountTransform.ResolveWorld(attachment, scene.MainModel);
        if (attachment.MountMode == AttachmentMountMode.MainModelAnchor
            && scene.MainModel is { } mainModel
            && StringComparer.Ordinal.Equals(
                attachment.AnchorId,
                mainModel.SourceId.ToString("N")))
        {
            world = ResolveWorldTransform(
                mainModelTransform ?? mainModel.Transform,
                attachment.Transform,
                attachment.ModelAnchor,
                mainModelFrame,
                mainModelPixelSize,
                mainModelReferenceHeight,
                mainModelRasterTransform);
        }

        return new SignalAttachmentVisual(
            attachment.SourceId,
            attachment.DisplayName,
            attachment.SourceTypeId,
            attachment.ResourceReference,
            attachment.VideoOptions,
            playback,
            world,
            attachment.Transform,
            attachment.MountMode,
            attachment.AnchorId,
            attachment.ModelAnchor,
            scene.ReferenceHeight,
            attachment.Placement,
            attachment.IsLocked);
    }

    private static bool CanReusePlayback(
        SignalAttachmentVisual visual,
        AttachmentInstance attachment) =>
        StringComparer.Ordinal.Equals(visual.SourceTypeId, attachment.SourceTypeId)
            && StringComparer.Ordinal.Equals(visual.ResourceReference, attachment.ResourceReference)
            && visual.VideoOptions == attachment.VideoOptions;

    private static SceneTransform ResolveWorldTransform(
        SceneTransform mainModelTransform,
        SceneTransform localTransform,
        AttachmentModelAnchor? modelAnchor,
        ModelRenderFrame? frame,
        PixelSize pixelSize,
        double referenceHeight,
        ModelRasterTransform rasterTransform)
    {
        if (modelAnchor is null
            || !AttachmentModelBindingResolver.TryResolveParent(
                frame,
                modelAnchor,
                pixelSize,
                referenceHeight,
                mainModelTransform,
                rasterTransform,
                out SceneTransform anchorParent))
        {
            return AttachmentMountTransform.Compose(mainModelTransform, localTransform);
        }

        return AttachmentMountTransform.Compose(anchorParent, localTransform);
    }

    private static double DistanceSquared(Point first, Point second)
    {
        double x = first.X - second.X;
        double y = first.Y - second.Y;
        return x * x + y * y;
    }

    private async Task<IBackgroundVideoPlayback> StartPlaybackAsync(
        AttachmentInstance attachment,
        CancellationToken cancellationToken)
    {
        switch (attachment.SourceTypeId)
        {
            case "attachment.image":
                if (imageDecoder is null)
                {
                    throw new NotSupportedException("Image attachment playback is unavailable.");
                }

                return new StaticImagePlayback(await imageDecoder
                    .DecodeAsync(attachment.ResourceReference, cancellationToken)
                    .ConfigureAwait(false));
            case "attachment.video":
                if (videoFactory is null)
                {
                    throw new NotSupportedException("Video attachment playback is unavailable.");
                }

                return await videoFactory.StartAsync(
                    attachment.ResourceReference,
                    attachment.VideoOptions,
                    cancellationToken).ConfigureAwait(false);
            case "attachment.spout2":
                return await playbackFactory.StartAsync(
                    new VideoSignalSourceSelection(VideoSignalProtocol.Spout2, attachment.ResourceReference),
                    cancellationToken).ConfigureAwait(false);
            case "attachment.ndi":
                return await playbackFactory.StartAsync(
                    new VideoSignalSourceSelection(VideoSignalProtocol.Ndi, attachment.ResourceReference),
                    cancellationToken).ConfigureAwait(false);
            default:
                throw new NotSupportedException($"Attachment type '{attachment.SourceTypeId}' is unavailable.");
        }
    }
}

internal static partial class SignalAttachmentScenePresenterLog
{
    [LoggerMessage(6882, LogLevel.Warning, "Scene attachment start failed for {SourceTypeId}:{ResourceReference}: {ErrorType}")]
    internal static partial void StartFailed(
        ILogger logger,
        string sourceTypeId,
        string resourceReference,
        string errorType);

    [LoggerMessage(6883, LogLevel.Information, "Scene attachment binding prepared for {SourceId}: kind={Kind}, frameAvailable={FrameAvailable}, duration={DurationMs} ms")]
    internal static partial void BindingPrepared(
        ILogger logger,
        Guid sourceId,
        AttachmentModelAnchorKind kind,
        bool frameAvailable,
        double durationMs);

    [LoggerMessage(6884, LogLevel.Warning, "Scene attachment binding preparation rejected for {SourceId}: frameAvailable={FrameAvailable}, duration={DurationMs} ms")]
    internal static partial void BindingPreparationRejected(
        ILogger logger,
        Guid sourceId,
        bool frameAvailable,
        double durationMs);
}
