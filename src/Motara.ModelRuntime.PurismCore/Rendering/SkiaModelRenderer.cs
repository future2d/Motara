using System.Collections.Immutable;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Rendering.SceneGraph;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.ModelRuntime.Abstractions;
using SkiaSharp;

namespace Motara.ModelRuntime.PurismCore;

public sealed class SkiaModelRenderer : IDisposable, IAsyncDisposable
{
    private sealed class PendingScreenshotCapture(
        ModelRenderFrame frame,
        PixelSize pixelSize,
        SKRect destination,
        SKColor background)
    {
        internal ModelRenderFrame Frame { get; } = frame;

        internal PixelSize PixelSize { get; } = pixelSize;

        internal SKRect Destination { get; } = destination;

        internal SKColor Background { get; } = background;

        internal long StartedAt { get; } = Stopwatch.GetTimestamp();

        internal TaskCompletionSource<SKImage> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private const int MaskAntialiasPaddingPixels = 2;
    private readonly ModelTextureAssets _textureAssets;
    private readonly SKImageInfo[] _textureInfos;
    private readonly MaskSurfacePool _maskSurfaces = new();
    private readonly MaskSurfacePool _maskedDrawableSurfaces = new();
    private readonly MaskSurfacePool _blendColorSurfaces = new();
    private readonly GpuDrawableBlendShaderCache _gpuBlendShaders = new();
    private readonly Dictionary<string, SKPoint[]> _positions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SKPoint[]> _textureCoordinates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ushort[]> _indices = new(StringComparer.Ordinal);
    private readonly ILogger<SkiaModelRenderer> _logger;
    private readonly bool _ownsTextureAssets;
    private readonly GpuResourceRetirementQueue _gpuRetirementQueue;
    private readonly RenderingBackendTransitionCoordinator _backendTransitions = new();
    private readonly object _lifetimeGate = new();
    private readonly object _frameCacheGate = new();
    private readonly object _rasterRenderGate = new();
    private readonly object _geometryCacheGate = new();
    private readonly object _metricsGate = new();
    private readonly object _backendGate = new();
    private readonly object _screenshotGate = new();
    private readonly TaskCompletionSource _firstFrameRendered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _resourcesDisposed = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<Task> _gpuRetirementTasks = [];
    private readonly List<PendingScreenshotCapture> _pendingScreenshotCaptures = [];
    private readonly double[] _directRenderSamples = new double[120];
    private readonly double[] _fullFrameCachePreparationSamples = new double[120];
    private int _referenceCount = 1;
    private int _firstRenderState;
    private int _disposedDrawOperationSkippedLogged;
    private int _staleCpuFramePreparationSkippedLogged;
    private long _metricsWindowStartedAt = Stopwatch.GetTimestamp();
    private long _lastDirectFrameRevision = -1;
    private long _fullFrameCacheMetricsWindowStartedAt = Stopwatch.GetTimestamp();
    private int _directRenderSampleCount;
    private int _fullFrameCachePreparationSampleCount;
    private long _directFrameCount;
    private long _fullFrameCacheCompletedFrameCount;
    private long _directSkippedFrameCount;
    private int _fullFrameCacheFramesPerSecond;
    private bool _disposed;
    private int _frameCacheStrategyLogged;
    private ModelRenderingBackendStatus _renderingBackendStatus = ModelRenderingBackendStatus.Cpu;
    private CpuTextureSet? _cpuTextures;
    private GpuTextureSet? _gpuTextures;
    private CpuTextureSet? _pendingCpuTextures;
    private long _pendingCpuGeneration;
    private bool _pendingCpuFallback;
    private Task? _cpuTextureRebuildTask;
    private CancellationTokenSource? _cpuTextureRebuildCancellation;
    private Task? _resourceDisposalTask;
    private SKImage? _cachedFrameImage;
    private ModelRenderFrame? _cachedFrame;
    private PixelSize _cachedPixelSize;
    private ModelRasterTransform _cachedRasterTransform = ModelRasterTransform.Identity;

    public Task FirstFrameRendered => _firstFrameRendered.Task;

    public event EventHandler<ModelRenderingBackendStatus>? RenderingBackendStatusChanged;

    public event EventHandler? ScreenshotWorkPending;

    public ModelRenderingBackendStatus RenderingBackendStatus
    {
        get
        {
            lock (_backendGate)
            {
                return _renderingBackendStatus;
            }
        }
    }

    internal RenderingResourceSnapshot ResourceSnapshot
    {
        get
        {
            int activeCpuSetCount;
            int activeGpuSetCount;
            int pendingCpuSetCount;
            lock (_backendGate)
            {
                activeCpuSetCount = _cpuTextures is null ? 0 : 1;
                activeGpuSetCount = _gpuTextures is null ? 0 : 1;
                pendingCpuSetCount = _pendingCpuTextures is null ? 0 : 1;
            }

            int pendingScreenshotCount;
            lock (_screenshotGate)
            {
                pendingScreenshotCount = _pendingScreenshotCaptures.Count;
            }

            return new RenderingResourceSnapshot(
                activeCpuSetCount,
                activeGpuSetCount,
                pendingCpuSetCount,
                _gpuRetirementQueue.PendingCount,
                pendingScreenshotCount);
        }
    }

    public bool RequiresFramePreparation =>
        _backendTransitions.ActiveBackend == ModelRenderingBackendPreference.Cpu;

    public int? FullFrameCacheFramesPerSecond
    {
        get
        {
            if (RenderingBackendStatus.State != ModelRenderingBackendState.Cpu)
            {
                return null;
            }

            int value = Volatile.Read(ref _fullFrameCacheFramesPerSecond);
            return value == 0 ? null : value;
        }
    }

    internal SkiaModelRenderer(ModelTextureAssets textureAssets, CpuTextureSet cpuTextures)
        : this(
            textureAssets,
            cpuTextures,
            NullLogger<SkiaModelRenderer>.Instance,
            ownsTextureAssets: false,
            new GpuResourceRetirementQueue())
    {
    }

    internal SkiaModelRenderer(
        ModelTextureAssets textureAssets,
        CpuTextureSet cpuTextures,
        ILogger<SkiaModelRenderer> logger)
        : this(
            textureAssets,
            cpuTextures,
            logger,
            ownsTextureAssets: false,
            new GpuResourceRetirementQueue())
    {
    }

    private SkiaModelRenderer(
        ModelTextureAssets textureAssets,
        CpuTextureSet cpuTextures,
        ILogger<SkiaModelRenderer> logger,
        bool ownsTextureAssets,
        GpuResourceRetirementQueue gpuRetirementQueue)
    {
        ArgumentNullException.ThrowIfNull(textureAssets);
        ArgumentNullException.ThrowIfNull(cpuTextures);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(gpuRetirementQueue);
        if (textureAssets.Count != cpuTextures.Count)
        {
            throw new ArgumentException("Texture asset and CPU texture counts must match.");
        }

        _textureAssets = textureAssets;
        _cpuTextures = cpuTextures;
        _textureInfos = Enumerable.Range(0, cpuTextures.Count)
            .Select(cpuTextures.GetInfo)
            .ToArray();
        _logger = logger;
        _ownsTextureAssets = ownsTextureAssets;
        _gpuRetirementQueue = gpuRetirementQueue;
    }

    public void SetRenderingBackendPreference(ModelRenderingBackendPreference preference)
    {
        long generation = _backendTransitions.SetDesired(preference);
        ModelRenderingBackendStatus status = _backendTransitions.Status;
        SkiaRendererLog.BackendPreferenceRequested(
            _logger,
            generation,
            preference,
            status.State);
        CancellationTokenSource? obsoleteCpuRebuild = null;
        if (preference == ModelRenderingBackendPreference.Gpu
            && _backendTransitions.CanRenderGpu)
        {
            lock (_backendGate)
            {
                obsoleteCpuRebuild = _cpuTextureRebuildCancellation;
            }
        }

        obsoleteCpuRebuild?.Cancel();
        if (status.State == ModelRenderingBackendState.SwitchingToCpu)
        {
            bool fallback = status.LastFaultReason is not null;
            bool started = fallback
                ? _backendTransitions.TryBeginCpuFallback(
                    generation,
                    status.LastFaultReason!.Value)
                : _backendTransitions.TryBeginCpuRebuild(generation);
            if (started)
            {
                StartCpuTextureRebuild(
                    generation,
                    fallback,
                    status.LastFaultReason);
            }
        }

        PublishRenderingBackendStatus(_backendTransitions.Status);
    }

    public bool TryRenderGpu(
        GRContext? grContext,
        SKCanvas canvas,
        ModelRenderFrame frame,
        PixelSize pixelSize,
        double renderingScale,
        SKPaint? paint)
        => TryRenderGpu(
            grContext,
            canvas,
            frame,
            pixelSize,
            renderingScale,
            ModelRasterTransform.Identity,
            paint);

    public bool TryRenderGpu(
        GRContext? grContext,
        SKCanvas canvas,
        ModelRenderFrame frame,
        PixelSize pixelSize,
        double renderingScale,
        ModelRasterTransform rasterTransform,
        SKPaint? paint)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(frame);
        ValidateRasterTransform(rasterTransform);
        if (grContext is null || grContext.IsAbandoned)
        {
            long generation = _backendTransitions.Generation;
            if (_backendTransitions.ActiveBackend == ModelRenderingBackendPreference.Gpu)
            {
                MarkActiveGpuContextAbandoned();
                BeginCpuFallback(generation, ModelRenderingBackendFaultReason.GpuContextLost);
                return true;
            }

            if (_backendTransitions.DesiredBackend == ModelRenderingBackendPreference.Gpu
                && _backendTransitions.TryBeginGpuUpload(generation))
            {
                _backendTransitions.FailGpuAttempt(
                    generation,
                    grContext is null
                        ? ModelRenderingBackendFaultReason.GpuUnavailable
                        : ModelRenderingBackendFaultReason.GpuContextLost);
                PublishRenderingBackendStatus(_backendTransitions.Status);
            }

            return false;
        }

        bool renderingStarted = false;
        try
        {
            lock (_rasterRenderGate)
            {
                ProcessPendingGpuCaptures(grContext);
                CommitPendingCpuTextures();
                if (_backendTransitions.ActiveBackend == ModelRenderingBackendPreference.Gpu)
                {
                    if (!_backendTransitions.CanRenderGpu)
                    {
                        return true;
                    }

                    lock (_backendGate)
                    {
                        if (_gpuTextures is null)
                        {
                            return true;
                        }

                        if (!ReferenceEquals(_gpuTextures.Context, grContext))
                        {
                            _gpuTextures.MarkContextAbandoned();
                            BeginCpuFallback(
                                _backendTransitions.Generation,
                                ModelRenderingBackendFaultReason.GpuContextLost);
                            return true;
                        }
                    }
                }
                else
                {
                    if (_backendTransitions.DesiredBackend != ModelRenderingBackendPreference.Gpu)
                    {
                        return false;
                    }

                    EnsureGpuTextures(grContext);
                    if (!_backendTransitions.CanRenderGpu)
                    {
                        return false;
                    }
                }

                ObjectDisposedException.ThrowIf(_disposed, this);
                GpuTextureSet textureSource;
                lock (_backendGate)
                {
                    textureSource = _gpuTextures ?? throw new InvalidOperationException(
                        "GPU textures are unavailable for the active backend.");
                }
                renderingStarted = true;
                RenderDirectCore(
                    canvas,
                    frame,
                    new SKRect(
                        0,
                        0,
                        pixelSize.Width / (float)renderingScale,
                        pixelSize.Height / (float)renderingScale),
                    pixelSize,
                    renderingScale,
                    textureSource,
                    rasterTransform,
                    paint);
            }

            return true;
        }
        catch (Exception exception) when (exception is not ObjectDisposedException)
        {
            SkiaRendererLog.GpuRenderingFailed(_logger, exception.GetType().Name);
            long generation = _backendTransitions.Generation;
            if (_backendTransitions.ActiveBackend == ModelRenderingBackendPreference.Gpu)
            {
                if (grContext.IsAbandoned)
                {
                    MarkActiveGpuContextAbandoned();
                }

                BeginCpuFallback(
                    generation,
                    renderingStarted
                        ? ModelRenderingBackendFaultReason.GpuRenderingFailed
                        : ModelRenderingBackendFaultReason.GpuTextureUploadFailed);
                return true;
            }

            if (_backendTransitions.FailGpuAttempt(
                    generation,
                    ModelRenderingBackendFaultReason.GpuTextureUploadFailed))
            {
                PublishRenderingBackendStatus(_backendTransitions.Status);
            }

            return false;
        }
    }

