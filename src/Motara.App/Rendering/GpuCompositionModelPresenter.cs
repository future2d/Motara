using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Motara.App.Models;
using Motara.Media;
using Motara.ModelRuntime.Abstractions;
using Motara.Persistence;
using SkiaSharp;

namespace Motara.App.Rendering;

internal sealed record GpuCompositionFrameRequest(
    IModelGpuFrameRenderer Renderer,
    Func<ModelRenderFrame?> FrameProvider,
    PixelSize PixelSize,
    double RenderingScale,
    FrameRateMode FrameRateMode,
    bool Present,
    Func<ModelRasterTransform> TransformProvider,
    Action<ModelRenderFrame, PixelSize, ModelRasterTransform>? FrameStateObserver = null);

internal sealed class GpuCompositionModelPresenter : IAsyncDisposable
{
    internal static GRSurfaceOrigin SharedTextureSurfaceOrigin =>
        GRSurfaceOrigin.BottomLeft;

    private sealed class CompositionBindings(
        IOpenGlTextureSharingRenderInterfaceContextFeature textureSharing,
        ICompositionGpuInterop gpuInterop,
        CompositionDrawingSurface drawingSurface,
        CompositionSurfaceVisual visual)
    {
        internal IOpenGlTextureSharingRenderInterfaceContextFeature TextureSharing { get; } =
            textureSharing;

        internal ICompositionGpuInterop GpuInterop { get; } = gpuInterop;

        internal CompositionDrawingSurface DrawingSurface { get; } = drawingSurface;

        internal CompositionSurfaceVisual Visual { get; } = visual;
    }

    private sealed class WorkerOutput(
        PixelSize size,
        ICompositionImportableOpenGlSharedTexture sharedTexture,
        GRBackendTexture backendTexture,
        SKSurface surface,
        ICompositionImportedGpuImage importedImage)
    {
        internal PixelSize Size { get; } = size;

        internal ICompositionImportableOpenGlSharedTexture SharedTexture { get; } = sharedTexture;

        internal GRBackendTexture BackendTexture { get; } = backendTexture;

        internal SKSurface Surface { get; } = surface;

        internal ICompositionImportedGpuImage ImportedImage { get; } = importedImage;

        internal long Generation { get; set; }

        internal long PresentationEpoch { get; set; }

        internal Task<PresentationTiming>? PresentationTask { get; set; }

        internal PendingFrameTiming? PendingTiming { get; set; }

        internal IGpuCompletionFence? CompletionFence { get; set; }
    }

    private sealed record PendingFrameTiming(
        long FrameStartedAt,
        double RenderCommandMs,
        double FlushMs);

    private readonly record struct PresentationTiming(
        double DurationMs,
        long CompletedAt,
        bool Presented);

    private sealed class WorkerResources : IDisposable
    {
        internal WorkerResources(
            IGlContext context,
            GRGlInterface glInterface,
            GRContext grContext,
            OpenGlGpuCompletionFenceFactory? fenceFactory)
        {
            Context = context;
            GlInterface = glInterface;
            GrContext = grContext;
            FenceFactory = fenceFactory;
        }

        internal IGlContext Context { get; }

        internal GRGlInterface GlInterface { get; }

        internal GRContext GrContext { get; }

        internal OpenGlGpuCompletionFenceFactory? FenceFactory { get; }

        internal List<WorkerOutput> Outputs { get; } = [];

        internal GpuCompositionOutputBufferCoordinator OutputBuffers { get; set; } =
            new(OutputBufferCount);

        public void Dispose()
        {
            GrContext.Dispose();
            GlInterface.Dispose();
            Context.Dispose();
        }
    }

    private sealed class GpuCompositionUnavailableException(
        GpuCompositionProbeSupport support)
        : Exception($"GPU composition is unavailable because {support}.")
    {
        internal GpuCompositionProbeSupport Support { get; } = support;
    }

    // Keep one buffer available for rendering while another is being presented and
    // a third is waiting for the compositor. This prevents compositor jitter from
    // turning directly into dropped render opportunities.
    private const int OutputBufferCount = 3;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private readonly Visual host;
    private readonly ILogger logger;
    private readonly GpuCompositionFrameMailbox<GpuCompositionFrameRequest> mailbox = new();
    private readonly RenderPipelineMetrics pipelineMetrics = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private Task workerTask = Task.CompletedTask;
    private int started;
    private int disposed;
    private long readbackSequence;

