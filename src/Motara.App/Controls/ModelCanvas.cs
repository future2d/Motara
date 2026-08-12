using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Models;
using Motara.ModelRuntime.Abstractions;
using Motara.App.Rendering;
using Motara.Media;
using Motara.ModelLibrary;
using Motara.Persistence;
using Motara.Scene;

namespace Motara.App.Controls;

public sealed class ModelCanvas : Control, IDisposable
{
    private sealed record PendingGpuFrameState(
        ActiveModel Active,
        ModelRenderFrame Frame,
        PixelSize PixelSize,
        ModelRasterTransform RasterTransform);

    private readonly DispatcherTimer frameTimer;
    private readonly FrameRefreshPacer frameRefreshPacer = new();
    private readonly ModelRenderStateSource renderStateSource = new();
    private readonly PresentationFrameRateSampler presentationFrameRateSampler = new();
    private ModelSelectionController? controller;
    private ActiveModelMotionExpansionSource? motionExpansionSource;
    private ActiveModel? lastInvalidatedModel;
    private long lastInvalidatedRevision = -1;
    private PixelSize lastInvalidatedPixelSize;
    private Task? framePreparationTask;
    private CancellationTokenSource? framePreparationCancellation;
    private ActiveModel? pendingPreparationModel;
    private ModelRenderFrame? pendingPreparationFrame;
    private PixelSize pendingPreparationPixelSize;
    private ModelRasterTransform pendingPreparationRasterTransform;
    private ModelRasterTransform lastInvalidatedRasterTransform = ModelRasterTransform.Identity;
    private double? blurRadius;
    private FrameRateMode frameRateMode = FrameRateMode.FramesPerSecond60;
    private ModelRenderingBackendState renderingBackendState = ModelRenderingBackendState.Cpu;
    private int schedulerGeneration;
    private long framePreparationGeneration;
    private int framePreparationFailureLogged;
    private ActiveModel? reportedFrameRateModel;
    private double? reportedFrameRate;
    private ILogger<ModelCanvas> logger = NullLogger<ModelCanvas>.Instance;
    private GpuCompositionModelPresenter? gpuCompositionPresenter;
    private bool gpuCompositionPresentationRequested;
    private PendingGpuFrameState? pendingGpuFrameState;
    private int gpuFrameStateDispatchScheduled;
    private SceneTransform sceneTransform = SceneTransform.Default;
    private double sceneReferenceHeight = 1080;

    internal double? BlurRadius => blurRadius;

    internal FrameRateMode FrameRateMode => frameRateMode;

    internal bool UsesAnimationFrames => UsesAnimationFrameScheduler(frameRateMode, renderingBackendState);

    internal TimeSpan TimerInterval => frameTimer.Interval;

    internal Task FramePreparationTask =>
        Volatile.Read(ref framePreparationTask) ?? Task.CompletedTask;

    internal event Action<double?>? MainModelFrameRateChanged;

    internal event Action<ModelRenderFrame, PixelSize, ModelRasterTransform, double>?
        MainModelFrameStateChanged;

    internal event Action<double?>? WindowPresentationFrameRateChanged;

    internal event Action<SignalFrame>? CompositionFrameReady;

    internal Func<bool>? CompositionFrameReadbackRequested { get; set; }