    public bool TryRenderGpuFrame(
        GRContext? grContext,
        SKCanvas canvas,
        ModelRenderFrame frame,
        PixelSize pixelSize,
        double renderingScale,
        SKPaint? paint)
        => TryRenderGpuFrame(
            grContext,
            canvas,
            frame,
            pixelSize,
            renderingScale,
            ModelRasterTransform.Identity,
            paint);

    public bool TryRenderGpuFrame(
        GRContext? grContext,
        SKCanvas canvas,
        ModelRenderFrame frame,
        PixelSize pixelSize,
        double renderingScale,
        ModelRasterTransform rasterTransform,
        SKPaint? paint)
    {
        if (!TryAcquireRenderReference())
        {
            return false;
        }

        try
        {
            return TryRenderGpu(
                grContext,
                canvas,
                frame,
                pixelSize,
                renderingScale,
                rasterTransform,
                paint);
        }
        finally
        {
            ReleaseRenderReference();
        }
    }

    public void ReportGpuCompositionFailure(ModelRenderingBackendFaultReason faultReason)
    {
        if (!Enum.IsDefined(faultReason))
        {
            throw new ArgumentOutOfRangeException(nameof(faultReason));
        }

        long generation = _backendTransitions.Generation;
        SkiaRendererLog.GpuCompositionFailureReported(
            _logger,
            generation,
            faultReason);
        if (_backendTransitions.ActiveBackend == ModelRenderingBackendPreference.Gpu)
        {
            if (faultReason is ModelRenderingBackendFaultReason.GpuUnavailable
                or ModelRenderingBackendFaultReason.GpuContextLost)
            {
                MarkActiveGpuContextAbandoned();
            }

            BeginCpuFallback(generation, faultReason);
            return;
        }

        if (_backendTransitions.DesiredBackend == ModelRenderingBackendPreference.Gpu
            && _backendTransitions.TryBeginGpuUpload(generation)
            && _backendTransitions.FailGpuAttempt(generation, faultReason))
        {
            PublishRenderingBackendStatus(_backendTransitions.Status);
        }
    }

    private void EnsureGpuTextures(GRContext context)
    {
        long generation = _backendTransitions.Generation;
        if (_backendTransitions.ActiveBackend == ModelRenderingBackendPreference.Gpu)
        {
            return;
        }

        if (!_backendTransitions.TryBeginGpuUpload(generation))
        {
            return;
        }

        CpuTextureSet cpuTextures;
        lock (_backendGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cpuTextures = _cpuTextures ?? throw new InvalidOperationException(
                "CPU textures are unavailable for GPU upload.");
        }

        GpuTextureSet uploaded;
        long uploadStartedAt = Stopwatch.GetTimestamp();
        try
        {
            uploaded = GpuTextureSet.Create(context, cpuTextures);
        }
        catch
        {
            _backendTransitions.FailGpuAttempt(
                generation,
                ModelRenderingBackendFaultReason.GpuTextureUploadFailed);
            PublishRenderingBackendStatus(_backendTransitions.Status);
            throw;
        }

        bool committed;
        lock (_backendGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            committed = _backendTransitions.TryCommitGpu(generation);
            if (committed)
            {
                _gpuTextures = uploaded;
                _cpuTextures = null;
            }
        }

        if (!committed)
        {
            ReleaseGpuTextureResources(uploaded);
            return;
        }

        cpuTextures.Dispose();
        SkiaRendererLog.GpuTextureUploadSubmitted(
            _logger,
            generation,
            uploaded.Count,
            uploaded.EstimatedBytes,
            Stopwatch.GetElapsedTime(uploadStartedAt).TotalMilliseconds);
        SkiaRendererLog.GpuRenderingEnabled(_logger);
        PublishRenderingBackendStatus(_backendTransitions.Status);
    }

    private void BeginCpuFallback(
        long generation,
        ModelRenderingBackendFaultReason faultReason)
    {
        if (_backendTransitions.TryBeginCpuFallback(generation, faultReason))
        {
            StartCpuTextureRebuild(generation, fallback: true, faultReason);
            PublishRenderingBackendStatus(_backendTransitions.Status);
        }
    }

    private void MarkActiveGpuContextAbandoned()
    {
        lock (_backendGate)
        {
            _gpuTextures?.MarkContextAbandoned();
        }
    }

    private void StartCpuTextureRebuild(
        long generation,
        bool fallback,
        ModelRenderingBackendFaultReason? faultReason)
    {
        CancellationTokenSource? previous;
        var cancellation = new CancellationTokenSource();
        lock (_backendGate)
        {
            if (_disposed)
            {
                cancellation.Dispose();
                return;
            }

            previous = _cpuTextureRebuildCancellation;
            _cpuTextureRebuildCancellation = cancellation;
            _cpuTextureRebuildTask = RebuildCpuTexturesAsync(
                generation,
                fallback,
                faultReason,
                cancellation);
        }

        previous?.Cancel();
        SkiaRendererLog.CpuTextureRebuildStarted(_logger, faultReason);
    }