    internal bool IsStarted => Volatile.Read(ref started) != 0;

    internal event EventHandler? PresentationCompleted;

    internal event Action<SignalFrame>? CompositionFrameReady;

    internal Func<bool>? ReadbackRequested { get; set; }

    internal GpuCompositionModelPresenter(Visual host, ILogger logger)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return;
        }

        GpuCompositionPresenterLog.WorkerStarted(logger);
        workerTask = Task.Run(() => RunWorkerAsync(lifetimeCancellation.Token));
    }

    internal long Publish(GpuCompositionFrameRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Start();
        GpuCompositionFrameRequest? current = mailbox.ReadCurrent().Value;
        bool invalidatePresentation = !CanContinuePresentation(current, request);
        long generation = mailbox.Publish(request, invalidatePresentation);
        if (invalidatePresentation)
        {
            long presentationEpoch = mailbox.ReadCurrent().PresentationEpoch;
            GpuCompositionPresenterLog.PresentationInvalidated(
                logger,
                generation,
                presentationEpoch);
            HideSurface(presentationEpoch);
        }

        return generation;
    }

    internal long Clear()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return -1;
        }

        Start();
        long generation = mailbox.Clear();
        HideSurface(mailbox.ReadCurrent().PresentationEpoch);
        return generation;
    }

    internal void RequestMaintenance()
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            mailbox.Wake();
        }
    }

    private static bool CanContinuePresentation(
        GpuCompositionFrameRequest? current,
        GpuCompositionFrameRequest next) =>
        current is { Present: true }
        && next.Present
        && ReferenceEquals(current.Renderer, next.Renderer)
        && current.PixelSize == next.PixelSize
        && current.RenderingScale.Equals(next.RenderingScale);

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        CompositionBindings? bindings = null;
        WorkerResources? resources = null;
        IModelGpuFrameRenderer? maintenanceRenderer = null;
        long metricsStartedAt = Stopwatch.GetTimestamp();
        long processedCount = 0;
        long supersededCount = 0;
        long bufferStarvedCount = 0;
        long lastGeneration = 0;
        var timingWindow = new GpuCompositionTimingWindow();
        var framePacer = new GpuCompositionFramePacer();
        try
        {
            GpuCompositionFrameSnapshot<GpuCompositionFrameRequest> snapshot =
                await mailbox.ReadLatestAsync(cancellationToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(GpuCompositionFramePacer.TickInterval);
            while (true)
            {
                if (lastGeneration > 0 && snapshot.Generation > lastGeneration + 1)
                {
                    supersededCount += snapshot.Generation - lastGeneration - 1;
                    pipelineMetrics.RecordSuperseded(snapshot.Generation - lastGeneration - 1);
                }

                lastGeneration = snapshot.Generation;
                GpuCompositionFrameRequest? request = snapshot.Value;
                try
                {
                    if (resources is not null)
                    {
                        CompletePendingFences(resources);
                        await ReapCompletedPresentationTasksAsync(
                            resources,
                            timingWindow).ConfigureAwait(false);
                    }

                    if (request is null)
                    {
                        if (resources is not null)
                        {
                            resources.OutputBuffers.DropReadyBuffers();
                            if (maintenanceRenderer is not null)
                            {
                                ReclaimGpuResources(resources, maintenanceRenderer);
                            }
                        }
                    }
                    else
                    {
                        maintenanceRenderer = request.Renderer;
                        resources?.OutputBuffers.DropInvalidatedReadyBuffers(
                            snapshot.PresentationEpoch);
                        if (!request.Present)
                        {
                            resources?.OutputBuffers.DropReadyBuffers();
                        }
                        else
                        {
                            StartReadyPresentation(
                                resources,
                                bindings,
                                snapshot.PresentationEpoch,
                                request);
                            if (framePacer.ShouldRender(request.FrameRateMode))
                            {
                                long frameStartedAt = Stopwatch.GetTimestamp();
                                ModelRenderFrame? frame = request.FrameProvider();
                                if (frame is not null)
                                {
                                    bindings ??= await CreateBindingsAsync(cancellationToken)
                                        .ConfigureAwait(false);
                                    resources ??= CreateWorkerResources(bindings.TextureSharing);
                                    await EnsureOutputAsync(
                                        resources,
                                        bindings,
                                        request.PixelSize,
                                        cancellationToken).ConfigureAwait(false);

                                    int outputIndex = resources.OutputBuffers.TryAcquireRenderBuffer();
                                    if (outputIndex < 0)
                                    {
                                        bufferStarvedCount++;
                                    }
                                    else
                                    {
                                        WorkerOutput output = resources.Outputs[outputIndex];
                                        ModelRasterTransform rasterTransform =
                                            request.TransformProvider();
                                        bool rendered = RenderFrame(
                                            resources,
                                            output,
                                            request,
                                            frame,
                                            rasterTransform,
                                            out double renderCommandMs,
                                            out double flushMs,
                                            out IGpuCompletionFence? completionFence);
                                        processedCount++;
                                        if (rendered)
                                        {
                                            request.FrameStateObserver?.Invoke(
                                                frame,
                                                request.PixelSize,
                                                rasterTransform);
                                            pipelineMetrics.RecordProduced();
                                            output.Generation = snapshot.Generation;
                                            output.PresentationEpoch = snapshot.PresentationEpoch;
                                            output.CompletionFence = completionFence;
                                            output.PendingTiming = new PendingFrameTiming(
                                                frameStartedAt,
                                                renderCommandMs,
                                                flushMs);
                                            if (completionFence is null)
                                            {
                                                resources.OutputBuffers.MarkReady(
                                                    outputIndex,
                                                    snapshot.Generation,
                                                    snapshot.PresentationEpoch);
                                            }
                                        }
                                        else
                                        {
                                            completionFence?.Dispose();
                                            resources.OutputBuffers.ReleaseRenderBuffer(outputIndex);
                                        }
                                    }

                                    StartReadyPresentation(
                                        resources,
                                        bindings,
                                        snapshot.PresentationEpoch,
                                        request);
                                }
                            }
                        }

                        if (resources is not null)
                        {
                            ReclaimGpuResources(resources, maintenanceRenderer);
                        }
                    }
                }
                catch (GpuCompositionUnavailableException exception)
                {
                    GpuCompositionPresenterLog.Unavailable(logger, exception.Support);
                    request?.Renderer.ReportGpuCompositionFailure(
                        ModelRenderingBackendFaultReason.GpuUnavailable);
                    await HideSurfaceAsync(snapshot.PresentationEpoch).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ModelRenderingBackendFaultReason reason =
                        resources?.Context.IsLost == true
                            || resources?.GrContext.IsAbandoned == true
                            || bindings?.GpuInterop.IsLost == true
                            ? ModelRenderingBackendFaultReason.GpuContextLost
                            : ModelRenderingBackendFaultReason.GpuRenderingFailed;
                    GpuCompositionPresenterLog.WorkerFailed(
                        logger,
                        exception,
                        reason,
                        exception.GetType().Name);
                    request?.Renderer.ReportGpuCompositionFailure(reason);
                    await HideSurfaceAsync(snapshot.PresentationEpoch).ConfigureAwait(false);
                    await DisposeWorkerResourcesAsync(resources).ConfigureAwait(false);
                    resources = null;
                    await DisposeBindingsAsync(bindings).ConfigureAwait(false);
                    bindings = null;
                    request?.Renderer.ReclaimReleasedGpuResources(activeContext: null);
                }

                LogMetricsIfDue(
                    ref metricsStartedAt,
                    ref processedCount,
                    ref supersededCount,
                    ref bufferStarvedCount,
                    timingWindow);

                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                snapshot = mailbox.ReadCurrent();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (resources is not null && maintenanceRenderer is not null)
            {
                try
                {
                    ReclaimGpuResources(resources, maintenanceRenderer);
                }
                catch (Exception exception)
                {
                    GpuCompositionPresenterLog.WorkerCleanupFailed(
                        logger,
                        exception,
                        exception.GetType().Name);
                }
            }

            await DisposeWorkerResourcesAsync(resources).ConfigureAwait(false);
            await DisposeBindingsAsync(bindings).ConfigureAwait(false);
            GpuCompositionPresenterLog.WorkerStopped(logger);
        }
    }

    private void StartReadyPresentation(
        WorkerResources? resources,
        CompositionBindings? bindings,
        long currentPresentationEpoch,
        GpuCompositionFrameRequest request)
    {
        if (resources is null
            || bindings is null
            || !request.Present
            || !mailbox.IsCurrentPresentation(currentPresentationEpoch)
            || !resources.OutputBuffers.TryTakeLatestReady(
                currentPresentationEpoch,
                out int outputIndex))
        {
            return;
        }

        WorkerOutput output = resources.Outputs[outputIndex];
        output.PresentationTask = PresentAsync(
            bindings,
            output,
            output.PresentationEpoch,
            lifetimeCancellation.Token);
    }

    private async Task ReapCompletedPresentationTasksAsync(
        WorkerResources resources,
        GpuCompositionTimingWindow timingWindow)
    {
        for (int index = 0; index < resources.Outputs.Count; index++)
        {
            WorkerOutput output = resources.Outputs[index];
            Task<PresentationTiming>? presentationTask = output.PresentationTask;
            if (presentationTask is null || !presentationTask.IsCompleted)
            {
                continue;
            }

            try
            {
                PresentationTiming presentation = await presentationTask.ConfigureAwait(false);
                if (presentation.Presented)
                {
                    PublishReadbackIfSubscribed(resources, output);
                    resources.OutputBuffers.MarkPresented(index);
                    if (output.PendingTiming is { } timing)
                    {
                        timingWindow.Add(
                            timing.RenderCommandMs,
                            timing.FlushMs,
                            presentation.DurationMs,
                            Stopwatch.GetElapsedTime(
                                timing.FrameStartedAt,
                                presentation.CompletedAt).TotalMilliseconds);
                        pipelineMetrics.RecordPresented();
                        pipelineMetrics.RecordRenderDuration(timing.RenderCommandMs);
                        pipelineMetrics.RecordPresentationDuration(presentation.DurationMs);
                    }

                    PresentationCompleted?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    resources.OutputBuffers.MarkPresentationFailed(index);
                }
            }
            catch
            {
                resources.OutputBuffers.MarkPresentationFailed(index);
                throw;
            }
            finally
            {
                output.PresentationTask = null;
                output.PendingTiming = null;
            }
        }
    }

    private void PublishReadbackIfSubscribed(WorkerResources resources, WorkerOutput output)
    {
        if (CompositionFrameReady is null || ReadbackRequested?.Invoke() != true)
        {
            return;
        }

        try
        {
            using (resources.Context.EnsureCurrent())
            {
                int width = output.Size.Width;
                int height = output.Size.Height;
                byte[] pixels = new byte[checked(width * height * 4)];
                using SKImage image = output.Surface.Snapshot();
                var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                GCHandle pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    if (!image.ReadPixels(info, pinned.AddrOfPinnedObject(), width * 4, 0, 0))
                    {
                        return;
                    }
                }
                finally
                {
                    pinned.Free();
                }

                using SignalFrame frame = SignalFrame.CopyFrom(
                    width,
                    height,
                    SignalPixelFormat.Bgra8888,
                    pixels,
                    Interlocked.Increment(ref readbackSequence),
                    TimeSpan.Zero,
                    hasAlpha: true);
                CompositionFrameReady?.Invoke(frame);
            }
        }
        catch (Exception exception)
        {
            GpuCompositionPresenterLog.ReadbackFailed(logger, exception.GetType().Name);
        }
    }

    private async Task<CompositionBindings> CreateBindingsAsync(
        CancellationToken cancellationToken)
    {
        long startedAt = Stopwatch.GetTimestamp();
        CompositionBindings bindings = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompositionVisual? elementVisual = ElementComposition.GetElementVisual(host);
            if (elementVisual is null)
            {
                throw new GpuCompositionUnavailableException(
                    GpuCompositionProbeSupport.CompositionInteropUnavailable);
            }

            Compositor compositor = elementVisual.Compositor;
            object? sharingFeature = await compositor.TryGetRenderInterfaceFeature(
                typeof(IOpenGlTextureSharingRenderInterfaceContextFeature));
            var textureSharing =
                sharingFeature as IOpenGlTextureSharingRenderInterfaceContextFeature;
            ICompositionGpuInterop? gpuInterop = await compositor.TryGetCompositionGpuInterop();
            GpuCompositionProbeSupport support = GpuCompositionInteropProbe.EvaluateSupport(
                textureSharing is not null,
                textureSharing?.CanCreateSharedContext == true,
                gpuInterop is not null);
            if (support != GpuCompositionProbeSupport.Supported)
            {
                throw new GpuCompositionUnavailableException(support);
            }

            CompositionDrawingSurface drawingSurface = compositor.CreateDrawingSurface();
            CompositionSurfaceVisual visual = compositor.CreateSurfaceVisual();
            visual.Size = new Vector(host.Bounds.Width, host.Bounds.Height);
            visual.Surface = drawingSurface;
            return new CompositionBindings(
                textureSharing!,
                gpuInterop!,
                drawingSurface,
                visual);
        }, DispatcherPriority.Render);
        GpuCompositionPresenterLog.Initialized(
            logger,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        return bindings;
    }

    private WorkerResources CreateWorkerResources(
        IOpenGlTextureSharingRenderInterfaceContextFeature textureSharing)
    {
        IGlContext context = textureSharing.CreateSharedContext()
            ?? throw new InvalidOperationException("A shared OpenGL context could not be created.");
        GRGlInterface? glInterface = null;
        GRContext? grContext = null;
        OpenGlGpuCompletionFenceFactory? fenceFactory = null;
        try
        {
            using (context.EnsureCurrent())
            {
                glInterface = CreateGlInterface(context);
                grContext = GRContext.CreateGl(glInterface)
                    ?? throw new InvalidOperationException(
                        "A shared Skia GPU context could not be created.");
                fenceFactory = OpenGlGpuCompletionFenceFactory.TryCreate(context.GlInterface);
            }

            GpuCompositionPresenterLog.FenceModeSelected(
                logger,
                fenceFactory is not null);
            return new WorkerResources(context, glInterface, grContext, fenceFactory);
        }
        catch
        {
            grContext?.Dispose();
            glInterface?.Dispose();
            context.Dispose();
            throw;
        }
    }

    private async Task EnsureOutputAsync(
        WorkerResources resources,
        CompositionBindings bindings,
        PixelSize size,
        CancellationToken cancellationToken)
    {
        if (resources.Outputs.Count == OutputBufferCount
            && resources.Outputs.All(output => output.Size == size))
        {
            return;
        }

        await DisposeOutputsAsync(resources, throwOnPresentationFailure: true)
            .ConfigureAwait(false);
        try
        {
            for (int index = 0; index < OutputBufferCount; index++)
            {
                resources.Outputs.Add(
                    await CreateOutputAsync(
                        resources,
                        bindings,
                        size,
                        cancellationToken).ConfigureAwait(false));
            }
        }
        catch
        {
            await DisposeOutputsAsync(resources, throwOnPresentationFailure: false)
                .ConfigureAwait(false);
            throw;
        }

        resources.OutputBuffers = new GpuCompositionOutputBufferCoordinator(OutputBufferCount);
    }

    private async Task<WorkerOutput> CreateOutputAsync(
        WorkerResources resources,
        CompositionBindings bindings,
        PixelSize size,
        CancellationToken cancellationToken)
    {
        ICompositionImportableOpenGlSharedTexture? sharedTexture = null;
        GRBackendTexture? backendTexture = null;
        SKSurface? surface = null;
        ICompositionImportedGpuImage? importedImage = null;
        try
        {
            using (resources.Context.EnsureCurrent())
            {
                sharedTexture = bindings.TextureSharing.CreateSharedTextureForComposition(
                    resources.Context,
                    size);
                backendTexture = new GRBackendTexture(
                    size.Width,
                    size.Height,
                    mipmapped: false,
                    new GRGlTextureInfo(
                        target: 0x0DE1,
                        id: (uint)sharedTexture.TextureId,
                        format: (uint)sharedTexture.InternalFormat));
                surface = SKSurface.Create(
                    resources.GrContext,
                    backendTexture,
                    SharedTextureSurfaceOrigin,
                    SKColorType.Rgba8888)
                    ?? throw new InvalidOperationException(
                        "The shared Skia output surface could not be created.");
            }

            importedImage = await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ICompositionImportedGpuImage image = bindings.GpuInterop.ImportImage(sharedTexture);
                await image.ImportCompleted.WaitAsync(cancellationToken);
                return image;
            }, DispatcherPriority.Render);
            GpuCompositionPresenterLog.OutputCreated(logger, size.Width, size.Height);
            return new WorkerOutput(
                size,
                sharedTexture,
                backendTexture,
                surface,
                importedImage);
        }
        catch
        {
            if (importedImage is not null)
            {
                await DisposeImportedImageAsync(importedImage).ConfigureAwait(false);
            }

            using (resources.Context.EnsureCurrent())
            {
                surface?.Dispose();
                backendTexture?.Dispose();
                sharedTexture?.Dispose();
            }

            throw;
        }
    }

    private static bool RenderFrame(
        WorkerResources resources,
        WorkerOutput output,
        GpuCompositionFrameRequest request,
        ModelRenderFrame frame,
        ModelRasterTransform rasterTransform,
        out double renderCommandMs,
        out double flushMs,
        out IGpuCompletionFence? completionFence)
    {
        renderCommandMs = 0;
        flushMs = 0;
        completionFence = null;
        using (resources.Context.EnsureCurrent())
        {
            request.Renderer.ReclaimReleasedGpuResources(resources.GrContext);
            output.Surface.Canvas.Clear(SKColors.Transparent);
            long renderStartedAt = Stopwatch.GetTimestamp();
            bool rendered = request.Renderer.TryRenderGpuFrame(
                resources.GrContext,
                output.Surface.Canvas,
                frame,
                request.PixelSize,
                request.RenderingScale,
                rasterTransform);
            renderCommandMs = Stopwatch.GetElapsedTime(renderStartedAt).TotalMilliseconds;
            long flushStartedAt = Stopwatch.GetTimestamp();
            output.Surface.Canvas.Flush();
            completionFence = GpuCompositionFrameSynchronizer.Submit(
                (submit, synchronous) => resources.GrContext.Flush(submit, synchronous),
                () => resources.FenceFactory?.CreateFence(),
                resources.Context.GlInterface.Flush);
            flushMs = Stopwatch.GetElapsedTime(flushStartedAt).TotalMilliseconds;
            return rendered;
        }
    }

    private void CompletePendingFences(WorkerResources resources)
    {
        using (resources.Context.EnsureCurrent())
        {
            for (int index = 0; index < resources.Outputs.Count; index++)
            {
                WorkerOutput output = resources.Outputs[index];
                IGpuCompletionFence? fence = output.CompletionFence;
                if (fence is null || !fence.IsSignaled)
                {
                    continue;
                }

                output.CompletionFence = null;
                fence.Dispose();
                if (mailbox.IsCurrentPresentation(output.PresentationEpoch))
                {
                    resources.OutputBuffers.MarkReady(
                        index,
                        output.Generation,
                        output.PresentationEpoch);
                }
                else
                {
                    resources.OutputBuffers.ReleaseRenderBuffer(index);
                    output.PendingTiming = null;
                }
            }
        }
    }

    private async Task<PresentationTiming> PresentAsync(
        CompositionBindings bindings,
        WorkerOutput output,
        long presentationEpoch,
        CancellationToken cancellationToken)
    {
        long startedAt = Stopwatch.GetTimestamp();
        bool presented = false;
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!mailbox.IsCurrentPresentation(presentationEpoch))
            {
                return;
            }

            await bindings.DrawingSurface.UpdateAsync(output.ImportedImage);
            if (!mailbox.IsCurrentPresentation(presentationEpoch))
            {
                return;
            }

            bindings.Visual.Size = new Vector(host.Bounds.Width, host.Bounds.Height);
            bindings.Visual.Surface = bindings.DrawingSurface;
            ElementComposition.SetElementChildVisual(host, bindings.Visual);
            presented = true;
        }, DispatcherPriority.Render);
        return new PresentationTiming(
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            Stopwatch.GetTimestamp(),
            presented);
    }

    private void HideSurface(long presentationEpoch)
    {
        void Hide()
        {
            if (mailbox.IsCurrentPresentation(presentationEpoch))
            {
                ElementComposition.SetElementChildVisual(host, null);
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Hide();
        }
        else
        {
            Dispatcher.UIThread.Post(Hide, DispatcherPriority.Render);
        }
    }

    private Task HideSurfaceAsync(long presentationEpoch) => Dispatcher.UIThread.InvokeAsync(() =>
    {
        if (mailbox.IsCurrentPresentation(presentationEpoch))
        {
            ElementComposition.SetElementChildVisual(host, null);
        }
    }, DispatcherPriority.Render).GetTask();

    private static void ReclaimGpuResources(
        WorkerResources resources,
        IModelGpuFrameRenderer renderer)
    {
        using (resources.Context.EnsureCurrent())
        {
            renderer.ReclaimReleasedGpuResources(resources.GrContext);
        }
    }

    private async Task DisposeWorkerResourcesAsync(WorkerResources? resources)
    {
        if (resources is null)
        {
            return;
        }

        await DisposeOutputsAsync(resources, throwOnPresentationFailure: false)
            .ConfigureAwait(false);
        resources.Dispose();
    }

    private async Task DisposeOutputsAsync(
        WorkerResources resources,
        bool throwOnPresentationFailure)
    {
        resources.OutputBuffers.DropReadyBuffers();
        Exception? firstFailure = null;
        foreach (WorkerOutput output in resources.Outputs)
        {
            if (output.PresentationTask is { } presentationTask)
            {
                try
                {
                    await presentationTask.ConfigureAwait(false);
                    resources.OutputBuffers.MarkPresented(resources.Outputs.IndexOf(output));
                }
                catch (Exception exception)
                {
                    firstFailure ??= exception;
                    GpuCompositionPresenterLog.WorkerCleanupFailed(
                        logger,
                        exception,
                        exception.GetType().Name);
                    resources.OutputBuffers.MarkPresentationFailed(
                        resources.Outputs.IndexOf(output));
                }
                finally
                {
                    output.PresentationTask = null;
                    output.PendingTiming = null;
                }
            }

            try
            {
                if (output.CompletionFence is not null)
                {
                    using (resources.Context.EnsureCurrent())
                    {
                        output.CompletionFence.Dispose();
                    }

                    output.CompletionFence = null;
                    resources.OutputBuffers.ReleaseRenderBuffer(resources.Outputs.IndexOf(output));
                }
                await DisposeImportedImageAsync(output.ImportedImage).ConfigureAwait(false);
                using (resources.Context.EnsureCurrent())
                {
                    output.Surface.Dispose();
                    output.BackendTexture.Dispose();
                    output.SharedTexture.Dispose();
                }
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
                GpuCompositionPresenterLog.WorkerCleanupFailed(
                    logger,
                    exception,
                    exception.GetType().Name);
            }
        }

        resources.Outputs.Clear();
        resources.OutputBuffers = new GpuCompositionOutputBufferCoordinator(OutputBufferCount);
        if (throwOnPresentationFailure && firstFailure is not null)
        {
            throw firstFailure;
        }
    }

    private static Task DisposeImportedImageAsync(ICompositionImportedGpuImage image) =>
        Dispatcher.UIThread.InvokeAsync(async () => await image.DisposeAsync(),
            DispatcherPriority.Render);

    private async Task DisposeBindingsAsync(CompositionBindings? bindings)
    {
        if (bindings is null)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ElementComposition.SetElementChildVisual(host, null);
            bindings.DrawingSurface.Dispose();
        }, DispatcherPriority.Render);
    }

    private void LogMetricsIfDue(
        ref long metricsStartedAt,
        ref long processedCount,
        ref long supersededCount,
        ref long bufferStarvedCount,
        GpuCompositionTimingWindow timingWindow)
    {
        if (Stopwatch.GetElapsedTime(metricsStartedAt) < TimeSpan.FromSeconds(1))
        {
            return;
        }

        GpuCompositionTimingSnapshot timing = timingWindow.SnapshotAndReset();
        RenderPipelineMetricSnapshot pipeline = pipelineMetrics.SnapshotAndReset();
        GpuCompositionPresenterLog.WorkerMetrics(
            logger,
            processedCount,
            supersededCount,
            bufferStarvedCount,
            timing.SampleCount,
            timing.RenderCommandP50Ms,
            timing.RenderCommandP95Ms,
            timing.FlushP50Ms,
            timing.FlushP95Ms,
            timing.CompositionUpdateP50Ms,
            timing.CompositionUpdateP95Ms,
            timing.FrameCycleP50Ms,
            timing.FrameCycleP95Ms);
        GpuCompositionPresenterLog.PipelineMetrics(
            logger,
            pipeline.ProducedFrames,
            pipeline.PresentedFrames,
            pipeline.SupersededFrames,
            pipeline.ReadyFramesRecycled,
            pipeline.FenceFailures,
            pipeline.RenderP50Ms,
            pipeline.RenderP95Ms,
            pipeline.PresentP50Ms,
            pipeline.PresentP95Ms);
        processedCount = 0;
        supersededCount = 0;
        bufferStarvedCount = 0;
        metricsStartedAt = Stopwatch.GetTimestamp();
    }

    private static GRGlInterface CreateGlInterface(IGlContext context)
    {
        GRGlGetProcedureAddressDelegate getProcedureAddress =
            procedure => context.GlInterface.GetProcAddress(procedure);
        return context.Version.Type == GlProfileType.OpenGL
            ? GRGlInterface.CreateOpenGl(getProcedureAddress)
            : GRGlInterface.CreateGles(getProcedureAddress);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetimeCancellation.Cancel();
        if (Volatile.Read(ref started) != 0)
        {
            try
            {
                await workerTask.WaitAsync(StopTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                GpuCompositionPresenterLog.StopTimedOut(
                    logger,
                    StopTimeout.TotalMilliseconds);
                return;
            }
        }

        mailbox.Dispose();
        lifetimeCancellation.Dispose();
    }
}