    public ModelCanvas()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
        frameTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1d / 60d),
        };
        frameTimer.Tick += (_, _) =>
        {
            InvalidateChangedFrame(force: false);
        };
        SizeChanged += (_, _) =>
        {
            InvalidateChangedFrame(force: true);
        };
    }

    internal void Attach(ModelSelectionController value)
    {
        ArgumentNullException.ThrowIfNull(value);
        CancelFramePreparation("ControllerChanged", clearPending: true);
        if (controller is not null)
        {
            controller.ActiveChanged -= OnActiveChanged;
            controller.RenderingBackendStatusChanged -= OnRenderingBackendStatusChanged;
            controller.WorkPending -= OnRenderWorkPending;
        }

        controller = value;
        controller.ActiveChanged += OnActiveChanged;
        controller.RenderingBackendStatusChanged += OnRenderingBackendStatusChanged;
        controller.WorkPending += OnRenderWorkPending;
        renderingBackendState = controller.RenderingBackendStatus.State;
        PublishMainModelFrameRate(controller.Active);
        if (controller.Active?.Renderer is IModelFrameEffectTarget target)
        {
            target.SetBlurRadius(blurRadius);
        }
        if (VisualRoot is not null)
        {
            StartRefreshScheduler();
        }

        ResetPresentationFrameRate("Attach");
        InvalidateChangedFrame(force: true);
    }

    internal void SetLogger(ILogger<ModelCanvas>? value) =>
        logger = value ?? NullLogger<ModelCanvas>.Instance;

    internal void AttachMotionExpansionSource(ActiveModelMotionExpansionSource value)
    {
        ArgumentNullException.ThrowIfNull(value);
        motionExpansionSource = value;
    }

    internal void SetBlurRadius(double? radius)
    {
        if (radius is double value && (!double.IsFinite(value) || value < 0 || value > 40))
        {
            throw new ArgumentOutOfRangeException(nameof(radius));
        }

        if (Nullable.Equals(blurRadius, radius)) return;
        blurRadius = radius;
        if (controller?.Active?.Renderer is IModelFrameEffectTarget target)
        {
            target.SetBlurRadius(radius);
        }
        InvalidateVisual();
    }

    internal void SetSceneTransform(SceneTransform transform, double referenceHeight)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (!double.IsFinite(referenceHeight) || referenceHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(referenceHeight));
        }

        sceneTransform = transform;
        sceneReferenceHeight = referenceHeight;
        InvalidateChangedFrame(force: true);
    }

    internal void SetFrameRateMode(FrameRateMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        frameRateMode = mode;
        frameTimer.Interval = mode == FrameRateMode.FramesPerSecond30
            ? TimeSpan.FromSeconds(1d / 30d)
            : TimeSpan.FromSeconds(1d / 60d);
        StartRefreshScheduler();
        InvalidateChangedFrame(force: true);
    }

    internal void RefreshRenderingBackend()
    {
        void Refresh()
        {
            CancelFramePreparation("BackendPreferenceChanged", clearPending: true);
            InvalidateChangedFrame(force: true);
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Refresh();
            return;
        }

        Dispatcher.UIThread.Post(Refresh);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (controller?.HasPendingWork == true && Bounds.Width > 0 && Bounds.Height > 0)
        {
            if (gpuCompositionPresenter?.IsStarted == true)
            {
                gpuCompositionPresenter.RequestMaintenance();
            }
            else
            {
                context.Custom(controller.CreateMaintenanceOperation(new Rect(Bounds.Size)));
            }
        }

        ActiveModel? active = controller?.Active;
        ModelRenderFrame? frame = active?.Runtime.CurrentFrame;
        if (active is null || frame is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        if (gpuCompositionPresenter is not null
            && UsesGpuCompositionPresenter(
                renderingBackendState,
                active.Renderer is IModelGpuFrameRenderer))
        {
            return;
        }

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        PixelSize pixelSize = PixelSize.FromSize(Bounds.Size, scaling);
        ICustomDrawOperation operation = active.Renderer.CreateDrawOperation(
            frame,
            pixelSize,
            scaling,
            CreateRasterTransform(active.Id, pixelSize));
        context.Custom(renderingBackendState == ModelRenderingBackendState.Cpu
            ? new PresentationTrackingDrawOperation(operation, RecordCompletedPresentation)
            : operation);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        gpuCompositionPresenter = new GpuCompositionModelPresenter(this, logger);
        gpuCompositionPresenter.PresentationCompleted += OnGpuPresentationCompleted;
        gpuCompositionPresenter.CompositionFrameReady += OnCompositionFrameReady;
        gpuCompositionPresenter.ReadbackRequested = () => CompositionFrameReadbackRequested?.Invoke() == true;
        if (controller is not null)
        {
            StartRefreshScheduler();
            InvalidateChangedFrame(force: true);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ResetPresentationFrameRate("Detached");
        DisposeGpuCompositionPresenter();

        StopRefreshScheduler();
        CancelFramePreparation("Detached", clearPending: true);
        base.OnDetachedFromVisualTree(e);
    }

    public void Dispose()
    {
        ResetPresentationFrameRate("Disposed");
        DisposeGpuCompositionPresenter();
        GC.SuppressFinalize(this);
    }

    private void DisposeGpuCompositionPresenter()
    {
        ClearPendingGpuFrameState();
        GpuCompositionModelPresenter? presenter = gpuCompositionPresenter;
        gpuCompositionPresenter = null;
        gpuCompositionPresentationRequested = false;
        if (presenter is not null)
        {
            presenter.PresentationCompleted -= OnGpuPresentationCompleted;
            presenter.CompositionFrameReady -= OnCompositionFrameReady;
            _ = presenter.DisposeAsync().AsTask();
        }
    }

    private void StartRefreshScheduler()
    {
        int generation = ++schedulerGeneration;
        frameTimer.Stop();
        frameRefreshPacer.Reset();
        if (VisualRoot is null)
        {
            return;
        }

        if (renderingBackendState != ModelRenderingBackendState.Cpu)
        {
            return;
        }

        if (!UsesAnimationFrames)
        {
            frameTimer.Start();
            return;
        }

        RequestAnimationFrame(generation);
    }

    private void StopRefreshScheduler()
    {
        schedulerGeneration++;
        frameTimer.Stop();
    }

    private void RequestAnimationFrame(int generation)
    {
        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        topLevel?.RequestAnimationFrame(timestamp =>
        {
            if (generation != schedulerGeneration || VisualRoot is null || !UsesAnimationFrames)
            {
                return;
            }

            if (frameRefreshPacer.ShouldRefresh(frameRateMode, timestamp))
            {
                InvalidateChangedFrame(force: false);
            }

            RequestAnimationFrame(generation);
        });
    }

    private void OnActiveChanged(object? sender, EventArgs args)
    {
        void ApplyActive()
        {
            ClearPendingGpuFrameState();
            CancelFramePreparation("ActiveModelChanged", clearPending: true);
            ResetPresentationFrameRate("ActiveModelChanged");
            if (controller?.Active?.Renderer is IModelFrameEffectTarget target)
            {
                target.SetBlurRadius(blurRadius);
            }

            PublishMainModelFrameRate(controller?.Active);
            InvalidateChangedFrame(force: true);
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyActive();
            return;
        }

        Dispatcher.UIThread.Post(ApplyActive);
    }

    private void OnRenderWorkPending(object? sender, EventArgs args)
    {
        if (gpuCompositionPresenter?.IsStarted == true)
        {
            gpuCompositionPresenter.RequestMaintenance();
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            InvalidateVisual();
            return;
        }

        Dispatcher.UIThread.Post(InvalidateVisual);
    }

    private void OnRenderingBackendStatusChanged(
        object? sender,
        ModelRenderingBackendStatus status)
    {
        void Publish()
        {
            bool schedulerChanged = renderingBackendState != status.State;
            renderingBackendState = status.State;
            if (schedulerChanged)
            {
                ClearPendingGpuFrameState();
                CancelFramePreparation("BackendStateChanged", clearPending: true);
                StartRefreshScheduler();
                ResetPresentationFrameRate("BackendStateChanged");
            }

            if (status.State is ModelRenderingBackendState.SwitchingToGpu
                or ModelRenderingBackendState.SwitchingToCpu)
            {
                if (status.State == ModelRenderingBackendState.SwitchingToCpu)
                {
                    ClearGpuCompositionPresentation();
                    InvalidateChangedFrame(force: true);
                }

                PublishMainModelFrameRate(null);
                return;
            }

            PublishMainModelFrameRate(controller?.Active);
            InvalidateChangedFrame(force: true);
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Publish();
            return;
        }

        Dispatcher.UIThread.Post(Publish);
    }

    internal static bool UsesAnimationFrameScheduler(
        FrameRateMode mode,
        ModelRenderingBackendState backend) => backend == ModelRenderingBackendState.Cpu
            && mode is FrameRateMode.VSync or FrameRateMode.VSyncHalf;

    internal static bool UsesGpuCompositionPresenter(
        ModelRenderingBackendState backend,
        bool rendererSupportsComposition) => rendererSupportsComposition
            && backend != ModelRenderingBackendState.Cpu;

    internal static bool ShouldPrepareCpuFrame(ModelRenderingBackendState backend) =>
        backend == ModelRenderingBackendState.Cpu;

    private ModelRasterTransform CreateRasterTransform(ModelId modelId, PixelSize pixelSize)
    {
        renderStateSource.PublishScene(sceneTransform, sceneReferenceHeight, pixelSize);
        return ReadLatestRasterTransform(modelId);
    }

    private ModelRasterTransform ReadLatestRasterTransform(ModelId modelId)
    {
        if (motionExpansionSource?.TryGet(modelId, out ModelMotionExpansionSnapshot snapshot) == true)
        {
            renderStateSource.PublishMotion(snapshot);
        }
        else
        {
            renderStateSource.PublishMotion(new ModelMotionExpansionSnapshot(modelId, 0, 0, 0));
        }

        return renderStateSource.TryGetRasterTransform(modelId, out ModelRasterTransform transform)
            ? transform
            : ModelRasterTransform.Identity;
    }

    private void InvalidateChangedFrame(bool force)
    {
        ActiveModel? active = controller?.Active;
        ModelRenderFrame? frame = active?.Runtime.CurrentFrame;
        if (active is null || frame is null)
        {
            ClearGpuCompositionPresentation();
            PublishMainModelFrameRate(null);
            lastInvalidatedModel = null;
            lastInvalidatedRevision = -1;
            if (force)
            {
                InvalidateVisual();
            }

            return;
        }

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            ClearGpuCompositionPresentation();
            lastInvalidatedPixelSize = default;
            if (force)
            {
                InvalidateVisual();
            }

            return;
        }

        double renderingScale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        PixelSize pixelSize = PixelSize.FromSize(Bounds.Size, renderingScale);
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
        {
            ClearGpuCompositionPresentation();
            lastInvalidatedPixelSize = default;
            return;
        }
        ModelRasterTransform rasterTransform = CreateRasterTransform(active.Id, pixelSize);
        if (!force
            && ReferenceEquals(active, lastInvalidatedModel)
            && frame.Revision == lastInvalidatedRevision
            && pixelSize == lastInvalidatedPixelSize
            && rasterTransform == lastInvalidatedRasterTransform)
        {
            return;
        }

        lastInvalidatedModel = active;
        lastInvalidatedRevision = frame.Revision;
        lastInvalidatedPixelSize = pixelSize;
        lastInvalidatedRasterTransform = rasterTransform;
        MainModelFrameStateChanged?.Invoke(
            frame,
            pixelSize,
            rasterTransform,
            sceneReferenceHeight);
        if (gpuCompositionPresenter is not null
            && active.Renderer is IModelGpuFrameRenderer gpuRenderer
            && UsesGpuCompositionPresenter(renderingBackendState, rendererSupportsComposition: true))
        {
            bool present = renderingBackendState is ModelRenderingBackendState.SwitchingToGpu
                or ModelRenderingBackendState.Gpu;
            gpuCompositionPresenter.Publish(new GpuCompositionFrameRequest(
                gpuRenderer,
                () => active.Runtime.CurrentFrame,
                pixelSize,
                renderingScale,
                frameRateMode,
                present,
                () => ReadLatestRasterTransform(active.Id),
                (observedFrame, observedPixelSize, observedTransform) =>
                    ObserveGpuFrameState(
                        active,
                        observedFrame,
                        observedPixelSize,
                        observedTransform)));
            gpuCompositionPresentationRequested = present;
        }
        else
        {
            ClearGpuCompositionPresentation();
        }

        if (ShouldPrepareCpuFrame(renderingBackendState)
            && active.Renderer is IModelFramePreparationTarget { RequiresFramePreparation: true })
        {
            QueueFramePreparation(active, frame, pixelSize, rasterTransform);
        }

        InvalidateVisual();
    }

    private void ClearGpuCompositionPresentation()
    {
        if (!gpuCompositionPresentationRequested)
        {
            return;
        }

        gpuCompositionPresentationRequested = false;
        gpuCompositionPresenter?.Clear();
    }

    private void OnGpuPresentationCompleted(object? sender, EventArgs args) =>
        RecordCompletedPresentation();

    private void ObserveGpuFrameState(
        ActiveModel active,
        ModelRenderFrame frame,
        PixelSize pixelSize,
        ModelRasterTransform rasterTransform)
    {
        Interlocked.Exchange(
            ref pendingGpuFrameState,
            new PendingGpuFrameState(active, frame, pixelSize, rasterTransform));
        if (Interlocked.Exchange(ref gpuFrameStateDispatchScheduled, 1) == 0)
        {
            Dispatcher.UIThread.Post(DrainGpuFrameState, DispatcherPriority.Render);
        }
    }

    private void DrainGpuFrameState()
    {
        PendingGpuFrameState? state = Interlocked.Exchange(ref pendingGpuFrameState, null);
        Interlocked.Exchange(ref gpuFrameStateDispatchScheduled, 0);
        if (state is not null
            && renderingBackendState != ModelRenderingBackendState.Cpu
            && ReferenceEquals(controller?.Active, state.Active))
        {
            MainModelFrameStateChanged?.Invoke(
                state.Frame,
                state.PixelSize,
                state.RasterTransform,
                sceneReferenceHeight);
        }

        if (Volatile.Read(ref pendingGpuFrameState) is not null
            && Interlocked.Exchange(ref gpuFrameStateDispatchScheduled, 1) == 0)
        {
            Dispatcher.UIThread.Post(DrainGpuFrameState, DispatcherPriority.Render);
        }
    }

    private void ClearPendingGpuFrameState()
    {
        Interlocked.Exchange(ref pendingGpuFrameState, null);
        Interlocked.Exchange(ref gpuFrameStateDispatchScheduled, 0);
    }

    private void OnCompositionFrameReady(SignalFrame frame) =>
        CompositionFrameReady?.Invoke(frame);

    private void RecordCompletedPresentation()
    {
        double? next = presentationFrameRateSampler.RecordCompletedFrame();
        if (next is null)
        {
            return;
        }

        void Publish()
        {
            WindowPresentationFrameRateChanged?.Invoke(next);
            ModelCanvasLog.PresentationFrameRatePublished(logger, next.Value);
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Publish();
        }
        else
        {
            Dispatcher.UIThread.Post(Publish);
        }
    }

    private void ResetPresentationFrameRate(string reason)
    {
        presentationFrameRateSampler.Reset();
        void Publish() => WindowPresentationFrameRateChanged?.Invoke(null);
        if (Dispatcher.UIThread.CheckAccess())
        {
            Publish();
        }
        else
        {
            Dispatcher.UIThread.Post(Publish);
        }

        ModelCanvasLog.PresentationFrameRateReset(logger, reason);
    }

    private void QueueFramePreparation(
        ActiveModel active,
        ModelRenderFrame frame,
        PixelSize pixelSize,
        ModelRasterTransform rasterTransform)
    {
        pendingPreparationModel = active;
        pendingPreparationFrame = frame;
        pendingPreparationPixelSize = pixelSize;
        pendingPreparationRasterTransform = rasterTransform;
        if (framePreparationTask is not null && !framePreparationTask.IsCompleted)
        {
            return;
        }

        StartPendingFramePreparation();
    }

    private void StartPendingFramePreparation()
    {
        ActiveModel active = pendingPreparationModel!;
        ModelRenderFrame frame = pendingPreparationFrame!;
        PixelSize pixelSize = pendingPreparationPixelSize;
        ModelRasterTransform rasterTransform = pendingPreparationRasterTransform;
        pendingPreparationModel = null;
        pendingPreparationFrame = null;
        long generation = framePreparationGeneration;
        var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        framePreparationCancellation = cancellation;
        framePreparationTask = completion.Task;
        _ = PrepareFrameAndInvalidateAsync(
            active,
            frame,
            pixelSize,
            rasterTransform,
            generation,
            cancellation,
            completion);
    }

    private async Task PrepareFrameAndInvalidateAsync(
        ActiveModel active,
        ModelRenderFrame frame,
        PixelSize pixelSize,
        ModelRasterTransform rasterTransform,
        long generation,
        CancellationTokenSource cancellation,
        TaskCompletionSource completion)
    {
        bool completed = false;
        try
        {
            await active.Renderer.PrepareFrameAsync(
                frame,
                pixelSize,
                rasterTransform,
                cancellation.Token).ConfigureAwait(false);
            completed = true;
            Interlocked.Exchange(ref framePreparationFailureLogged, 0);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            LogFramePreparationFailure(active, frame, exception);
        }
        finally
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => CompleteFramePreparation(
                    active,
                    generation,
                    cancellation,
                    completed));
            }
            catch (Exception exception)
            {
                LogFramePreparationFailure(active, frame, exception);
            }
            finally
            {
                completion.TrySetResult();
            }
        }
    }

    private void LogFramePreparationFailure(
        ActiveModel active,
        ModelRenderFrame frame,
        Exception exception)
    {
        if (Interlocked.Exchange(ref framePreparationFailureLogged, 1) != 0)
        {
            return;
        }

        ModelCanvasLog.FramePreparationFailed(
            logger,
            exception,
            active.Id.Value,
            frame.Revision,
            exception.GetType().Name);
    }

    private void CompleteFramePreparation(
        ActiveModel active,
        long generation,
        CancellationTokenSource cancellation,
        bool completed)
    {
        if (!ReferenceEquals(framePreparationCancellation, cancellation))
        {
            cancellation.Dispose();
            return;
        }

        framePreparationCancellation = null;
        framePreparationTask = null;
        if (completed
            && generation == framePreparationGeneration
            && ReferenceEquals(controller?.Active, active))
        {
            PublishMainModelFrameRate(active);
            InvalidateVisual();
        }

        cancellation.Dispose();
        if (pendingPreparationFrame is not null)
        {
            StartPendingFramePreparation();
        }
    }

    private void CancelFramePreparation(string reason, bool clearPending)
    {
        long generation = ++framePreparationGeneration;
        if (clearPending)
        {
            pendingPreparationModel = null;
            pendingPreparationFrame = null;
            pendingPreparationPixelSize = default;
            pendingPreparationRasterTransform = default;
        }

        CancellationTokenSource? cancellation = framePreparationCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        cancellation.Cancel();
        ModelCanvasLog.FramePreparationCancelled(logger, generation, reason);
    }

    private void PublishMainModelFrameRate(ActiveModel? active)
    {
        ModelRenderingBackendStatus? status = active?.Renderer is IModelRenderingBackendTarget target
            ? target.RenderingBackendStatus
            : null;
        double? next = status is { State: ModelRenderingBackendState.Cpu or ModelRenderingBackendState.Gpu }
            ? status.FramesPerSecond
            : status is null && active?.Renderer is IModelFramePreparationTarget preparationTarget
                ? preparationTarget.FullFrameCacheFramesPerSecond
                : null;
        if (ReferenceEquals(reportedFrameRateModel, active)
            && reportedFrameRate == next)
        {
            return;
        }

        reportedFrameRateModel = active;
        reportedFrameRate = next;
        MainModelFrameRateChanged?.Invoke(next);
    }

}

internal static partial class ModelCanvasLog
{
    [LoggerMessage(
        6290,
        LogLevel.Warning,
        "Model frame preparation failed for {ModelId} revision {Revision} with {ExceptionType}")]
    internal static partial void FramePreparationFailed(
        ILogger logger,
        Exception exception,
        string modelId,
        long revision,
        string exceptionType);

    [LoggerMessage(
        6291,
        LogLevel.Debug,
        "Model frame preparation generation {Generation} cancelled because {Reason}")]
    internal static partial void FramePreparationCancelled(
        ILogger logger,
        long generation,
        string reason);

    [LoggerMessage(
        6292,
        LogLevel.Debug,
        "Window presentation frame rate published at {FramesPerSecond} FPS")]
    internal static partial void PresentationFrameRatePublished(
        ILogger logger,
        double framesPerSecond);

    [LoggerMessage(
        6293,
        LogLevel.Debug,
        "Window presentation frame rate sampler reset because {Reason}")]
    internal static partial void PresentationFrameRateReset(
        ILogger logger,
        string reason);
}