    private async Task RebuildCpuTexturesAsync(
        long generation,
        bool fallback,
        ModelRenderingBackendFaultReason? faultReason,
        CancellationTokenSource cancellation)
    {
        CpuTextureSet? decoded = null;
        long rebuildStartedAt = Stopwatch.GetTimestamp();
        try
        {
            decoded = await _textureAssets.DecodeCpuTexturesAsync(cancellation.Token)
                .ConfigureAwait(false);
            long decodedBytes = decoded.EstimatedBytes;
            CpuTextureSet? previousPending;
            lock (_backendGate)
            {
                if (_disposed
                    || cancellation.IsCancellationRequested
                    || !ReferenceEquals(_cpuTextureRebuildCancellation, cancellation)
                    || generation != _backendTransitions.Generation)
                {
                    return;
                }

                previousPending = _pendingCpuTextures;
                _pendingCpuTextures = decoded;
                _pendingCpuGeneration = generation;
                _pendingCpuFallback = fallback;
                decoded = null;
            }

            previousPending?.Dispose();
            SkiaRendererLog.CpuTextureRebuildCompleted(
                _logger,
                generation,
                decodedBytes,
                Stopwatch.GetElapsedTime(rebuildStartedAt).TotalMilliseconds);
            lock (_rasterRenderGate)
            {
                CommitPendingCpuTextures();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SkiaRendererLog.CpuTextureRebuildCanceled(_logger);
            return;
        }
        catch (Exception exception)
        {
            SkiaRendererLog.CpuTextureRebuildFailed(_logger, exception.GetType().Name);
            if (_backendTransitions.FailCpuRebuild(generation))
            {
                PublishRenderingBackendStatus(_backendTransitions.Status);
            }
        }
        finally
        {
            decoded?.Dispose();
            lock (_backendGate)
            {
                if (ReferenceEquals(_cpuTextureRebuildCancellation, cancellation))
                {
                    _cpuTextureRebuildCancellation = null;
                    _cpuTextureRebuildTask = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private bool CommitPendingCpuTextures()
    {
        CpuTextureSet? pending;
        GpuTextureSet? releasedGpu = null;
        bool committed;
        lock (_backendGate)
        {
            pending = _pendingCpuTextures;
            if (pending is null)
            {
                return false;
            }

            committed = _pendingCpuFallback
                ? _backendTransitions.TryCommitCpuFallback(_pendingCpuGeneration)
                : _backendTransitions.TryCommitCpu(_pendingCpuGeneration);
            _pendingCpuTextures = null;
            if (committed)
            {
                releasedGpu = _gpuTextures;
                _gpuTextures = null;
                _cpuTextures = pending;
                pending = null;
            }
        }

        pending?.Dispose();
        if (committed && releasedGpu is not null)
        {
            _gpuBlendShaders.Clear();
        }
        ReleaseGpuTextureResources(releasedGpu);
        if (committed)
        {
            PublishRenderingBackendStatus(_backendTransitions.Status);
        }

        return committed;
    }

    private bool TryGetCpuTextureSource(out CpuTextureSet textureSource)
    {
        lock (_backendGate)
        {
            if (_backendTransitions.ActiveBackend != ModelRenderingBackendPreference.Cpu
                || _cpuTextures is null)
            {
                textureSource = null!;
                return false;
            }

            textureSource = _cpuTextures;
            return true;
        }
    }

    private void ReleaseGpuTextureResources(GpuTextureSet? textures)
    {
        if (textures is null)
        {
            return;
        }

        GpuRetirementTicket ticket = _gpuRetirementQueue.Enqueue(textures);
        lock (_lifetimeGate)
        {
            _gpuRetirementTasks.Add(ticket.Completion);
        }

        _ = ticket.Completion.ContinueWith(
            static (completed, state) =>
            {
                var renderer = (SkiaModelRenderer)state!;
                if (completed.IsFaulted)
                {
                    Exception? exception = completed.Exception?.GetBaseException();
                    SkiaRendererLog.GpuTextureResourceCacheReclaimFailed(
                        renderer._logger,
                        exception?.GetType().Name ?? "Unknown");
                }

                lock (renderer._lifetimeGate)
                {
                    renderer._gpuRetirementTasks.Remove(completed);
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void ReclaimReleasedGpuResources(GRContext? activeContext)
    {
        try
        {
            GpuRetirementDrainResult result = activeContext is null
                ? _gpuRetirementQueue.DrainAbandoned()
                : _gpuRetirementQueue.Drain(activeContext);
            if (result.RetiredSetCount == 0)
            {
                return;
            }

            SkiaRendererLog.GpuTextureResourceCacheReclaimed(
                _logger,
                result.RetiredSetCount,
                result.RetiredBytes,
                0,
                0);
        }
        catch (Exception exception) when (exception is not ObjectDisposedException)
        {
            SkiaRendererLog.GpuTextureResourceCacheReclaimFailed(_logger, exception.GetType().Name);
        }
    }

    private void PublishRenderingBackendStatus(ModelRenderingBackendStatus status)
    {
        bool changed;
        lock (_backendGate)
        {
            if (_disposed || _renderingBackendStatus == status)
            {
                return;
            }

            _renderingBackendStatus = status;
            changed = true;
        }

        if (changed)
        {
            RenderingBackendStatusChanged?.Invoke(this, status);
        }
    }

    public static Task<SkiaModelRenderer> CreateAsync(
        IModelAssetSource assets,
        ImmutableArray<string> textureAssetIds,
        ILogger<SkiaModelRenderer> logger,
        SkiaModelRenderMaintenance maintenance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(maintenance);
        return CreateAsync(
            assets,
            textureAssetIds,
            logger,
            maintenance.RetirementQueue,
            cancellationToken);
    }

    public static async Task<SkiaModelRenderer> CreateAsync(
        IModelAssetSource assets,
        ImmutableArray<string> textureAssetIds,
        CancellationToken cancellationToken) => await CreateAsync(
            assets,
            textureAssetIds,
            NullLogger<SkiaModelRenderer>.Instance,
            cancellationToken).ConfigureAwait(false);

    public static async Task<SkiaModelRenderer> CreateAsync(
        IModelAssetSource assets,
        ImmutableArray<string> textureAssetIds,
        ILogger<SkiaModelRenderer> logger,
        CancellationToken cancellationToken) => await CreateAsync(
            assets,
            textureAssetIds,
            logger,
            new GpuResourceRetirementQueue(logger),
            cancellationToken).ConfigureAwait(false);

    internal static async Task<SkiaModelRenderer> CreateAsync(
        IModelAssetSource assets,
        ImmutableArray<string> textureAssetIds,
        ILogger<SkiaModelRenderer> logger,
        GpuResourceRetirementQueue gpuRetirementQueue,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(gpuRetirementQueue);
        ModelTextureAssets textureAssets = await ModelTextureAssets.LoadAsync(
            assets,
            textureAssetIds,
            cancellationToken).ConfigureAwait(false);
        CpuTextureSet? cpuTextures = null;
        try
        {
            cpuTextures = await textureAssets.DecodeCpuTexturesAsync(cancellationToken)
                .ConfigureAwait(false);
            return new SkiaModelRenderer(
                textureAssets,
                cpuTextures,
                logger,
                ownsTextureAssets: true,
                gpuRetirementQueue);
        }
        catch
        {
            cpuTextures?.Dispose();
            textureAssets.Dispose();
            throw;
        }
    }

    public static async Task<SkiaModelRenderer> CreateAsync(
        ImmutableArray<string> texturePaths,
        CancellationToken cancellationToken) => await CreateAsync(
            texturePaths,
            NullLogger<SkiaModelRenderer>.Instance,
            cancellationToken).ConfigureAwait(false);

    public static async Task<SkiaModelRenderer> CreateAsync(
        ImmutableArray<string> texturePaths,
        ILogger<SkiaModelRenderer> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ModelTextureAssets textureAssets = await ModelTextureAssets.LoadAsync(
            texturePaths,
            cancellationToken)
            .ConfigureAwait(false);
        CpuTextureSet? cpuTextures = null;
        try
        {
            cpuTextures = await textureAssets.DecodeCpuTexturesAsync(cancellationToken)
                .ConfigureAwait(false);
            return new SkiaModelRenderer(
                textureAssets,
                cpuTextures,
                logger,
                ownsTextureAssets: true,
                new GpuResourceRetirementQueue());
        }
        catch
        {
            cpuTextures?.Dispose();
            textureAssets.Dispose();
            throw;
        }
    }

    public ICustomDrawOperation CreateDrawOperation(
        ModelRenderFrame frame,
        PixelSize pixelSize,
        double renderingScale,
        double? blurRadius = null)
        => CreateDrawOperation(
            frame,
            pixelSize,
            renderingScale,
            ModelRasterTransform.Identity,
            blurRadius);

    public ICustomDrawOperation CreateDrawOperation(
        ModelRenderFrame frame,
        PixelSize pixelSize,
        double renderingScale,
        ModelRasterTransform rasterTransform,
        double? blurRadius = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateRasterTransform(rasterTransform);
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelSize));
        }

        if (!double.IsFinite(renderingScale) || renderingScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(renderingScale));
        }

        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _referenceCount++;
        }

        return new SkiaModelDrawOperation(
            this,
            frame,
            pixelSize,
            renderingScale,
            rasterTransform,
            blurRadius);
    }

    public Task PrepareFrameAsync(
        ModelRenderFrame frame,
        PixelSize pixelSize,
        CancellationToken cancellationToken)
        => PrepareFrameAsync(
            frame,
            pixelSize,
            ModelRasterTransform.Identity,
            cancellationToken);

    public Task PrepareFrameAsync(
        ModelRenderFrame frame,
        PixelSize pixelSize,
        ModelRasterTransform rasterTransform,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateRasterTransform(rasterTransform);
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelSize));
        }

        bool referenceAcquired = TryAcquireRenderReference();
        ObjectDisposedException.ThrowIf(!referenceAcquired, this);

        return PrepareFrameTrackedAsync(frame, pixelSize, rasterTransform, cancellationToken);
    }

    private async Task PrepareFrameTrackedAsync(
        ModelRenderFrame frame,
        PixelSize pixelSize,
        ModelRasterTransform rasterTransform,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(
                    () => PrepareFrameCore(frame, pixelSize, rasterTransform, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ReleaseRenderReference();
        }
    }

    internal void Render(SKCanvas canvas, ModelRenderFrame frame, SKRect destination)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(frame);
        if (!float.IsFinite(destination.Left)
            || !float.IsFinite(destination.Top)
            || !float.IsFinite(destination.Right)
            || !float.IsFinite(destination.Bottom)
            || destination.Width <= 0
            || destination.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(destination));
        }

        lock (_rasterRenderGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!TryGetCpuTextureSource(out CpuTextureSet textureSource))
            {
                throw new InvalidOperationException("CPU textures are unavailable for raster rendering.");
            }
            SKRectI clip = canvas.DeviceClipBounds;
            RenderRasterCore(
                canvas,
                frame,
                destination,
                clip.Width,
                clip.Height,
                renderingScale: 1,
                textureSource,
                ModelRasterTransform.Identity,
                reuseMaskedDrawableSurface: false);
        }
    }

    public async Task<SKImage> CaptureFrameAsync(
        ModelRenderFrame frame,
        PixelSize pixelSize,
        SKRect destination,
        SKColor background,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelSize));
        }

        cancellationToken.ThrowIfCancellationRequested();
        bool referenceAcquired = TryAcquireRenderReference();
        ObjectDisposedException.ThrowIf(!referenceAcquired, this);

        PendingScreenshotCapture? pending = null;
        long captureStartedAt = Stopwatch.GetTimestamp();
        try
        {
            SKImage? immediate = await Task.Run(() =>
            {
                lock (_rasterRenderGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_backendTransitions.ActiveBackend == ModelRenderingBackendPreference.Cpu)
                    {
                        if (!TryGetCpuTextureSource(out CpuTextureSet textureSource))
                        {
                            throw new InvalidOperationException(
                                "CPU textures are unavailable for screenshot capture.");
                        }

                        return CaptureCpuFrameCore(
                            frame,
                            pixelSize,
                            destination,
                            background,
                            textureSource);
                    }

                    pending = new PendingScreenshotCapture(frame, pixelSize, destination, background);
                    lock (_screenshotGate)
                    {
                        _pendingScreenshotCaptures.Add(pending);
                    }

                    return null;
                }
            }, cancellationToken).ConfigureAwait(false);
            if (immediate is not null)
            {
                SkiaRendererLog.ScreenshotCompleted(
                    _logger,
                    "Cpu",
                    pixelSize.Width,
                    pixelSize.Height,
                    Stopwatch.GetElapsedTime(captureStartedAt).TotalMilliseconds);
                return immediate;
            }

            PendingScreenshotCapture capture = pending
                ?? throw new InvalidOperationException("Screenshot capture was not scheduled.");
            try
            {
                int pendingCount;
                lock (_screenshotGate)
                {
                    pendingCount = _pendingScreenshotCaptures.Count;
                }

                SkiaRendererLog.ScreenshotQueued(
                    _logger,
                    pixelSize.Width,
                    pixelSize.Height,
                    pendingCount);
                ScreenshotWorkPending?.Invoke(this, EventArgs.Empty);
                return await capture.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancelPendingScreenshot(capture, cancellationToken);
                throw;
            }
            catch
            {
                CancelPendingScreenshot(capture, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            ReleaseRenderReference();
        }
    }

    private SKImage CaptureCpuFrameCore(
        ModelRenderFrame frame,
        PixelSize pixelSize,
        SKRect destination,
        SKColor background,
        IModelTextureShaderSource textureSource)
    {
        using SKSurface surface = MaskSurfacePool.CreateExact(pixelSize.Width, pixelSize.Height);
        surface.Canvas.Clear(background);
        RenderRasterCore(
            surface.Canvas,
            frame,
            destination,
            pixelSize.Width,
            pixelSize.Height,
            renderingScale: 1,
            textureSource,
            ModelRasterTransform.Identity,
            reuseMaskedDrawableSurface: true);
        surface.Canvas.Flush();
        return surface.Snapshot();
    }

    private void ProcessPendingGpuCaptures(GRContext grContext)
    {
        if (!_backendTransitions.CanRenderGpu)
        {
            return;
        }

        GpuTextureSet textureSource;
        lock (_backendGate)
        {
            if (_gpuTextures is null || !ReferenceEquals(_gpuTextures.Context, grContext))
            {
                return;
            }

            textureSource = _gpuTextures;
        }

        List<PendingScreenshotCapture> captures = TakePendingScreenshots();
        foreach (PendingScreenshotCapture capture in captures)
        {
            SKImage? image = null;
            try
            {
                image = CaptureGpuFrameCore(grContext, capture, textureSource);
                if (!capture.Completion.TrySetResult(image))
                {
                    image.Dispose();
                }
                else
                {
                    SkiaRendererLog.ScreenshotCompleted(
                        _logger,
                        "Gpu",
                        capture.PixelSize.Width,
                        capture.PixelSize.Height,
                        Stopwatch.GetElapsedTime(capture.StartedAt).TotalMilliseconds);
                }
            }
            catch (Exception exception)
            {
                image?.Dispose();
                capture.Completion.TrySetException(exception);
            }
        }
    }

    private SKImage CaptureGpuFrameCore(
        GRContext grContext,
        PendingScreenshotCapture capture,
        IModelTextureShaderSource textureSource)
    {
        using SKSurface surface = SKSurface.Create(
            grContext,
            budgeted: false,
            new SKImageInfo(
                capture.PixelSize.Width,
                capture.PixelSize.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Premul),
            sampleCount: 1,
            GRSurfaceOrigin.TopLeft)
            ?? throw new InvalidOperationException("GPU screenshot surface is unavailable.");
        surface.Canvas.Clear(capture.Background);
        RenderGpuCaptureCore(
            surface.Canvas,
            capture.Frame,
            capture.Destination,
            capture.PixelSize,
            textureSource);
        surface.Canvas.Flush();
        using SKImage gpuImage = surface.Snapshot();
        return gpuImage.ToRasterImage(ensurePixelData: true)
            ?? throw new InvalidOperationException("GPU screenshot readback failed.");
    }

    private void RenderGpuCaptureCore(
        SKCanvas canvas,
        ModelRenderFrame frame,
        SKRect destination,
        PixelSize pixelSize,
        IModelTextureShaderSource textureSource)
    {
        foreach (ModelDrawable drawable in frame.Drawables.OrderBy(static item => item.RenderOrder))
        {
            if (drawable.Masks.IsEmpty)
            {
                DrawDrawable(
                    canvas,
                    frame,
                    destination,
                    drawable,
                    ToSkiaBlendMode(drawable.BlendMode),
                    textureSource,
                    ModelRasterTransform.Identity);
                continue;
            }

            DrawMaskedDrawableDirect(
                canvas,
                frame,
                destination,
                drawable,
                pixelSize.Width,
                pixelSize.Height,
                renderingScale: 1,
                textureSource,
                ModelRasterTransform.Identity);
        }
    }

    private List<PendingScreenshotCapture> TakePendingScreenshots()
    {
        lock (_screenshotGate)
        {
            if (_pendingScreenshotCaptures.Count == 0)
            {
                return [];
            }

            List<PendingScreenshotCapture> captures = [.. _pendingScreenshotCaptures];
            _pendingScreenshotCaptures.Clear();
            return captures;
        }
    }

    private void CancelPendingScreenshot(
        PendingScreenshotCapture capture,
        CancellationToken cancellationToken)
    {
        lock (_screenshotGate)
        {
            _pendingScreenshotCaptures.Remove(capture);
        }

        if (cancellationToken.CanBeCanceled)
        {
            capture.Completion.TrySetCanceled(cancellationToken);
        }
        else
        {
            capture.Completion.TrySetException(new ObjectDisposedException(nameof(SkiaModelRenderer)));
        }
    }

    private void CancelAllPendingScreenshots()
    {
        List<PendingScreenshotCapture> captures = TakePendingScreenshots();
        foreach (PendingScreenshotCapture capture in captures)
        {
            capture.Completion.TrySetException(new ObjectDisposedException(nameof(SkiaModelRenderer)));
        }
    }

    internal void RenderLeased(
        SKCanvas canvas,
        ModelRenderFrame frame,
        SKRect destination,
        PixelSize pixelSize,
        double renderingScale,
        SKPaint? paint = null)
        => RenderLeased(
            canvas,
            frame,
            destination,
            pixelSize,
            renderingScale,
            ModelRasterTransform.Identity,
            paint);

    internal void RenderLeased(
        SKCanvas canvas,
        ModelRenderFrame frame,
        SKRect destination,
        PixelSize pixelSize,
        double renderingScale,
        ModelRasterTransform rasterTransform,
        SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(frame);
        ValidateRasterTransform(rasterTransform);
        lock (_frameCacheGate)
        {
            try
            {
                if (_cachedFrameImage is null)
                {
                    EnsureFrameCache(frame, pixelSize, rasterTransform);
                }

                canvas.DrawImage(_cachedFrameImage!, destination, paint);
            }
            catch (Exception exception)
            {
                _firstFrameRendered.TrySetException(exception);
                throw;
            }
        }
    }

    internal bool TryRenderLeasedCpu(
        SKCanvas canvas,
        ModelRenderFrame frame,
        SKRect destination,
        PixelSize pixelSize,
        double renderingScale,
        SKPaint? paint = null)
        => TryRenderLeasedCpu(
            canvas,
            frame,
            destination,
            pixelSize,
            renderingScale,
            ModelRasterTransform.Identity,
            paint);

    internal bool TryRenderLeasedCpu(
        SKCanvas canvas,
        ModelRenderFrame frame,
        SKRect destination,
        PixelSize pixelSize,
        double renderingScale,
        ModelRasterTransform rasterTransform,
        SKPaint? paint = null)
    {
        if (_backendTransitions.Status.State != ModelRenderingBackendState.Cpu)
        {
            return false;
        }

        RenderLeased(
            canvas,
            frame,
            destination,
            pixelSize,
            renderingScale,
            rasterTransform,
            paint);
        return true;
    }

    private void EnsureFrameCache(
        ModelRenderFrame frame,
        PixelSize pixelSize,
        ModelRasterTransform rasterTransform)
    {
        if (ReferenceEquals(frame, _cachedFrame)
            && pixelSize == _cachedPixelSize
            && rasterTransform == _cachedRasterTransform)
        {
            return;
        }

        long startedAt = Stopwatch.GetTimestamp();
        LogFrameCacheStrategy();

        SKImage? image = CreateFrameCacheImage(
                frame,
                pixelSize,
                rasterTransform,
                CancellationToken.None)
            ?? throw new InvalidOperationException(
                "CPU textures are unavailable for frame preparation.");
        SwapFrameCache(frame, pixelSize, rasterTransform, ref image);
        RecordFullFrameCachePreparation(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
    }

    private void PrepareFrameCore(
        ModelRenderFrame frame,
        PixelSize pixelSize,
        ModelRasterTransform rasterTransform,
        CancellationToken cancellationToken)
    {
        long startedAt = Stopwatch.GetTimestamp();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_frameCacheGate)
        {
            if (ReferenceEquals(frame, _cachedFrame)
                && pixelSize == _cachedPixelSize
                && rasterTransform == _cachedRasterTransform)
            {
                return;
            }
        }

        LogFrameCacheStrategy();
        SKImage? preparedImage = null;
        bool completed = false;
        try
        {
            preparedImage = CreateFrameCacheImage(
                frame,
                pixelSize,
                rasterTransform,
                cancellationToken);
            if (preparedImage is null)
            {
                if (Interlocked.Exchange(ref _staleCpuFramePreparationSkippedLogged, 1) == 0)
                {
                    SkiaRendererLog.StaleCpuFramePreparationSkipped(
                        _logger,
                        _backendTransitions.Status.State);
                }

                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (_frameCacheGate)
            {
                SwapFrameCache(
                    frame,
                    pixelSize,
                    rasterTransform,
                    ref preparedImage);
                completed = true;
            }
        }
        finally
        {
            preparedImage?.Dispose();
            if (completed)
            {
                RecordFullFrameCachePreparation(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            }
        }
    }

    private SKImage? CreateFrameCacheImage(
        ModelRenderFrame frame,
        PixelSize pixelSize,
        ModelRasterTransform rasterTransform,
        CancellationToken cancellationToken)
    {
        lock (_rasterRenderGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetCpuTextureSource(out CpuTextureSet textureSource))
            {
                return null;
            }

            using SKSurface surface = MaskSurfacePool.CreateExact(
                pixelSize.Width,
                pixelSize.Height);
            surface.Canvas.Clear(SKColors.Transparent);
            RenderRasterFrame(
                surface.Canvas,
                frame,
                new SKRect(0, 0, pixelSize.Width, pixelSize.Height),
                pixelSize.Width,
                pixelSize.Height,
                renderingScale: 1,
                textureSource,
                rasterTransform,
                reuseMaskedDrawableSurface: true);
            surface.Canvas.Flush();
            return surface.Snapshot();
        }
    }

    private void SwapFrameCache(
        ModelRenderFrame frame,
        PixelSize pixelSize,
        ModelRasterTransform rasterTransform,
        ref SKImage? preparedImage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SKImage? previousImage = _cachedFrameImage;
        _cachedFrameImage = preparedImage;
        preparedImage = null;
        _cachedFrame = frame;
        _cachedPixelSize = pixelSize;
        _cachedRasterTransform = rasterTransform;
        previousImage?.Dispose();
        _firstFrameRendered.TrySetResult();
    }

    private void LogFrameCacheStrategy()
    {
        if (Interlocked.Exchange(ref _frameCacheStrategyLogged, 1) == 0)
        {
            SkiaRendererLog.BackendSelected(_logger, "FullFrameCache");
        }
    }

    private void RenderRasterFrame(
        SKCanvas canvas,
        ModelRenderFrame frame,
        SKRect destination,
        int surfaceWidth,
        int surfaceHeight,
        double renderingScale,
        IModelTextureShaderSource textureSource,
        ModelRasterTransform rasterTransform,
        bool reuseMaskedDrawableSurface)
    {
        bool firstRender = Interlocked.CompareExchange(ref _firstRenderState, 1, 0) == 0;
        long startedAt = Stopwatch.GetTimestamp();
        if (firstRender)
        {
            SkiaRendererLog.FirstRenderStarted(
                _logger,
                GetCanvasSizeBucket(surfaceWidth, surfaceHeight));
        }

        try
        {
            RenderRasterCore(
                canvas,
                frame,
                destination,
                surfaceWidth,
                surfaceHeight,
                renderingScale,
                textureSource,
                rasterTransform,
                reuseMaskedDrawableSurface);
            if (firstRender)
            {
                SkiaRendererLog.FirstRenderCompleted(
                    _logger,
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            }
        }
        catch (Exception exception)
        {
            if (firstRender)
            {
                SkiaRendererLog.FirstRenderFailed(
                    _logger,
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    exception.GetType().Name);
            }

            throw;
        }
        finally
        {
            if (firstRender)
            {
                Volatile.Write(ref _firstRenderState, 2);
            }
        }
    }

    private void RenderRasterCore(
        SKCanvas canvas,
        ModelRenderFrame frame,
        SKRect destination,
        int surfaceWidth,
        int surfaceHeight,
        double renderingScale,
        IModelTextureShaderSource textureSource,
        ModelRasterTransform rasterTransform,
        bool reuseMaskedDrawableSurface)
    {
        foreach (ModelDrawable drawable in frame.Drawables.OrderBy(static item => item.RenderOrder))
        {
            if (drawable.Masks.IsEmpty)
            {
                DrawDrawable(
                    canvas,
                    frame,
                    destination,
                    drawable,
                    ToSkiaBlendMode(drawable.BlendMode),
                    textureSource,
                    rasterTransform);
                continue;
            }

            DrawMaskedDrawableRaster(
                canvas,
                frame,
                destination,
                drawable,
                surfaceWidth,
                surfaceHeight,
                renderingScale,
                textureSource,
                rasterTransform,
                reuseMaskedDrawableSurface);
        }
    }

    private void RenderDirectCore(
        SKCanvas canvas,
        ModelRenderFrame frame,
        SKRect destination,
        PixelSize pixelSize,
        double renderingScale,
        IModelTextureShaderSource textureSource,
        ModelRasterTransform rasterTransform,
        SKPaint? paint)
    {
        bool firstRender = Interlocked.CompareExchange(ref _firstRenderState, 1, 0) == 0;
        long startedAt = Stopwatch.GetTimestamp();
        if (firstRender)
        {
            SkiaRendererLog.FirstRenderStarted(_logger, GetCanvasSizeBucket(pixelSize.Width, pixelSize.Height));
        }

        try
        {
            if (paint is not null)
            {
                canvas.SaveLayer(destination, paint);
            }

            try
            {
                foreach (ModelDrawable drawable in frame.Drawables.OrderBy(static item => item.RenderOrder))
                {
                    if (drawable.Masks.IsEmpty)
                    {
                        DrawDrawable(
                            canvas,
                            frame,
                            destination,
                            drawable,
                            ToSkiaBlendMode(drawable.BlendMode),
                            textureSource,
                            rasterTransform);
                        continue;
                    }

                    DrawMaskedDrawableDirect(
                        canvas,
                        frame,
                        destination,
                        drawable,
                        pixelSize.Width,
                        pixelSize.Height,
                        renderingScale,
                        textureSource,
                        rasterTransform);
                }
            }
            finally
            {
                if (paint is not null)
                {
                    canvas.Restore();
                }
            }

            double durationMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            _firstFrameRendered.TrySetResult();
            RecordDirectRender(frame, durationMs);
            if (firstRender)
            {
                SkiaRendererLog.FirstRenderCompleted(_logger, durationMs);
            }
        }
        catch (Exception exception)
        {
            if (firstRender)
            {
                SkiaRendererLog.FirstRenderFailed(
                    _logger,
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    exception.GetType().Name);
            }

            throw;
        }
        finally
        {
            if (firstRender)
            {
                Volatile.Write(ref _firstRenderState, 2);
            }
        }
    }

    private void RecordDirectRender(ModelRenderFrame frame, double durationMs)
    {
        int? framesPerSecond = null;
        lock (_metricsGate)
        {
            if (_lastDirectFrameRevision >= 0
                && frame.Revision > _lastDirectFrameRevision + 1)
            {
                _directSkippedFrameCount += frame.Revision - _lastDirectFrameRevision - 1;
            }

            _lastDirectFrameRevision = frame.Revision;
            _directFrameCount++;
            if (_directRenderSampleCount < _directRenderSamples.Length)
            {
                _directRenderSamples[_directRenderSampleCount++] = durationMs;
            }
            else
            {
                _directRenderSamples[(int)(_directFrameCount % _directRenderSamples.Length)] = durationMs;
            }

            if (Stopwatch.GetElapsedTime(_metricsWindowStartedAt) < TimeSpan.FromSeconds(1))
            {
                return;
            }

            Array.Sort(_directRenderSamples, 0, _directRenderSampleCount);
            int p50Index = Math.Max(0, (int)Math.Ceiling(_directRenderSampleCount * 0.50) - 1);
            int p95Index = Math.Max(0, (int)Math.Ceiling(_directRenderSampleCount * 0.95) - 1);
            long drawableCount = frame.Drawables.Length;
            long maskedDrawableCount = frame.Drawables.LongCount(static item => !item.Masks.IsEmpty);
            SkiaRendererLog.DirectRenderMetrics(
                _logger,
                _directFrameCount,
                _directSkippedFrameCount,
                _directRenderSamples[p50Index],
                _directRenderSamples[p95Index],
                drawableCount,
                maskedDrawableCount);
            framesPerSecond = checked((int)_directFrameCount);
            _metricsWindowStartedAt = Stopwatch.GetTimestamp();
            _directFrameCount = 0;
            _directSkippedFrameCount = 0;
            _directRenderSampleCount = 0;
        }

        if (framesPerSecond is int value
            && RenderingBackendStatus.State == ModelRenderingBackendState.Gpu)
        {
            PublishRenderingBackendStatus(new ModelRenderingBackendStatus(
                ModelRenderingBackendState.Gpu,
                lastFaultReason: null,
                framesPerSecond: value));
        }
    }

    private void RecordFullFrameCachePreparation(double durationMs)
    {
        int? framesPerSecond = null;
        lock (_metricsGate)
        {
            _fullFrameCacheCompletedFrameCount++;
            if (_fullFrameCachePreparationSampleCount < _fullFrameCachePreparationSamples.Length)
            {
                _fullFrameCachePreparationSamples[_fullFrameCachePreparationSampleCount++] = durationMs;
            }
            else
            {
                _fullFrameCachePreparationSamples[
                    (int)(_fullFrameCacheCompletedFrameCount % _fullFrameCachePreparationSamples.Length)] = durationMs;
            }

            if (Stopwatch.GetElapsedTime(_fullFrameCacheMetricsWindowStartedAt) < TimeSpan.FromSeconds(1))
            {
                return;
            }

            Array.Sort(
                _fullFrameCachePreparationSamples,
                0,
                _fullFrameCachePreparationSampleCount);
            int p50Index = Math.Max(
                0,
                (int)Math.Ceiling(_fullFrameCachePreparationSampleCount * 0.50) - 1);
            int p95Index = Math.Max(
                0,
                (int)Math.Ceiling(_fullFrameCachePreparationSampleCount * 0.95) - 1);
            int completedFramesPerSecond = checked((int)_fullFrameCacheCompletedFrameCount);
            Volatile.Write(ref _fullFrameCacheFramesPerSecond, completedFramesPerSecond);
            SkiaRendererLog.FullFrameCachePreparationMetrics(
                _logger,
                completedFramesPerSecond,
                _fullFrameCachePreparationSamples[p50Index],
                _fullFrameCachePreparationSamples[p95Index]);
            framesPerSecond = completedFramesPerSecond;
            _fullFrameCacheMetricsWindowStartedAt = Stopwatch.GetTimestamp();
            _fullFrameCacheCompletedFrameCount = 0;
            _fullFrameCachePreparationSampleCount = 0;
        }

        if (framesPerSecond is int value && RenderingBackendStatus.State == ModelRenderingBackendState.Cpu)
        {
            PublishRenderingBackendStatus(new ModelRenderingBackendStatus(
                ModelRenderingBackendState.Cpu,
                RenderingBackendStatus.LastFaultReason,
                value));
        }
    }

    private static string GetCanvasSizeBucket(int width, int height) => ((long)width * height) switch
    {
        <= 500_000 => "Small",
        <= 2_500_000 => "Medium",
        <= 9_000_000 => "Large",
        _ => "VeryLarge",
    };

    public void Dispose()
    {
        CancellationTokenSource? cpuTextureRebuildCancellation;
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _backendTransitions.BeginDispose();
        CancelAllPendingScreenshots();
        lock (_backendGate)
        {
            cpuTextureRebuildCancellation = _cpuTextureRebuildCancellation;
        }

        cpuTextureRebuildCancellation?.Cancel();

        ReleaseReference();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _resourcesDisposed.Task.ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    internal void ReleaseDrawOperation() => ReleaseReference();

    internal bool TryAcquireRenderReference()
    {
        bool skipped;
        lock (_lifetimeGate)
        {
            skipped = _disposed;
            if (!skipped)
            {
                _referenceCount++;
            }
        }

        if (skipped)
        {
            ReportDisposedDrawOperation();
        }

        return !skipped;
    }

    internal void ReleaseRenderReference() => ReleaseReference();

    internal void ReportDisposedDrawOperation()
    {
        if (Interlocked.Exchange(ref _disposedDrawOperationSkippedLogged, 1) == 0)
        {
            SkiaRendererLog.DisposedDrawOperationSkipped(_logger);
        }
    }

    private void ReleaseReference()
    {
        bool disposeResources;
        lock (_lifetimeGate)
        {
            _referenceCount--;
            disposeResources = _referenceCount == 0;
        }

        if (disposeResources)
        {
            _resourceDisposalTask = DisposeResourcesAsync();
        }
    }

    private async Task DisposeResourcesAsync()
    {
        try
        {
            _firstFrameRendered.TrySetCanceled();
            lock (_frameCacheGate)
            {
                _cachedFrameImage?.Dispose();
                _cachedFrameImage = null;
                _cachedFrame = null;
                _cachedPixelSize = default;
                _cachedRasterTransform = ModelRasterTransform.Identity;
            }

            Task? cpuTextureRebuildTask;
            lock (_backendGate)
            {
                cpuTextureRebuildTask = _cpuTextureRebuildTask;
            }

            if (cpuTextureRebuildTask is not null)
            {
                await cpuTextureRebuildTask.ConfigureAwait(false);
            }

            CpuTextureSet? releasedCpuTextures;
            CpuTextureSet? releasedPendingCpuTextures;
            GpuTextureSet? releasedGpuTextures;
            lock (_backendGate)
            {
                releasedCpuTextures = _cpuTextures;
                _cpuTextures = null;
                releasedPendingCpuTextures = _pendingCpuTextures;
                _pendingCpuTextures = null;
                releasedGpuTextures = _gpuTextures;
                _gpuTextures = null;
            }

            releasedPendingCpuTextures?.Dispose();
            releasedCpuTextures?.Dispose();
            _gpuBlendShaders.Dispose();
            ReleaseGpuTextureResources(releasedGpuTextures);

            if (_ownsTextureAssets)
            {
                _textureAssets.Dispose();
            }

            _maskSurfaces.Dispose();
            _maskedDrawableSurfaces.Dispose();
            _blendColorSurfaces.Dispose();

            Task[] retirementTasks;
            lock (_lifetimeGate)
            {
                retirementTasks = [.. _gpuRetirementTasks];
            }

            await Task.WhenAll(retirementTasks).ConfigureAwait(false);
            SkiaRendererLog.RendererResourcesDisposed(_logger, retirementTasks.Length);
        }
        catch (Exception exception)
        {
            SkiaRendererLog.GpuTextureResourceCacheReclaimFailed(
                _logger,
                exception.GetType().Name);
        }
        finally
        {
            _resourcesDisposed.TrySetResult();
        }
    }

    private void DrawMaskedDrawableRaster(
        SKCanvas canvas,
        ModelRenderFrame frame,
        SKRect destination,
        ModelDrawable drawable,
        int surfaceWidth,
        int surfaceHeight,
        double renderingScale,
        IModelTextureShaderSource textureSource,
        ModelRasterTransform rasterTransform,
        bool reuseMaskedDrawableSurface)
    {
        if (!TryGetDrawablePixelBounds(
                frame,
                destination,
                drawable,
                surfaceWidth,
                surfaceHeight,
                renderingScale,
                rasterTransform,
                out SKRectI pixelBounds))
        {
            return;
        }

        int maskWidth = pixelBounds.Width;
        int maskHeight = pixelBounds.Height;
        SKSurface maskSurface = _maskSurfaces.Rent(maskWidth, maskHeight);
        float scale = (float)renderingScale;
        foreach (int maskIndex in drawable.Masks)
        {
            // Cubism uses a mask source's raw mesh and texture alpha, not its display state.
            DrawMaskSource(
                maskSurface.Canvas,
                frame,
                destination,
                frame.Drawables[maskIndex],
                textureSource,
                rasterTransform,
                (float)renderingScale,
                pixelBounds.Left,
                pixelBounds.Top);
        }

        maskSurface.Canvas.Flush();
        using SKImage maskImage = maskSurface.Snapshot(new SKRectI(0, 0, maskWidth, maskHeight));
        SKSurface maskedDrawableSurface = reuseMaskedDrawableSurface
            ? RentMaskedDrawableCompositeSurface(maskWidth, maskHeight)
            : MaskSurfacePool.CreateExact(maskWidth, maskHeight);
        try
        {
            DrawDrawable(
                maskedDrawableSurface.Canvas,
                frame,
                destination,
                drawable,
                SKBlendMode.SrcOver,
                textureSource,
                rasterTransform,
                coordinateScale: (float)renderingScale,
                offsetX: pixelBounds.Left,
                offsetY: pixelBounds.Top);
            using (var maskPaint = new SKPaint
            {
                BlendMode = drawable.IsInvertedMask ? SKBlendMode.DstOut : SKBlendMode.DstIn,
            })
            {
                maskedDrawableSurface.Canvas.DrawImage(maskImage, 0, 0, maskPaint);
            }

            maskedDrawableSurface.Canvas.Flush();
            using SKImage compositeImage = maskedDrawableSurface.Snapshot(
                new SKRectI(0, 0, maskWidth, maskHeight));
            var layerBounds = new SKRect(
                pixelBounds.Left / scale,
                pixelBounds.Top / scale,
                pixelBounds.Right / scale,
                pixelBounds.Bottom / scale);
            using var compositePaint = new SKPaint { BlendMode = ToSkiaBlendMode(drawable.BlendMode) };
            canvas.DrawImage(compositeImage, layerBounds, compositePaint);
        }
        finally
        {
            if (!reuseMaskedDrawableSurface)
            {
                maskedDrawableSurface.Dispose();
            }
        }
    }

    private SKSurface RentMaskedDrawableCompositeSurface(int width, int height)
    {
        SKImageInfo previousInfo = _maskedDrawableSurfaces.Info;
        SKSurface surface = _maskedDrawableSurfaces.Rent(width, height);
        SKImageInfo currentInfo = _maskedDrawableSurfaces.Info;
        if (currentInfo.Width != previousInfo.Width || currentInfo.Height != previousInfo.Height)
        {
            SkiaRendererLog.MaskedDrawableCompositeSurfacePoolCapacityIncreased(
                _logger,
                currentInfo.Width,
                currentInfo.Height);
        }

        return surface;
    }

    private void DrawMaskedDrawableDirect(
        SKCanvas canvas,
        ModelRenderFrame frame,
        SKRect destination,
        ModelDrawable drawable,
        int surfaceWidth,
        int surfaceHeight,
        double renderingScale,
        IModelTextureShaderSource textureSource,
        ModelRasterTransform rasterTransform)
    {
        if (!TryGetDrawablePixelBounds(
                frame,
                destination,
                drawable,
                surfaceWidth,
                surfaceHeight,
                renderingScale,
                rasterTransform,
                out SKRectI pixelBounds))
        {
            return;
        }

        float scale = (float)renderingScale;
        var layerBounds = new SKRect(
            pixelBounds.Left / scale,
            pixelBounds.Top / scale,
            pixelBounds.Right / scale,
            pixelBounds.Bottom / scale);
        using var compositePaint = new SKPaint { BlendMode = ToSkiaBlendMode(drawable.BlendMode) };
        canvas.SaveLayer(layerBounds, compositePaint);
        try
        {
            DrawDrawable(
                canvas,
                frame,
                destination,
                drawable,
                SKBlendMode.SrcOver,
                textureSource,
                rasterTransform);
            if (drawable.IsInvertedMask)
            {
                foreach (int maskIndex in drawable.Masks)
                {
                    // Cubism uses a mask source's raw mesh and texture alpha, not its display state.
                    DrawMaskSource(
                        canvas,
                        frame,
                        destination,
                        frame.Drawables[maskIndex],
                        textureSource,
                        rasterTransform,
                        blendMode: SKBlendMode.DstOut);
                }
            }
            else
            {
                using var maskPaint = new SKPaint { BlendMode = SKBlendMode.DstIn };
                canvas.SaveLayer(layerBounds, maskPaint);
                try
                {
                    foreach (int maskIndex in drawable.Masks)
                    {
                        // Cubism uses a mask source's raw mesh and texture alpha, not its display state.
                        DrawMaskSource(
                            canvas,
                            frame,
                            destination,
                            frame.Drawables[maskIndex],
                            textureSource,
                            rasterTransform);
                    }
                }
                finally
                {
                    canvas.Restore();
                }
            }
        }
        finally
        {
            canvas.Restore();
        }
    }

    private void DrawMaskSource(
        SKCanvas canvas,
        ModelRenderFrame frame,
        SKRect destination,
        ModelDrawable drawable,
        IModelTextureShaderSource textureSource,
        ModelRasterTransform rasterTransform,
        float coordinateScale = 1,
        float offsetX = 0,
        float offsetY = 0,
        SKBlendMode blendMode = SKBlendMode.SrcOver) => DrawDrawable(
            canvas,
            frame,
            destination,
            drawable,
            blendMode,
            textureSource,
            rasterTransform,
            useDrawableOpacity: false,
            coordinateScale,
            offsetX,
            offsetY);

    private static bool TryGetDrawablePixelBounds(
        ModelRenderFrame frame,
        SKRect destination,
        ModelDrawable drawable,
        int surfaceWidth,
        int surfaceHeight,
        double renderingScale,
        ModelRasterTransform rasterTransform,
        out SKRectI pixelBounds)
    {
        float modelWidth = (float)(frame.Canvas.Width / frame.Canvas.PixelsPerUnit);
        float modelHeight = (float)(frame.Canvas.Height / frame.Canvas.PixelsPerUnit);
        float modelScale = Math.Min(destination.Width / modelWidth, destination.Height / modelHeight);
        float pixelScale = (float)renderingScale;
        float rasterScale = (float)rasterTransform.Scale;
        float radians = (float)(rasterTransform.RotationDegrees * Math.PI / 180d);
        float cosine = MathF.Cos(radians);
        float sine = MathF.Sin(radians);
        float centerX = destination.MidX
            + ((float)rasterTransform.TranslationXRatio * destination.Height);
        float centerY = destination.MidY
            + ((float)rasterTransform.TranslationYRatio * destination.Height);
        float left = float.PositiveInfinity;
        float top = float.PositiveInfinity;
        float right = float.NegativeInfinity;
        float bottom = float.NegativeInfinity;

        foreach (ModelVertex vertex in drawable.Vertices)
        {
            float localX = vertex.X * modelScale * rasterScale;
            float localY = -vertex.Y * modelScale * rasterScale;
            float x = (centerX + (localX * cosine) - (localY * sine)) * pixelScale;
            float y = (centerY + (localX * sine) + (localY * cosine)) * pixelScale;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }

        int pixelLeft = Math.Max(0, (int)Math.Floor(left) - MaskAntialiasPaddingPixels);
        int pixelTop = Math.Max(0, (int)Math.Floor(top) - MaskAntialiasPaddingPixels);
        int pixelRight = Math.Min(surfaceWidth, (int)Math.Ceiling(right) + MaskAntialiasPaddingPixels);
        int pixelBottom = Math.Min(surfaceHeight, (int)Math.Ceiling(bottom) + MaskAntialiasPaddingPixels);
        if (pixelRight <= pixelLeft || pixelBottom <= pixelTop)
        {
            pixelBounds = default;
            return false;
        }

        pixelBounds = new SKRectI(pixelLeft, pixelTop, pixelRight, pixelBottom);
        return true;
    }

    private void DrawDrawable(
        SKCanvas canvas,
        ModelRenderFrame frame,
        SKRect destination,
        ModelDrawable drawable,
        SKBlendMode blendMode,
        IModelTextureShaderSource textureSource,
        ModelRasterTransform rasterTransform,
        bool useDrawableOpacity = true,
        float coordinateScale = 1,
        float offsetX = 0,
        float offsetY = 0)
    {
        if (drawable.Indices.IsEmpty)
        {
            return;
        }

        SKImageInfo textureInfo = _textureInfos[drawable.TextureIndex];
        SKPoint[] positions;
        SKPoint[] textureCoordinates;
        ushort[] indices;
        lock (_geometryCacheGate)
        {
            if (!_positions.TryGetValue(drawable.Id, out SKPoint[]? cachedPositions)
                || cachedPositions.Length != drawable.Vertices.Length)
            {
                positions = new SKPoint[drawable.Vertices.Length];
                _positions[drawable.Id] = positions;
            }
            else
            {
                positions = cachedPositions;
            }

            for (int index = 0; index < drawable.Vertices.Length; index++)
            {
                ModelVertex vertex = drawable.Vertices[index];
                positions[index] = new SKPoint(vertex.X, vertex.Y);
            }

            if (!_textureCoordinates.TryGetValue(drawable.Id, out SKPoint[]? cachedTextureCoordinates))
            {
                textureCoordinates = drawable.Vertices
                    .Select(vertex => new SKPoint(
                        vertex.U * textureInfo.Width,
                        vertex.V * textureInfo.Height))
                    .ToArray();
                _textureCoordinates.Add(drawable.Id, textureCoordinates);
            }
            else
            {
                textureCoordinates = cachedTextureCoordinates;
            }

            if (!_indices.TryGetValue(drawable.Id, out ushort[]? cachedIndices))
            {
                indices = drawable.Indices.ToArray();
                _indices.Add(drawable.Id, indices);
            }
            else
            {
                indices = cachedIndices;
            }
        }
        using SKVertices vertices = SKVertices.CreateCopy(
            SKVertexMode.Triangles,
            positions,
            textureCoordinates,
            colors: null,
            indices);
        bool hasBlendColor = drawable.MultiplyColor != ModelColor.MultiplyIdentity
            || drawable.ScreenColor != ModelColor.ScreenIdentity;
        if (hasBlendColor)
        {
            if (textureSource is GpuTextureSet)
            {
                DrawDrawableWithGpuBlendColors();
            }
            else
            {
                DrawDrawableWithBlendColors();
            }
        }
        else
        {
            using SKPaint paint = CreateDrawablePaint();
            DrawVertices(paint);
        }

        SKPaint CreateDrawablePaint() => new()
        {
            Shader = textureSource.GetShader(drawable.TextureIndex),
            BlendMode = blendMode,
            Color = SKColors.White.WithAlpha((byte)Math.Round(
                (useDrawableOpacity ? drawable.Opacity : 1) * byte.MaxValue)),
            IsAntialias = true,
        };

        void DrawVertices(SKPaint paint)
        {
            canvas.Save();
            ApplyModelTransform(
                canvas,
                frame.Canvas,
                destination,
                rasterTransform,
                coordinateScale,
                offsetX,
                offsetY);
            canvas.DrawVertices(vertices, SKBlendMode.Modulate, paint);
            canvas.Restore();
        }

        void DrawDrawableWithBlendColors()
        {
            canvas.Save();
            ApplyModelTransform(
                canvas,
                frame.Canvas,
                destination,
                rasterTransform,
                coordinateScale,
                offsetX,
                offsetY);
            SKMatrix deviceMatrix = canvas.TotalMatrix;
            canvas.Restore();

            SKPoint[] devicePositions = positions
                .Select(position => deviceMatrix.MapPoint(position))
                .ToArray();
            SKRectI clip = canvas.DeviceClipBounds;
            if (!TryGetPixelBounds(devicePositions, clip, out SKRectI pixelBounds))
            {
                return;
            }

            for (int index = 0; index < devicePositions.Length; index++)
            {
                devicePositions[index].Offset(-pixelBounds.Left, -pixelBounds.Top);
            }

            using SKVertices deviceVertices = SKVertices.CreateCopy(
                SKVertexMode.Triangles,
                devicePositions,
                textureCoordinates,
                colors: null,
                indices);
            SKSurface surface = _blendColorSurfaces.Rent(pixelBounds.Width, pixelBounds.Height);
            using (var sourcePaint = new SKPaint
            {
                Shader = textureSource.GetShader(drawable.TextureIndex),
                BlendMode = SKBlendMode.SrcOver,
                Color = SKColors.White.WithAlpha((byte)Math.Round(
                    (useDrawableOpacity ? drawable.Opacity : 1) * byte.MaxValue)),
                IsAntialias = true,
            })
            {
                surface.Canvas.DrawVertices(deviceVertices, SKBlendMode.Modulate, sourcePaint);
            }

            surface.Canvas.Flush();
            using SKPixmap pixels = surface.PeekPixels()
                ?? throw new InvalidOperationException("Drawable blend surface pixels are unavailable.");
            ApplyBlendColors(pixels, pixelBounds.Width, pixelBounds.Height, drawable);
            using SKImage image = surface.Snapshot(
                new SKRectI(0, 0, pixelBounds.Width, pixelBounds.Height));
            using var compositePaint = new SKPaint { BlendMode = blendMode };
            canvas.Save();
            canvas.ResetMatrix();
            canvas.DrawImage(image, pixelBounds.Left, pixelBounds.Top, compositePaint);
            canvas.Restore();
        }

        void DrawDrawableWithGpuBlendColors()
        {
            lock (_gpuBlendShaders.SyncRoot)
            {
                using SKPaint paint = CreateDrawablePaint();
                paint.Shader = _gpuBlendShaders.GetOrCreate(
                    drawable.Id,
                    textureSource.GetShader(drawable.TextureIndex),
                    drawable.MultiplyColor,
                    drawable.ScreenColor);
                DrawVertices(paint);
            }
        }
    }

    private static bool TryGetPixelBounds(
        SKPoint[] positions,
        SKRectI clip,
        out SKRectI pixelBounds)
    {
        if (positions.Length == 0)
        {
            pixelBounds = default;
            return false;
        }

        float left = positions[0].X;
        float top = positions[0].Y;
        float right = left;
        float bottom = top;
        for (int index = 1; index < positions.Length; index++)
        {
            SKPoint position = positions[index];
            left = Math.Min(left, position.X);
            top = Math.Min(top, position.Y);
            right = Math.Max(right, position.X);
            bottom = Math.Max(bottom, position.Y);
        }

        int pixelLeft = Math.Max(clip.Left, (int)Math.Floor(left) - MaskAntialiasPaddingPixels);
        int pixelTop = Math.Max(clip.Top, (int)Math.Floor(top) - MaskAntialiasPaddingPixels);
        int pixelRight = Math.Min(clip.Right, (int)Math.Ceiling(right) + MaskAntialiasPaddingPixels);
        int pixelBottom = Math.Min(clip.Bottom, (int)Math.Ceiling(bottom) + MaskAntialiasPaddingPixels);
        if (pixelRight <= pixelLeft || pixelBottom <= pixelTop)
        {
            pixelBounds = default;
            return false;
        }

        pixelBounds = new SKRectI(pixelLeft, pixelTop, pixelRight, pixelBottom);
        return true;
    }

    private static void ApplyBlendColors(
        SKPixmap pixels,
        int width,
        int height,
        ModelDrawable drawable)
    {
        Span<byte> data = pixels.GetPixelSpan();
        int rowBytes = checked((int)pixels.RowBytes);
        ModelColor multiply = drawable.MultiplyColor;
        ModelColor screen = drawable.ScreenColor;
        for (int y = 0; y < height; y++)
        {
            int row = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                int offset = row + (x * 4);
                byte alpha = data[offset + 3];
                if (alpha == 0)
                {
                    data[offset] = 0;
                    data[offset + 1] = 0;
                    data[offset + 2] = 0;
                    continue;
                }

                data[offset] = BlendPremultipliedChannel(
                    data[offset],
                    alpha,
                    multiply.B,
                    screen.B);
                data[offset + 1] = BlendPremultipliedChannel(
                    data[offset + 1],
                    alpha,
                    multiply.G,
                    screen.G);
                data[offset + 2] = BlendPremultipliedChannel(
                    data[offset + 2],
                    alpha,
                    multiply.R,
                    screen.R);
            }
        }
    }

    private static byte BlendPremultipliedChannel(
        byte channel,
        byte alpha,
        float multiply,
        float screen)
    {
        float unpremultiplied = channel / (float)alpha;
        float blended = (unpremultiplied * multiply * (1 - screen)) + screen;
        return (byte)Math.Clamp((int)MathF.Round(blended * alpha), 0, alpha);
    }

    private static void ApplyModelTransform(
        SKCanvas canvas,
        ModelCanvasInfo modelCanvas,
        SKRect destination,
        ModelRasterTransform rasterTransform,
        float coordinateScale,
        float offsetX,
        float offsetY)
    {
        float modelWidth = (float)(modelCanvas.Width / modelCanvas.PixelsPerUnit);
        float modelHeight = (float)(modelCanvas.Height / modelCanvas.PixelsPerUnit);
        float scale = Math.Min(destination.Width / modelWidth, destination.Height / modelHeight);
        float translationX = (float)rasterTransform.TranslationXRatio * destination.Height;
        float translationY = (float)rasterTransform.TranslationYRatio * destination.Height;
        canvas.Translate(
            ((destination.MidX + translationX) * coordinateScale) - offsetX,
            ((destination.MidY + translationY) * coordinateScale) - offsetY);
        canvas.RotateDegrees((float)rasterTransform.RotationDegrees);
        float transformedScale = scale * (float)rasterTransform.Scale * coordinateScale;
        canvas.Scale(transformedScale, -transformedScale);
    }

    private static void ValidateRasterTransform(ModelRasterTransform rasterTransform)
    {
        if (!rasterTransform.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(rasterTransform));
        }
    }

    internal static SKBlendMode ToSkiaBlendMode(ModelBlendMode blendMode) => blendMode switch
    {
        ModelBlendMode.Normal => SKBlendMode.SrcOver,
        ModelBlendMode.Additive => SKBlendMode.Plus,
        ModelBlendMode.Multiplicative => SKBlendMode.Multiply,
        ModelBlendMode.Darken => SKBlendMode.Darken,
        ModelBlendMode.ColorBurn => SKBlendMode.ColorBurn,
        ModelBlendMode.Lighten => SKBlendMode.Lighten,
        ModelBlendMode.Screen => SKBlendMode.Screen,
        ModelBlendMode.ColorDodge => SKBlendMode.ColorDodge,
        ModelBlendMode.Overlay => SKBlendMode.Overlay,
        ModelBlendMode.SoftLight => SKBlendMode.SoftLight,
        ModelBlendMode.HardLight => SKBlendMode.HardLight,
        ModelBlendMode.Hue => SKBlendMode.Hue,
        ModelBlendMode.Color => SKBlendMode.Color,
        _ => throw new ArgumentOutOfRangeException(nameof(blendMode)),
    };
}