internal static partial class GpuCompositionPresenterLog
{
    [LoggerMessage(6340, LogLevel.Information, "GPU composition worker started")]
    internal static partial void WorkerStarted(ILogger logger);

    [LoggerMessage(6341, LogLevel.Information,
        "GPU composition worker initialized in {DurationMs} ms")]
    internal static partial void Initialized(ILogger logger, double durationMs);

    [LoggerMessage(6342, LogLevel.Warning,
        "GPU composition worker is unavailable because {Support}")]
    internal static partial void Unavailable(
        ILogger logger,
        GpuCompositionProbeSupport support);

    [LoggerMessage(6343, LogLevel.Warning,
        "GPU composition worker failed with {ExceptionType}; fallback reason: {FaultReason}")]
    internal static partial void WorkerFailed(
        ILogger logger,
        Exception exception,
        ModelRenderingBackendFaultReason faultReason,
        string exceptionType);

    [LoggerMessage(6344, LogLevel.Information, "GPU composition worker stopped")]
    internal static partial void WorkerStopped(ILogger logger);

    [LoggerMessage(6345, LogLevel.Information,
        "GPU composition output created at {Width} x {Height}")]
    internal static partial void OutputCreated(ILogger logger, int width, int height);

    [LoggerMessage(6346, LogLevel.Information,
        "GPU composition metrics: {ProcessedCount} frames, {SupersededCount} superseded, {BufferStarvedCount} buffer-starved, {TimingSampleCount} timing samples; command p50/p95 {RenderCommandP50Ms}/{RenderCommandP95Ms} ms, flush {FlushP50Ms}/{FlushP95Ms} ms, composition {CompositionUpdateP50Ms}/{CompositionUpdateP95Ms} ms, cycle {FrameCycleP50Ms}/{FrameCycleP95Ms} ms")]
    internal static partial void WorkerMetrics(
        ILogger logger,
        long processedCount,
        long supersededCount,
        long bufferStarvedCount,
        int timingSampleCount,
        double renderCommandP50Ms,
        double renderCommandP95Ms,
        double flushP50Ms,
        double flushP95Ms,
        double compositionUpdateP50Ms,
        double compositionUpdateP95Ms,
        double frameCycleP50Ms,
        double frameCycleP95Ms);

