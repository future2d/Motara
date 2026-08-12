using Avalonia;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.ModelRuntime.Abstractions;
using SkiaSharp;

namespace Motara.ModelRuntime.PurismCore;

internal sealed class CpuFallbackRenderer : IAsyncDisposable
{
    internal delegate SKImage RenderCompleteFrameDelegate(
        ModelRenderFrame frame,
        PixelSize pixelSize,
        ModelRasterTransform transform,
        CancellationToken cancellationToken);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly RenderCompleteFrameDelegate renderCompleteFrame;
    private readonly ILogger logger;
    private SKImage? latest;
    private int disposed;

    internal CpuFallbackRenderer(
        RenderCompleteFrameDelegate renderCompleteFrame,
        ILogger<CpuFallbackRenderer>? logger = null)
    {
        this.renderCompleteFrame = renderCompleteFrame
            ?? throw new ArgumentNullException(nameof(renderCompleteFrame));
        this.logger = logger ?? NullLogger<CpuFallbackRenderer>.Instance;
    }

    internal SKImage? Latest
    {
        get
        {
            lock (gate)
            {
                return latest;
            }
        }
    }

    internal async Task PrepareLatestAsync(
        ModelRenderFrame frame,
        PixelSize pixelSize,
        ModelRasterTransform transform,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeCancellation.Token);
        linked.Token.ThrowIfCancellationRequested();
        long startedAt = Stopwatch.GetTimestamp();
        SKImage prepared = await Task.Run(
                () => renderCompleteFrame(frame, pixelSize, transform, linked.Token),
                linked.Token)
            .ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            await gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                SKImage? previous = latest;
                latest = prepared;
                prepared = null!;
                previous?.Dispose();
            }
            finally
            {
                gate.Release();
            }

            CpuFallbackRendererLog.Prepared(
                logger,
                frame.Revision,
                pixelSize.Width,
                pixelSize.Height,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        finally
        {
            prepared?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetimeCancellation.Cancel();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            latest?.Dispose();
            latest = null;
        }
        finally
        {
            gate.Release();
        }

        gate.Dispose();
        lifetimeCancellation.Dispose();
    }
}

internal static partial class CpuFallbackRendererLog
{
    [LoggerMessage(7060, LogLevel.Debug,
        "CPU fallback frame prepared: revision {Revision}, size {Width}x{Height}, duration {DurationMs} ms")]
    internal static partial void Prepared(
        ILogger logger,
        long revision,
        int width,
        int height,
        double durationMs);
}