internal readonly record struct RenderingResourceSnapshot(
    int ActiveCpuSetCount,
    int ActiveGpuSetCount,
    int PendingCpuSetCount,
    int PendingGpuRetirementCount,
    int PendingScreenshotCount);

internal static partial class SkiaRendererLog
{
    [LoggerMessage(7000, LogLevel.Information, "First model render started for {CanvasSizeBucket} canvas")]
    internal static partial void FirstRenderStarted(ILogger logger, string canvasSizeBucket);

    [LoggerMessage(7001, LogLevel.Information, "First model render completed in {DurationMs} ms")]
    internal static partial void FirstRenderCompleted(ILogger logger, double durationMs);

    [LoggerMessage(7002, LogLevel.Error,
        "First model render failed after {DurationMs} ms with {ExceptionType}")]
    internal static partial void FirstRenderFailed(
        ILogger logger,
        double durationMs,
        string exceptionType);

    [LoggerMessage(7003, LogLevel.Information,
        "Model direct rendering: {FrameCount} frames, {SkippedFrameCount} skipped frames, p50 {P50DurationMs} ms, p95 {P95DurationMs} ms, {DrawableCount} drawables, {MaskedDrawableCount} masked drawables")]
    internal static partial void DirectRenderMetrics(
        ILogger logger,
        long frameCount,
        long skippedFrameCount,
        double p50DurationMs,
        double p95DurationMs,
        long drawableCount,
        long maskedDrawableCount);

