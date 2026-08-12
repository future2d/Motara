using System.Collections.Immutable;
using System.Diagnostics;
using Avalonia;
using Avalonia.Rendering.SceneGraph;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.ModelLibrary;
using Motara.ModelRuntime.Abstractions;
using Motara.ModelRuntime.PurismCore;
using Motara.App.Screenshots;
using SkiaSharp;

namespace Motara.App.Models;

internal interface IModelFrameRenderer : IAsyncDisposable
{
    Task FirstFrameRendered { get; }

    Task PrepareFrameAsync(
        ModelRenderFrame frame,
        PixelSize pixelSize,
        CancellationToken cancellationToken);

    Task PrepareFrameAsync(
        ModelRenderFrame frame,
        PixelSize pixelSize,
        ModelRasterTransform rasterTransform,
        CancellationToken cancellationToken) =>
        PrepareFrameAsync(frame, pixelSize, cancellationToken);

    ICustomDrawOperation CreateDrawOperation(
        ModelRenderFrame frame,
        PixelSize pixelSize,
        double renderingScale);

    ICustomDrawOperation CreateDrawOperation(
        ModelRenderFrame frame,
        PixelSize pixelSize,
        double renderingScale,
        ModelRasterTransform rasterTransform) =>
        CreateDrawOperation(frame, pixelSize, renderingScale);
}

internal interface IModelFramePreparationTarget
{
    bool RequiresFramePreparation { get; }

    int? FullFrameCacheFramesPerSecond => null;
}

internal interface IModelGpuFrameRenderer
{
    bool TryRenderGpuFrame(
        GRContext context,
        SKCanvas canvas,
        ModelRenderFrame frame,
        PixelSize pixelSize,
        double renderingScale);

    bool TryRenderGpuFrame(
        GRContext context,
        SKCanvas canvas,
        ModelRenderFrame frame,
        PixelSize pixelSize,
        double renderingScale,
        ModelRasterTransform rasterTransform) =>
        TryRenderGpuFrame(context, canvas, frame, pixelSize, renderingScale);

    void ReclaimReleasedGpuResources(GRContext? activeContext);

    void ReportGpuCompositionFailure(ModelRenderingBackendFaultReason faultReason);
}

internal interface IModelRenderingBackendTarget
{
    ModelRenderingBackendStatus RenderingBackendStatus { get; }

    event EventHandler<ModelRenderingBackendStatus>? RenderingBackendStatusChanged;

    void SetRenderingBackendPreference(ModelRenderingBackendPreference preference);
}

internal interface IModelFrameEffectTarget
{
    void SetBlurRadius(double? radius);
}

internal interface IModelScreenshotRenderer
{
    Task<SKImage> CaptureFrameAsync(
        ModelRenderFrame frame,
        PixelSize pixelSize,
        SKRect destination,
        SKColor background,
        CancellationToken cancellationToken);
}

internal interface IModelRenderWorkSource
{
    event EventHandler? WorkPending;
}

internal interface IModelRenderMaintenance
{
    bool HasPendingWork { get; }

    event EventHandler? WorkPending;

    ICustomDrawOperation CreateMaintenanceOperation(Rect bounds);
}

internal interface IModelFrameRendererFactory
{
    Task<IModelFrameRenderer> CreateAsync(
        IModelAssetSource assets,
        ImmutableArray<string> textureAssetIds,
        CancellationToken cancellationToken);
}

