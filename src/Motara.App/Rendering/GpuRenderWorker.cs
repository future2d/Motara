using Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.ModelLibrary;
using Motara.ModelRuntime.Abstractions;
using Motara.Persistence;

namespace Motara.App.Rendering;

internal sealed record GpuRenderRequest(
    long Generation,
    ModelId ModelId,
    Func<ModelRenderFrame?> FrameProvider,
    Func<ModelRasterTransform> TransformProvider,
    PixelSize PixelSize,
    double RenderingScale,
    FrameRateMode FrameRateMode);

internal enum GpuRenderResult
{
    Skipped,
    Rendered,
}

internal interface IGpuRenderBackend : IAsyncDisposable
{
    ValueTask InitializeAsync(GpuRenderRequest request, CancellationToken cancellationToken);

    GpuRenderResult Render(GpuRenderRequest request, ModelRenderFrame frame);

    void Reclaim();
}

internal sealed class GpuRenderWorker : IAsyncDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly CancellationTokenSource cancellation = new();
    private readonly Func<GpuRenderRequest, IGpuRenderBackend> backendFactory;
    private readonly WindowsGpuWorkerPolicy workerPolicy;
    private readonly ILogger logger;
    private readonly TaskCompletionSource completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private GpuRenderRequest? latest;
    private int disposed;

    internal GpuRenderWorker(
        Func<GpuRenderRequest, IGpuRenderBackend> backendFactory,
        WindowsGpuWorkerPolicy? workerPolicy = null,
        ILogger<GpuRenderWorker>? logger = null)
    {
        this.backendFactory = backendFactory
            ?? throw new ArgumentNullException(nameof(backendFactory));
        this.workerPolicy = workerPolicy ?? new WindowsGpuWorkerPolicy();
        this.logger = logger ?? NullLogger<GpuRenderWorker>.Instance;
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Motara GPU Render Worker",
        };
        thread.Start();
    }

    internal void Publish(GpuRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        lock (gate)
        {
            latest = request;
        }

        wake.Set();
    }

    private void Run()
    {
        IGpuRenderBackend? backend = null;
        long backendGeneration = -1;
        long failedGeneration = -1;
        try
        {
            try
            {
                workerPolicy.ApplyCurrentThread();
            }
            catch (Exception exception)
            {
                GpuRenderWorkerLog.PolicyFailed(logger, exception, exception.GetType().Name);
            }

            GpuRenderWorkerLog.Started(logger);
            var pacer = new GpuCompositionFramePacer();
            while (!cancellation.IsCancellationRequested)
            {
                GpuRenderRequest? request = ReadLatest();
                if (request is null || request.Generation == failedGeneration)
                {
                    WaitForWork(Timeout.InfiniteTimeSpan);
                    continue;
                }

                if (backendGeneration != request.Generation)
                {
                    DisposeBackend(backend);
                    backend = null;
                    backendGeneration = -1;
                    backend = backendFactory(request);
                    backend.InitializeAsync(request, cancellation.Token)
                        .AsTask().GetAwaiter().GetResult();
                    backendGeneration = request.Generation;
                    GpuRenderWorkerLog.GenerationInitialized(logger, backendGeneration);
                }

                ModelRenderFrame? frame = request.FrameProvider();
                if (frame is not null && pacer.ShouldRender(request.FrameRateMode))
                {
                    backend!.Render(request, frame);
                    backend.Reclaim();
                }

                WaitForWork(GpuCompositionFramePacer.TickInterval);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failedGeneration = ReadLatest()?.Generation ?? backendGeneration;
            GpuRenderWorkerLog.Failed(
                logger,
                exception,
                failedGeneration,
                exception.GetType().Name);
        }
        finally
        {
            try
            {
                DisposeBackend(backend);
            }
            catch (Exception exception)
            {
                GpuRenderWorkerLog.CleanupFailed(logger, exception, exception.GetType().Name);
            }

            GpuRenderWorkerLog.Stopped(logger);
            completion.TrySetResult();
        }
    }

    private GpuRenderRequest? ReadLatest()
    {
        lock (gate)
        {
            return latest;
        }
    }

    private void WaitForWork(TimeSpan timeout)
    {
        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        wake.WaitOne(timeout);
    }

    private static void DisposeBackend(IGpuRenderBackend? backend)
    {
        if (backend is not null)
        {
            backend.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        cancellation.Cancel();
        wake.Set();
        try
        {
            await completion.Task.WaitAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            GpuRenderWorkerLog.StopTimedOut(logger, StopTimeout.TotalMilliseconds);
            return;
        }

        wake.Dispose();
        cancellation.Dispose();
    }
}

internal static partial class GpuRenderWorkerLog
{
    [LoggerMessage(7050, LogLevel.Information, "GPU-primary render worker started")]
    internal static partial void Started(ILogger logger);

    [LoggerMessage(7051, LogLevel.Information,
        "GPU-primary render generation {Generation} initialized")]
    internal static partial void GenerationInitialized(ILogger logger, long generation);

    [LoggerMessage(7052, LogLevel.Warning,
        "GPU-primary worker policy failed with {ExceptionType}; default Windows scheduling remains active")]
    internal static partial void PolicyFailed(ILogger logger, Exception exception, string exceptionType);

    [LoggerMessage(7053, LogLevel.Error,
        "GPU-primary render generation {Generation} failed with {ExceptionType}")]
    internal static partial void Failed(
        ILogger logger,
        Exception exception,
        long generation,
        string exceptionType);

    [LoggerMessage(7054, LogLevel.Information, "GPU-primary render worker stopped")]
    internal static partial void Stopped(ILogger logger);

    [LoggerMessage(7055, LogLevel.Warning,
        "GPU-primary render worker cleanup failed with {ExceptionType}")]
    internal static partial void CleanupFailed(ILogger logger, Exception exception, string exceptionType);

    [LoggerMessage(7056, LogLevel.Warning,
        "GPU-primary render worker did not stop within {TimeoutMs} ms")]
    internal static partial void StopTimedOut(ILogger logger, double timeoutMs);
}