    [LoggerMessage(7004, LogLevel.Information, "Model rendering backend selected: {Backend}")]
    internal static partial void BackendSelected(ILogger logger, string backend);

    [LoggerMessage(7005, LogLevel.Information,
        "Masked drawable composite surface pool capacity increased to {Width} x {Height} pixels")]
    internal static partial void MaskedDrawableCompositeSurfacePoolCapacityIncreased(
        ILogger logger,
        int width,
        int height);

    [LoggerMessage(7006, LogLevel.Information,
        "Model full-frame cache preparation: {CompletedFrameCount} completed frames, p50 {P50DurationMs} ms, p95 {P95DurationMs} ms")]
    internal static partial void FullFrameCachePreparationMetrics(
        ILogger logger,
        long completedFrameCount,
        double p50DurationMs,
        double p95DurationMs);

    [LoggerMessage(7007, LogLevel.Information, "GPU model rendering enabled")]
    internal static partial void GpuRenderingEnabled(ILogger logger);

    [LoggerMessage(7008, LogLevel.Warning, "GPU model rendering failed with {ExceptionType}; CPU fallback requested")]
    internal static partial void GpuRenderingFailed(ILogger logger, string exceptionType);

    [LoggerMessage(7009, LogLevel.Information, "CPU model texture rebuild started after {FaultReason}")]
    internal static partial void CpuTextureRebuildStarted(
        ILogger logger,
        ModelRenderingBackendFaultReason? faultReason);

