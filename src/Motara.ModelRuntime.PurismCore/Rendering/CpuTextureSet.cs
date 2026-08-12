using System.Collections.Immutable;
using SkiaSharp;

namespace Motara.ModelRuntime.PurismCore;

internal sealed class CpuTextureSet : IDisposable, IModelTextureShaderSource
{
    private readonly ImmutableArray<SKBitmap> bitmaps;
    private readonly ImmutableArray<SKShader> shaders;
    private int disposed;

    private CpuTextureSet(
        ImmutableArray<SKBitmap> bitmaps,
        ImmutableArray<SKShader> shaders,
        long estimatedBytes)
    {
        this.bitmaps = bitmaps;
        this.shaders = shaders;
        EstimatedBytes = estimatedBytes;
    }

    internal int Count => bitmaps.Length;

    internal long EstimatedBytes { get; }

    internal SKBitmap GetBitmap(int index)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return bitmaps[index];
    }

    internal SKShader GetShader(int index)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return shaders[index];
    }

    SKShader IModelTextureShaderSource.GetShader(int index) => GetShader(index);

    internal SKImageInfo GetInfo(int index)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return bitmaps[index].Info;
    }

    internal static CpuTextureSet Create(ImmutableArray<SKBitmap> bitmaps)
    {
        if (bitmaps.IsDefault)
        {
            throw new ArgumentException("CPU texture bitmaps must be initialized.", nameof(bitmaps));
        }

        var shaders = ImmutableArray.CreateBuilder<SKShader>(bitmaps.Length);
        try
        {
            long estimatedBytes = 0;
            foreach (SKBitmap bitmap in bitmaps)
            {
                ArgumentNullException.ThrowIfNull(bitmap);
                shaders.Add(SKShader.CreateBitmap(
                    bitmap,
                    SKShaderTileMode.Clamp,
                    SKShaderTileMode.Clamp));
                estimatedBytes = checked(estimatedBytes + bitmap.ByteCount);
            }

            return new CpuTextureSet(bitmaps, shaders.MoveToImmutable(), estimatedBytes);
        }
        catch
        {
            foreach (SKShader shader in shaders)
            {
                shader.Dispose();
            }

            foreach (SKBitmap bitmap in bitmaps)
            {
                bitmap.Dispose();
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

        foreach (SKBitmap bitmap in bitmaps)
        {
            bitmap.Dispose();
        }
    }
}
