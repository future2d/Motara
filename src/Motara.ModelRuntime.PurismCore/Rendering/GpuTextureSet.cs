using System.Collections.Immutable;
using SkiaSharp;

namespace Motara.ModelRuntime.PurismCore;

internal sealed class GpuTextureSet : IDisposable, IGpuRetirementResource, IModelTextureShaderSource
{
    private readonly ImmutableArray<SKImage> images;
    private readonly ImmutableArray<SKShader> shaders;
    private int contextAbandoned;
    private int disposed;

    private GpuTextureSet(
        GRContext context,
        ImmutableArray<SKImage> images,
        ImmutableArray<SKShader> shaders,
        long estimatedBytes)
    {
        Context = context;
        this.images = images;
        this.shaders = shaders;
        EstimatedBytes = estimatedBytes;
    }

    internal GRContext Context { get; }

    internal int Count => images.Length;

    internal long EstimatedBytes { get; }

    object IGpuRetirementResource.ContextIdentity => Context;

    bool IGpuRetirementResource.IsContextAbandoned =>
        Volatile.Read(ref contextAbandoned) != 0;

    int IGpuRetirementResource.ResourceCount => images.Length + shaders.Length;

    long IGpuRetirementResource.EstimatedBytes => EstimatedBytes;

    internal SKShader GetShader(int index) => shaders[index];

    internal void MarkContextAbandoned() =>
        Interlocked.Exchange(ref contextAbandoned, 1);

    SKShader IModelTextureShaderSource.GetShader(int index) => GetShader(index);

    internal static GpuTextureSet Create(GRContext context, CpuTextureSet textures)
    {
        ArgumentNullException.ThrowIfNull(textures);
        return CreateCore(context, textures.Count, textures.GetBitmap, textures.EstimatedBytes);
    }

    private static GpuTextureSet CreateCore(
        GRContext context,
        int textureCount,
        Func<int, SKBitmap> getBitmap,
        long estimatedBytes)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(getBitmap);
        var images = ImmutableArray.CreateBuilder<SKImage>(textureCount);
        var shaders = ImmutableArray.CreateBuilder<SKShader>(textureCount);
        try
        {
            for (int index = 0; index < textureCount; index++)
            {
                SKBitmap bitmap = getBitmap(index);
                using SKSurface stage = SKSurface.Create(
                    context,
                    budgeted: false,
                    bitmap.Info,
                    sampleCount: 1,
                    GRSurfaceOrigin.TopLeft)
                    ?? throw new InvalidOperationException("GPU texture staging surface is unavailable.");
                stage.Canvas.DrawBitmap(bitmap, 0, 0);
                stage.Canvas.Flush();
                SKImage image = stage.Snapshot();
                images.Add(image);
                shaders.Add(SKShader.CreateImage(
                    image,
                    SKShaderTileMode.Clamp,
                    SKShaderTileMode.Clamp));
            }

            GpuTextureUploadSynchronizer.Complete(new SkiaGpuUploadContext(context));
            return new GpuTextureSet(
                context,
                images.MoveToImmutable(),
                shaders.MoveToImmutable(),
                estimatedBytes);
        }
        catch
        {
            foreach (SKShader shader in shaders)
            {
                shader.Dispose();
            }

            foreach (SKImage image in images)
            {
                image.Dispose();
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        foreach (SKShader shader in shaders)
        {
            shader.Dispose();
        }

        foreach (SKImage image in images)
        {
            image.Dispose();
        }
    }

    void IGpuRetirementResource.DisposeOnGpuThread() => Dispose();
}

internal interface IGpuUploadContext
{
    void Flush(bool submit, bool synchronous);
}

internal static class GpuTextureUploadSynchronizer
{
    internal static void Complete(IGpuUploadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Submit the recorded upload without blocking the caller on a GPU fence.
        context.Flush(submit: true, synchronous: false);
    }
}

internal sealed class SkiaGpuUploadContext(GRContext context) : IGpuUploadContext
{
    private readonly GRContext context = context ?? throw new ArgumentNullException(nameof(context));

    public void Flush(bool submit, bool synchronous) => context.Flush(submit, synchronous);
}