    [LoggerMessage(7010, LogLevel.Information,
        "CPU model texture rebuild generation {Generation} completed in {DurationMs} ms with {DecodedBytes} decoded bytes")]
    internal static partial void CpuTextureRebuildCompleted(
        ILogger logger,
        long generation,
        long decodedBytes,
        double durationMs);

    [LoggerMessage(7011, LogLevel.Error, "CPU model texture rebuild failed with {ExceptionType}")]
    internal static partial void CpuTextureRebuildFailed(ILogger logger, string exceptionType);

    [LoggerMessage(7012, LogLevel.Information, "GPU model rendering retry requested")]
    internal static partial void GpuRenderingRetryRequested(ILogger logger);

    [LoggerMessage(7014, LogLevel.Information, "Skipped a delayed model draw operation after its renderer was disposed")]
    internal static partial void DisposedDrawOperationSkipped(ILogger logger);

    [LoggerMessage(
        7013,
        LogLevel.Information,
        "GPU texture resource cache reclaimed: {BeforeResourceCount} resources/{BeforeResourceBytes} bytes -> {AfterResourceCount} resources/{AfterResourceBytes} bytes")]
    internal static partial void GpuTextureResourceCacheReclaimed(
        ILogger logger,
        int beforeResourceCount,
        long beforeResourceBytes,
        int afterResourceCount,
        long afterResourceBytes);