internal sealed class SkiaModelFrameRendererFactory
    : IModelFrameRendererFactory, IModelRenderMaintenance
{
    private readonly ILogger<SkiaModelRenderer> logger;
    private readonly SkiaModelRenderMaintenance maintenance;

    internal SkiaModelFrameRendererFactory()
        : this(NullLogger<SkiaModelRenderer>.Instance, new SkiaModelRenderMaintenance())
    {
    }

    internal SkiaModelFrameRendererFactory(ILogger<SkiaModelRenderer> logger)
        : this(logger, new SkiaModelRenderMaintenance(logger))
    {
    }

    internal SkiaModelFrameRendererFactory(
        ILogger<SkiaModelRenderer> logger,
        SkiaModelRenderMaintenance maintenance)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(maintenance);
        this.logger = logger;
        this.maintenance = maintenance;
    }

    public bool HasPendingWork => maintenance.HasPendingWork;

    public event EventHandler? WorkPending
    {
        add => maintenance.WorkPending += value;
        remove => maintenance.WorkPending -= value;
    }

    public ICustomDrawOperation CreateMaintenanceOperation(Rect bounds) =>
        maintenance.CreateMaintenanceOperation(bounds);

    public async Task<IModelFrameRenderer> CreateAsync(
        IModelAssetSource assets,
        ImmutableArray<string> textureAssetIds,
        CancellationToken cancellationToken)
    {
        SkiaModelRenderer renderer = await SkiaModelRenderer.CreateAsync(
            assets,
            textureAssetIds,
            logger,
            maintenance,
            cancellationToken).ConfigureAwait(false);
        return new SkiaModelFrameRenderer(renderer);
    }

    private sealed class SkiaModelFrameRenderer(SkiaModelRenderer renderer)
        : IModelFrameRenderer,
            IModelFramePreparationTarget,
            IModelGpuFrameRenderer,
            IModelRenderingBackendTarget,
            IModelFrameEffectTarget,
            IModelScreenshotRenderer,
            IModelRenderWorkSource
    {
        private readonly object effectGate = new();
        private double? blurRadius;

        public Task FirstFrameRendered => renderer.FirstFrameRendered;

        public bool RequiresFramePreparation => renderer.RequiresFramePreparation;

        public int? FullFrameCacheFramesPerSecond => renderer.FullFrameCacheFramesPerSecond;

        public ModelRenderingBackendStatus RenderingBackendStatus => renderer.RenderingBackendStatus;

        public event EventHandler<ModelRenderingBackendStatus>? RenderingBackendStatusChanged
        {
            add => renderer.RenderingBackendStatusChanged += value;
            remove => renderer.RenderingBackendStatusChanged -= value;
        }

        public event EventHandler? WorkPending
        {
            add => renderer.ScreenshotWorkPending += value;
            remove => renderer.ScreenshotWorkPending -= value;
        }

        public Task PrepareFrameAsync(
            ModelRenderFrame frame,
            PixelSize pixelSize,
            CancellationToken cancellationToken) => renderer.PrepareFrameAsync(
                frame,
                pixelSize,
                cancellationToken);

        public Task PrepareFrameAsync(
            ModelRenderFrame frame,
            PixelSize pixelSize,
            ModelRasterTransform rasterTransform,
            CancellationToken cancellationToken) => renderer.PrepareFrameAsync(
                frame,
                pixelSize,
                rasterTransform,
                cancellationToken);

        public ICustomDrawOperation CreateDrawOperation(
            ModelRenderFrame frame,
            PixelSize pixelSize,
            double renderingScale) => renderer.CreateDrawOperation(
                frame,
                pixelSize,
                renderingScale,
                blurRadius);

        public ICustomDrawOperation CreateDrawOperation(
            ModelRenderFrame frame,
            PixelSize pixelSize,
            double renderingScale,
            ModelRasterTransform rasterTransform) => renderer.CreateDrawOperation(
                frame,
                pixelSize,
                renderingScale,
                rasterTransform,
                blurRadius);

        public void SetBlurRadius(double? radius)
        {
            lock (effectGate)
            {
                blurRadius = radius;
            }
        }

        public bool TryRenderGpuFrame(
            GRContext context,
            SKCanvas canvas,
            ModelRenderFrame frame,
            PixelSize pixelSize,
            double renderingScale)
            => TryRenderGpuFrame(
                context,
                canvas,
                frame,
                pixelSize,
                renderingScale,
                ModelRasterTransform.Identity);

        public bool TryRenderGpuFrame(
            GRContext context,
            SKCanvas canvas,
            ModelRenderFrame frame,
            PixelSize pixelSize,
            double renderingScale,
            ModelRasterTransform rasterTransform)
        {
            double? radius;
            lock (effectGate)
            {
                radius = blurRadius;
            }

            using SKPaint? paint = radius is > 0
                ? new SKPaint
                {
                    ImageFilter = SKImageFilter.CreateBlur(
                        (float)radius.Value,
                        (float)radius.Value),
                }
                : null;
            canvas.Save();
            canvas.Scale((float)renderingScale);
            try
            {
                return renderer.TryRenderGpuFrame(
                    context,
                    canvas,
                    frame,
                    pixelSize,
                    renderingScale,
                    rasterTransform,
                    paint);
            }
            finally
            {
                canvas.Restore();
            }
        }

        public void ReclaimReleasedGpuResources(GRContext? activeContext) =>
            renderer.ReclaimReleasedGpuResources(activeContext);

        public void ReportGpuCompositionFailure(ModelRenderingBackendFaultReason faultReason) =>
            renderer.ReportGpuCompositionFailure(faultReason);

        public void SetRenderingBackendPreference(ModelRenderingBackendPreference preference) =>
            renderer.SetRenderingBackendPreference(preference);

        public Task<SKImage> CaptureFrameAsync(
            ModelRenderFrame frame,
            PixelSize pixelSize,
            SKRect destination,
            SKColor background,
            CancellationToken cancellationToken) => renderer.CaptureFrameAsync(
                frame,
                pixelSize,
                destination,
                background,
                cancellationToken);

        public ValueTask DisposeAsync() => renderer.DisposeAsync();
    }
}