    [LoggerMessage(6347, LogLevel.Warning,
        "GPU composition worker cleanup failed with {ExceptionType}")]
    internal static partial void WorkerCleanupFailed(
        ILogger logger,
        Exception exception,
        string exceptionType);

    [LoggerMessage(6348, LogLevel.Warning,
        "GPU composition worker did not stop within {TimeoutMs} ms")]
    internal static partial void StopTimedOut(ILogger logger, double timeoutMs);

    [LoggerMessage(6349, LogLevel.Debug,
        "GPU composition presentation epoch {PresentationEpoch} invalidated at request generation {Generation}")]
    internal static partial void PresentationInvalidated(
        ILogger logger,
        long generation,
        long presentationEpoch);

    [LoggerMessage(6350, LogLevel.Information,
        "GPU composition completion fences enabled: {FenceEnabled}; synchronous flush fallback is used when disabled")]
    internal static partial void FenceModeSelected(ILogger logger, bool fenceEnabled);

    [LoggerMessage(6351, LogLevel.Warning, "GPU composition frame readback failed with {ErrorType}")]
    internal static partial void ReadbackFailed(ILogger logger, string errorType);

    [LoggerMessage(7057, LogLevel.Debug,
        "GPU pipeline metrics: produced {ProducedFrames}, presented {PresentedFrames}, superseded {SupersededFrames}, ready recycled {ReadyFramesRecycled}, fence failures {FenceFailures}; render p50/p95 {RenderP50Ms}/{RenderP95Ms} ms, present p50/p95 {PresentP50Ms}/{PresentP95Ms} ms")]
    internal static partial void PipelineMetrics(
        ILogger logger,
        long producedFrames,
        long presentedFrames,
        long supersededFrames,
        long readyFramesRecycled,
        long fenceFailures,
        double renderP50Ms,
        double renderP95Ms,
        double presentP50Ms,
        double presentP95Ms);
}