    [LoggerMessage(7016, LogLevel.Warning, "GPU texture resource retirement failed with {ExceptionType}")]
    internal static partial void GpuTextureResourceCacheReclaimFailed(ILogger logger, string exceptionType);

    [LoggerMessage(7015, LogLevel.Information, "CPU model texture rebuild canceled")]
    internal static partial void CpuTextureRebuildCanceled(ILogger logger);

    [LoggerMessage(7017, LogLevel.Debug,
        "Model rendering backend preference generation {Generation} requested {Preference}; transition state is {State}")]
    internal static partial void BackendPreferenceRequested(
        ILogger logger,
        long generation,
        ModelRenderingBackendPreference preference,
        ModelRenderingBackendState state);

    [LoggerMessage(7018, LogLevel.Information,
        "GPU texture upload generation {Generation} submitted in {DurationMs} ms for {TextureCount} textures and {EstimatedBytes} bytes")]
    internal static partial void GpuTextureUploadSubmitted(
        ILogger logger,
        long generation,
        int textureCount,
        long estimatedBytes,
        double durationMs);

    [LoggerMessage(7019, LogLevel.Debug,
        "Screenshot queued at {Width} x {Height}; {PendingCount} requests pending")]
    internal static partial void ScreenshotQueued(
        ILogger logger,
        int width,
        int height,
        int pendingCount);

    [LoggerMessage(7020, LogLevel.Information,
        "{Backend} screenshot completed at {Width} x {Height} in {DurationMs} ms")]
    internal static partial void ScreenshotCompleted(
        ILogger logger,
        string backend,
        int width,
        int height,
        double durationMs);

    [LoggerMessage(7021, LogLevel.Information,
        "Model renderer resources disposed after observing {RetirementTaskCount} GPU retirement tasks")]
    internal static partial void RendererResourcesDisposed(
        ILogger logger,
        int retirementTaskCount);

    [LoggerMessage(7022, LogLevel.Debug,
        "Skipped stale CPU frame preparation after rendering backend changed to {State}")]
    internal static partial void StaleCpuFramePreparationSkipped(
        ILogger logger,
        ModelRenderingBackendState state);

    [LoggerMessage(7023, LogLevel.Warning,
        "GPU composition failure reported for generation {Generation}: {FaultReason}")]
    internal static partial void GpuCompositionFailureReported(
        ILogger logger,
        long generation,
        ModelRenderingBackendFaultReason faultReason);

}