internal sealed record ActiveModel(
    ModelId Id,
    IModelRuntime Runtime,
    IModelFrameRenderer Renderer,
    ModelDescriptor? Descriptor = null);

internal sealed class ModelSelectionController
    : IActiveModelSource, IScreenshotModelFrameSource, IModelRenderMaintenance, IAsyncDisposable
{
    private sealed record SelectionOperation(
        long Generation,
        CancellationTokenSource Cancellation,
        CancellationToken CallerToken);

    private readonly IModelRuntimeFactory runtimeFactory;
    private readonly IModelFrameRendererFactory rendererFactory;
    private readonly IModelCatalog catalog;
    private readonly ILogger<ModelSelectionController> logger;
    private readonly IModelRenderMaintenance? renderMaintenance;
    private readonly object stateGate = new();
    private readonly SemaphoreSlim selectionGate = new(1, 1);
    private ActiveModel? active;
    private ModelRenderingBackendPreference renderingBackendPreference =
        ModelRenderingBackendPreference.Cpu;
    private ModelRenderingBackendStatus renderingBackendStatus = ModelRenderingBackendStatus.Cpu;
    private IModelRenderingBackendTarget? renderingBackendTarget;
    private EventHandler<ModelRenderingBackendStatus>? renderingBackendStatusHandler;
    private IModelRenderWorkSource? renderWorkSource;
    private EventHandler? renderWorkHandler;
    private CancellationTokenSource? selectionCancellation;
    private long requestGeneration;
    private bool disposed;

    internal ModelSelectionController(
        IModelRuntimeFactory runtimeFactory,
        IModelFrameRendererFactory rendererFactory,
        IModelCatalog catalog)
        : this(runtimeFactory, rendererFactory, catalog, null)
    {
    }

    internal ModelSelectionController(
        IModelRuntimeFactory runtimeFactory,
        IModelFrameRendererFactory rendererFactory,
        IModelCatalog catalog,
        ILogger<ModelSelectionController>? logger)
    {
        ArgumentNullException.ThrowIfNull(runtimeFactory);
        ArgumentNullException.ThrowIfNull(rendererFactory);
        ArgumentNullException.ThrowIfNull(catalog);
        this.runtimeFactory = runtimeFactory;
        this.rendererFactory = rendererFactory;
        this.catalog = catalog;
        this.logger = logger ?? NullLogger<ModelSelectionController>.Instance;
        renderMaintenance = rendererFactory as IModelRenderMaintenance;
        if (renderMaintenance is not null)
        {
            renderMaintenance.WorkPending += OnRenderWorkPending;
        }
    }

    internal event EventHandler? ActiveChanged;

    internal event EventHandler<ModelRenderingBackendStatus>? RenderingBackendStatusChanged;

    public event EventHandler? WorkPending;

    public bool HasPendingWork => renderMaintenance?.HasPendingWork == true;

    public ICustomDrawOperation CreateMaintenanceOperation(Rect bounds) =>
        renderMaintenance?.CreateMaintenanceOperation(bounds)
        ?? throw new InvalidOperationException("Model render maintenance is unavailable.");

    internal ActiveModel? Active => Volatile.Read(ref active);

    internal ModelRenderingBackendStatus RenderingBackendStatus
    {
        get
        {
            lock (stateGate)
            {
                return renderingBackendStatus;
            }
        }
    }

    event EventHandler? IActiveModelSource.ActiveChanged
    {
        add => ActiveChanged += value;
        remove => ActiveChanged -= value;
    }

    ActiveModel? IActiveModelSource.Active => Active;

    internal void SetRenderingBackendPreference(ModelRenderingBackendPreference preference)
    {
        if (!Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        IModelRenderingBackendTarget? target;
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            renderingBackendPreference = preference;
            target = active?.Renderer as IModelRenderingBackendTarget;
        }

        if (target is null)
        {
            PublishRenderingBackendStatus(ModelRenderingBackendStatus.Cpu);
            return;
        }

        target.SetRenderingBackendPreference(preference);
        PublishRenderingBackendStatus(target.RenderingBackendStatus);
    }

    async Task<SKImage?> IScreenshotModelFrameSource.CaptureCurrentFrameAsync(
        PixelSize pixelSize,
        SKRect destination,
        SKColor background,
        CancellationToken cancellationToken)
    {
        Task<SKImage>? capture;
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ActiveModel? current = active;
            ModelRenderFrame? frame = current?.Runtime.CurrentFrame;
            IModelScreenshotRenderer? renderer = current?.Renderer as IModelScreenshotRenderer;
            capture = renderer is not null && frame is not null
                ? renderer.CaptureFrameAsync(
                    frame,
                    pixelSize,
                    destination,
                    background,
                    cancellationToken)
                : null;
        }

        return capture is null
            ? null
            : await capture.ConfigureAwait(false);
    }

    internal Task<bool> SelectAsync(ModelId modelId, CancellationToken cancellationToken) =>
        SelectAsync(modelId, initialFramePixelSize: null, cancellationToken);

    internal async Task<ModelCapabilities?> GetCapabilitiesAsync(
        ModelId modelId,
        CancellationToken cancellationToken)
    {
        if (Active is { } current && current.Id == modelId)
        {
            return current.Runtime.Capabilities;
        }

        ModelDescriptor? descriptor = catalog.Current.Entries
            .FirstOrDefault(entry => entry.Id == modelId && entry.IsSelectable)?.Descriptor;
        if (descriptor is null)
        {
            return null;
        }

        IModelRuntime runtime = runtimeFactory.Create();
        try
        {
            await using FileModelAssetSource assets = FileModelAssetSource.Create(descriptor);
            ModelLoadResult result = await runtime.LoadAsync(
                new ModelLoadRequest(
                    assets,
                    assets.DescriptorAssetId,
                    assets.NativeModelAssetId,
                    assets.TextureAssetIds)
                {
                    ParameterNames = descriptor.ParameterNames,
                    AuxiliaryAssets = descriptor.AuxiliaryAssets,
                },
                cancellationToken).ConfigureAwait(false);
            return result.Capabilities;
        }
        finally
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal async Task ClearAsync(CancellationToken cancellationToken)
    {
        SelectionOperation operation = BeginSelectionOperation(cancellationToken);
        bool gateEntered = false;
        try
        {
            try
            {
                await selectionGate.WaitAsync(operation.Cancellation.Token).ConfigureAwait(false);
                gateEntered = true;
            }
            catch (OperationCanceledException) when (!operation.CallerToken.IsCancellationRequested)
            {
                ModelSelectionLog.SelectionSuperseded(logger, operation.Generation);
                return;
            }

            if (!IsCurrentSelection(operation.Generation))
            {
                ModelSelectionLog.SelectionSuperseded(logger, operation.Generation);
                return;
            }

            ActiveModel? previous = TakeActive(operation.Generation);
            if (previous is null)
            {
                return;
            }

            ActiveChanged?.Invoke(this, EventArgs.Empty);
            DetachRenderingBackendTarget(previous.Renderer);
            DetachRenderWorkSource(previous.Renderer);
            await DisposeActiveModelAsync(previous).ConfigureAwait(false);
            PublishRenderingBackendStatus(ModelRenderingBackendStatus.Cpu);
            ModelSelectionLog.ActiveCleared(logger);
        }
        finally
        {
            if (gateEntered)
            {
                selectionGate.Release();
            }

            CompleteSelectionOperation(operation);
        }
    }

    internal async Task<bool> SelectAsync(
        ModelId modelId,
        PixelSize? initialFramePixelSize,
        CancellationToken cancellationToken)
    {
        SelectionOperation operation = BeginSelectionOperation(cancellationToken);
        string operationId = Guid.NewGuid().ToString("N");
        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["OperationId"] = operationId,
        });
        ModelSelectionLog.SelectionStarted(logger);
        bool gateEntered = false;
        try
        {
            try
            {
                await selectionGate.WaitAsync(operation.Cancellation.Token).ConfigureAwait(false);
                gateEntered = true;
            }
            catch (OperationCanceledException) when (!operation.CallerToken.IsCancellationRequested)
            {
                ModelSelectionLog.SelectionSuperseded(logger, operation.Generation);
                return false;
            }

            if (!IsCurrentSelection(operation.Generation))
            {
                ModelSelectionLog.SelectionSuperseded(logger, operation.Generation);
                return false;
            }

            ModelCatalogEntry? entry = catalog.Current.Entries.FirstOrDefault(
                candidate => candidate.Id == modelId && candidate.IsSelectable);
            ModelDescriptor? descriptor = entry?.Descriptor;
            if (descriptor is null)
            {
                ModelSelectionLog.SelectionFailed(logger, "NotSelectable");
                return false;
            }

            ActiveModel? previous = TakeActive(operation.Generation);
            if (previous is not null)
            {
                ActiveChanged?.Invoke(this, EventArgs.Empty);
                DetachRenderingBackendTarget(previous.Renderer);
                DetachRenderWorkSource(previous.Renderer);
                await DisposeActiveModelAsync(previous).ConfigureAwait(false);
                PublishRenderingBackendStatus(ModelRenderingBackendStatus.Cpu);
            }

            if (!IsCurrentSelection(operation.Generation))
            {
                ModelSelectionLog.SelectionSuperseded(logger, operation.Generation);
                return false;
            }

            IModelRuntime candidateRuntime = runtimeFactory.Create();
            IModelFrameRenderer? candidateRenderer = null;
            bool committed = false;
            try
            {
                await using FileModelAssetSource assets = FileModelAssetSource.Create(descriptor);
                var request = new ModelLoadRequest(
                    assets,
                    assets.DescriptorAssetId,
                    assets.NativeModelAssetId,
                    assets.TextureAssetIds)
                {
                    ParameterNames = descriptor.ParameterNames,
                    AuxiliaryAssets = descriptor.AuxiliaryAssets,
                };
                long runtimeLoadStartedAt = Stopwatch.GetTimestamp();
                ModelLoadResult result = await candidateRuntime.LoadAsync(
                        request,
                        operation.Cancellation.Token)
                    .ConfigureAwait(false);
                operation.Cancellation.Token.ThrowIfCancellationRequested();
                if (!result.IsSuccess)
                {
                    ModelSelectionLog.SelectionFailed(
                        logger,
                        result.Error?.Code.ToString() ?? "Unknown");
                    return false;
                }

                ModelSelectionLog.RuntimeLoaded(
                    logger,
                    Stopwatch.GetElapsedTime(runtimeLoadStartedAt).TotalMilliseconds);
                if (!IsCurrentSelection(operation.Generation))
                {
                    ModelSelectionLog.SelectionSuperseded(logger, operation.Generation);
                    return false;
                }

                long rendererCreateStartedAt = Stopwatch.GetTimestamp();
                candidateRenderer = await rendererFactory.CreateAsync(
                    assets,
                    assets.TextureAssetIds,
                    operation.Cancellation.Token).ConfigureAwait(false);
                operation.Cancellation.Token.ThrowIfCancellationRequested();
                ModelSelectionLog.RendererCreated(
                    logger,
                    Stopwatch.GetElapsedTime(rendererCreateStartedAt).TotalMilliseconds);
                if (!IsCurrentSelection(operation.Generation))
                {
                    ModelSelectionLog.SelectionSuperseded(logger, operation.Generation);
                    return false;
                }

                if (initialFramePixelSize is PixelSize pixelSize && result.Frame is ModelRenderFrame frame)
                {
                    await candidateRenderer.PrepareFrameAsync(
                            frame,
                            pixelSize,
                            operation.Cancellation.Token)
                        .ConfigureAwait(false);
                    operation.Cancellation.Token.ThrowIfCancellationRequested();
                }

                lock (stateGate)
                {
                    if (disposed || operation.Generation != requestGeneration)
                    {
                        return false;
                    }

                    active = new ActiveModel(modelId, candidateRuntime, candidateRenderer, descriptor);
                    committed = true;
                }

                ConfigureRenderingBackendTarget(candidateRenderer);
                ConfigureRenderWorkSource(candidateRenderer);
                ModelSelectionLog.ActiveCommitted(logger);
                ActiveChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            finally
            {
                if (!committed)
                {
                    if (candidateRenderer is not null)
                    {
                        await candidateRenderer.DisposeAsync().ConfigureAwait(false);
                    }

                    await candidateRuntime.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!operation.CallerToken.IsCancellationRequested)
        {
            ModelSelectionLog.SelectionSuperseded(logger, operation.Generation);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ModelSelectionLog.SelectionFailed(logger, exception.GetType().Name);
            throw;
        }
        finally
        {
            if (gateEntered)
            {
                selectionGate.Release();
            }

            CompleteSelectionOperation(operation);
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? pendingCancellation;
        lock (stateGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            requestGeneration++;
            pendingCancellation = selectionCancellation;
        }

        TryCancel(pendingCancellation);
        await selectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ActiveModel? previous;
            lock (stateGate)
            {
                previous = active;
                active = null;
            }

            ActiveChanged?.Invoke(this, EventArgs.Empty);
            if (previous is not null)
            {
                DetachRenderingBackendTarget(previous.Renderer);
                DetachRenderWorkSource(previous.Renderer);
                await DisposeActiveModelAsync(previous).ConfigureAwait(false);
            }
        }
        finally
        {
            selectionGate.Release();
        }

        PublishRenderingBackendStatus(ModelRenderingBackendStatus.Cpu);
        if (renderMaintenance is not null)
        {
            renderMaintenance.WorkPending -= OnRenderWorkPending;
        }
    }

    private SelectionOperation BeginSelectionOperation(CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        CancellationTokenSource? previous;
        long generation;
        lock (stateGate)
        {
            if (disposed)
            {
                cancellation.Dispose();
                throw new ObjectDisposedException(nameof(ModelSelectionController));
            }

            generation = ++requestGeneration;
            previous = selectionCancellation;
            selectionCancellation = cancellation;
        }

        TryCancel(previous);
        ModelSelectionLog.SelectionRequestQueued(logger, generation, previous is not null);
        return new SelectionOperation(generation, cancellation, callerToken);
    }

    private void CompleteSelectionOperation(SelectionOperation operation)
    {
        lock (stateGate)
        {
            if (ReferenceEquals(selectionCancellation, operation.Cancellation))
            {
                selectionCancellation = null;
            }
        }

        operation.Cancellation.Dispose();
    }

    private bool IsCurrentSelection(long generation)
    {
        lock (stateGate)
        {
            return !disposed && generation == requestGeneration;
        }
    }

    private ActiveModel? TakeActive(long generation)
    {
        lock (stateGate)
        {
            if (disposed || generation != requestGeneration)
            {
                return null;
            }

            ActiveModel? previous = active;
            active = null;
            return previous;
        }
    }

    private async Task DisposeActiveModelAsync(ActiveModel model)
    {
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            await model.Renderer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await model.Runtime.DisposeAsync().ConfigureAwait(false);
        }

        ModelSelectionLog.ActiveRetired(
            logger,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ConfigureRenderWorkSource(IModelFrameRenderer renderer)
    {
        DetachRenderWorkSource(renderer);
        if (renderer is not IModelRenderWorkSource source)
        {
            return;
        }

        EventHandler handler = OnRenderWorkPending;
        renderWorkSource = source;
        renderWorkHandler = handler;
        source.WorkPending += handler;
    }

    private void DetachRenderWorkSource(IModelFrameRenderer renderer)
    {
        if (renderWorkSource is not null && renderWorkHandler is not null)
        {
            renderWorkSource.WorkPending -= renderWorkHandler;
        }

        renderWorkSource = null;
        renderWorkHandler = null;
    }

    private void OnRenderWorkPending(object? sender, EventArgs args) =>
        WorkPending?.Invoke(this, EventArgs.Empty);

    private void ConfigureRenderingBackendTarget(IModelFrameRenderer renderer)
    {
        if (renderer is not IModelRenderingBackendTarget target)
        {
            PublishRenderingBackendStatus(ModelRenderingBackendStatus.Cpu);
            return;
        }

        ModelRenderingBackendPreference preference;
        lock (stateGate)
        {
            if (disposed || !ReferenceEquals(active?.Renderer, renderer))
            {
                return;
            }

            preference = renderingBackendPreference;
            EventHandler<ModelRenderingBackendStatus> handler = (_, status) =>
                OnRenderingBackendStatusChanged(target, status);
            renderingBackendTarget = target;
            renderingBackendStatusHandler = handler;
            target.RenderingBackendStatusChanged += handler;
        }

        target.SetRenderingBackendPreference(preference);
        PublishRenderingBackendStatus(target.RenderingBackendStatus);
    }

    private void DetachRenderingBackendTarget(IModelFrameRenderer renderer)
    {
        if (renderer is not IModelRenderingBackendTarget target)
        {
            return;
        }

        EventHandler<ModelRenderingBackendStatus>? handler = null;
        lock (stateGate)
        {
            if (ReferenceEquals(renderingBackendTarget, target))
            {
                handler = renderingBackendStatusHandler;
                renderingBackendTarget = null;
                renderingBackendStatusHandler = null;
            }
        }

        if (handler is not null)
        {
            target.RenderingBackendStatusChanged -= handler;
        }
    }

    private void OnRenderingBackendStatusChanged(
        IModelRenderingBackendTarget target,
        ModelRenderingBackendStatus status)
    {
        lock (stateGate)
        {
            if (disposed
                || !ReferenceEquals(active?.Renderer, target)
                || !ReferenceEquals(renderingBackendTarget, target))
            {
                return;
            }
        }

        PublishRenderingBackendStatus(status);
    }

    private void PublishRenderingBackendStatus(ModelRenderingBackendStatus status)
    {
        bool changed;
        lock (stateGate)
        {
            if (disposed || renderingBackendStatus == status)
            {
                return;
            }

            renderingBackendStatus = status;
            changed = true;
        }

        if (changed)
        {
            ModelSelectionLog.RenderingBackendStatusChanged(
                logger,
                status.State,
                status.LastFaultReason);
            RenderingBackendStatusChanged?.Invoke(this, status);
        }
    }
}

internal static partial class ModelSelectionLog
{
    [LoggerMessage(6100, LogLevel.Information, "Model selection started")]
    internal static partial void SelectionStarted(ILogger logger);

    [LoggerMessage(6101, LogLevel.Debug, "Model runtime load completed in {DurationMs} ms")]
    internal static partial void RuntimeLoaded(ILogger logger, double durationMs);

    [LoggerMessage(6102, LogLevel.Debug, "Model frame renderer created in {DurationMs} ms")]
    internal static partial void RendererCreated(ILogger logger, double durationMs);

    [LoggerMessage(6103, LogLevel.Information, "Active model committed")]
    internal static partial void ActiveCommitted(ILogger logger);

    [LoggerMessage(6104, LogLevel.Warning, "Model selection failed with {ErrorCode}")]
    internal static partial void SelectionFailed(ILogger logger, string errorCode);

    [LoggerMessage(6105, LogLevel.Debug,
        "Model selection generation {Generation} superseded by a newer request")]
    internal static partial void SelectionSuperseded(ILogger logger, long generation);

    [LoggerMessage(6106, LogLevel.Information, "Active model cleared")]
    internal static partial void ActiveCleared(ILogger logger);

    [LoggerMessage(6107, LogLevel.Information, "Model rendering backend changed to {State} with fault {FaultReason}")]
    internal static partial void RenderingBackendStatusChanged(
        ILogger logger,
        ModelRenderingBackendState state,
        ModelRenderingBackendFaultReason? faultReason);

    [LoggerMessage(6108, LogLevel.Debug,
        "Model selection generation {Generation} queued; previous request superseded: {SupersededPrevious}")]
    internal static partial void SelectionRequestQueued(
        ILogger logger,
        long generation,
        bool supersededPrevious);

    [LoggerMessage(6109, LogLevel.Information,
        "Previous active model resources retired in {DurationMs} ms")]
    internal static partial void ActiveRetired(ILogger logger, double durationMs);
}
