using System.Diagnostics;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Motara.App.Rendering;

internal enum GpuCompositionProbeSupport
{
    Supported = 0,
    MissingTextureSharingFeature = 1,
    SharedContextUnavailable = 2,
    CompositionInteropUnavailable = 3,
}

internal static class GpuCompositionInteropProbe
{
    private static readonly PixelSize ProbeSize = new(16, 16);

    internal static GpuCompositionProbeSupport EvaluateSupport(
        bool hasTextureSharingFeature,
        bool canCreateSharedContext,
        bool hasCompositionInterop)
    {
        if (!hasTextureSharingFeature)
        {
            return GpuCompositionProbeSupport.MissingTextureSharingFeature;
        }

        if (!canCreateSharedContext)
        {
            return GpuCompositionProbeSupport.SharedContextUnavailable;
        }

        return hasCompositionInterop
            ? GpuCompositionProbeSupport.Supported
            : GpuCompositionProbeSupport.CompositionInteropUnavailable;
    }

    internal static async Task<GpuCompositionProbeSupport> RunAsync(
        Visual host,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(logger);
        cancellationToken.ThrowIfCancellationRequested();

        CompositionVisual? elementVisual = ElementComposition.GetElementVisual(host);
        if (elementVisual is null)
        {
            GpuCompositionProbeLog.Unavailable(
                logger,
                GpuCompositionProbeSupport.CompositionInteropUnavailable);
            return GpuCompositionProbeSupport.CompositionInteropUnavailable;
        }

        Compositor compositor = elementVisual.Compositor;
        object? sharingFeature = await compositor.TryGetRenderInterfaceFeature(
            typeof(IOpenGlTextureSharingRenderInterfaceContextFeature));
        var textureSharing = sharingFeature as IOpenGlTextureSharingRenderInterfaceContextFeature;
        ICompositionGpuInterop? gpuInterop = await compositor.TryGetCompositionGpuInterop();
        GpuCompositionProbeSupport support = EvaluateSupport(
            textureSharing is not null,
            textureSharing?.CanCreateSharedContext == true,
            gpuInterop is not null);
        if (support != GpuCompositionProbeSupport.Supported)
        {
            GpuCompositionProbeLog.Unavailable(logger, support);
            return support;
        }

        GpuCompositionProbeLog.Started(
            logger,
            string.Join(',', gpuInterop!.SupportedImageHandleTypes));
        long startedAt = Stopwatch.GetTimestamp();
        using CompositionDrawingSurface drawingSurface = compositor.CreateDrawingSurface();
        try
        {
            await RunWorkerAsync(
                (invokeOnUiThread, workerCancellationToken) => RunCoreAsync(
                    textureSharing!,
                    gpuInterop,
                    drawingSurface,
                    invokeOnUiThread,
                    workerCancellationToken),
                operation => Dispatcher.UIThread.InvokeAsync(
                    operation,
                    DispatcherPriority.Render),
                cancellationToken);
            GpuCompositionProbeLog.Completed(
                logger,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            return GpuCompositionProbeSupport.Supported;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            GpuCompositionProbeLog.Canceled(logger);
            throw;
        }
        catch (Exception exception)
        {
            GpuCompositionProbeLog.Failed(
                logger,
                exception,
                exception.GetType().Name,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            return GpuCompositionProbeSupport.CompositionInteropUnavailable;
        }
    }

    internal static Task RunWorkerAsync(
        Func<Func<Func<Task>, Task>, CancellationToken, Task> worker,
        Func<Func<Task>, Task> invokeOnUiThread,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(invokeOnUiThread);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => worker(invokeOnUiThread, cancellationToken),
            cancellationToken);
    }

    private static async Task RunCoreAsync(
        IOpenGlTextureSharingRenderInterfaceContextFeature textureSharing,
        ICompositionGpuInterop gpuInterop,
        CompositionDrawingSurface drawingSurface,
        Func<Func<Task>, Task> invokeOnUiThread,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using IGlContext sharedContext = textureSharing.CreateSharedContext()
            ?? throw new InvalidOperationException("A shared OpenGL context could not be created.");
        using ICompositionImportableOpenGlSharedTexture sharedTexture =
            textureSharing.CreateSharedTextureForComposition(sharedContext, ProbeSize);

        using (sharedContext.EnsureCurrent())
        using (GRGlInterface glInterface = CreateGlInterface(sharedContext))
        using (GRContext grContext = GRContext.CreateGl(glInterface)
            ?? throw new InvalidOperationException("A shared Skia GPU context could not be created."))
        using (var backendTexture = new GRBackendTexture(
            ProbeSize.Width,
            ProbeSize.Height,
            mipmapped: false,
            new GRGlTextureInfo(
                target: 0x0DE1,
                id: (uint)sharedTexture.TextureId,
                format: (uint)sharedTexture.InternalFormat)))
        using (SKSurface surface = SKSurface.Create(
            grContext,
            backendTexture,
            GRSurfaceOrigin.TopLeft,
            SKColorType.Rgba8888)
            ?? throw new InvalidOperationException("The shared Skia surface could not be created."))
        {
            surface.Canvas.Clear(SKColors.Magenta);
            surface.Canvas.Flush();
            grContext.Flush(submit: true, synchronous: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await invokeOnUiThread(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using ICompositionImportedGpuImage importedImage =
                gpuInterop.ImportImage(sharedTexture);
            await importedImage.ImportCompleted.WaitAsync(cancellationToken);
            await drawingSurface.UpdateAsync(importedImage).WaitAsync(cancellationToken);
        });
    }

    private static GRGlInterface CreateGlInterface(IGlContext context)
    {
        GRGlGetProcedureAddressDelegate getProcedureAddress =
            procedure => context.GlInterface.GetProcAddress(procedure);
        return context.Version.Type == GlProfileType.OpenGL
            ? GRGlInterface.CreateOpenGl(getProcedureAddress)
            : GRGlInterface.CreateGles(getProcedureAddress);
    }
}

internal static partial class GpuCompositionProbeLog
{
    [LoggerMessage(
        6292,
        LogLevel.Information,
        "GPU composition interop probe started; supported external image handles: {SupportedImageHandleTypes}")]
    internal static partial void Started(ILogger logger, string supportedImageHandleTypes);

    [LoggerMessage(
        6293,
        LogLevel.Information,
        "GPU composition interop probe completed in {DurationMs} ms")]
    internal static partial void Completed(ILogger logger, double durationMs);

    [LoggerMessage(
        6294,
        LogLevel.Information,
        "GPU composition interop probe is unavailable because {Support}")]
    internal static partial void Unavailable(ILogger logger, GpuCompositionProbeSupport support);

    [LoggerMessage(
        6295,
        LogLevel.Warning,
        "GPU composition interop probe failed with {ExceptionType} after {DurationMs} ms")]
    internal static partial void Failed(
        ILogger logger,
        Exception exception,
        string exceptionType,
        double durationMs);

    [LoggerMessage(6296, LogLevel.Debug, "GPU composition interop probe canceled")]
    internal static partial void Canceled(ILogger logger);
}
