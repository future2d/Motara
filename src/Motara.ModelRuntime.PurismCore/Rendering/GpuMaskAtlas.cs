using System.Collections.Immutable;
using System.Numerics;
using Motara.ModelRuntime.Abstractions;

namespace Motara.ModelRuntime.PurismCore;

internal readonly struct MaskKey : IEquatable<MaskKey>
{
    internal MaskKey(ImmutableArray<int> sources) => Sources = sources;

    internal ImmutableArray<int> Sources { get; }

    public bool Equals(MaskKey other) => Sources.AsSpan().SequenceEqual(other.Sources.AsSpan());

    public override bool Equals(object? obj) => obj is MaskKey other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (int source in Sources)
        {
            hash.Add(source);
        }

        return hash.ToHashCode();
    }
}

internal readonly record struct GpuMaskRegion(
    int X,
    int Y,
    int Width,
    int Height,
    Vector4 UvTransform);

internal sealed class GpuMaskAtlas : IDisposable
{
    private const int CellSize = 256;
    private int disposed;

    private GpuMaskAtlas(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, CellSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, CellSize);
        Width = width;
        Height = height;
    }

    internal int Width { get; private set; }

    internal int Height { get; private set; }

    internal ImmutableDictionary<MaskKey, GpuMaskRegion> Regions { get; private set; } =
        ImmutableDictionary<MaskKey, GpuMaskRegion>.Empty;

    internal static GpuMaskAtlas CreateForTest(int width, int height) => new(width, height);

    internal void UpdateLayout(ModelRenderFrame frame)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(frame);
        var keys = frame.Drawables
            .Where(static drawable => !drawable.Masks.IsEmpty)
            .Select(static drawable => new MaskKey(
                drawable.Masks.Order().ToImmutableArray()))
            .Distinct()
            .ToImmutableArray();
        Regions = PackStable(keys, Regions);
    }

    private ImmutableDictionary<MaskKey, GpuMaskRegion> PackStable(
        ImmutableArray<MaskKey> keys,
        ImmutableDictionary<MaskKey, GpuMaskRegion> existing)
    {
        var result = ImmutableDictionary.CreateBuilder<MaskKey, GpuMaskRegion>();
        var used = new HashSet<(int X, int Y)>();
        foreach (MaskKey key in keys)
        {
            if (existing.TryGetValue(key, out GpuMaskRegion region))
            {
                result[key] = region;
                used.Add((region.X, region.Y));
            }
        }

        foreach (MaskKey key in keys)
        {
            if (result.ContainsKey(key))
            {
                continue;
            }

            (int x, int y) = FindFreeCell(used);
            used.Add((x, y));
            result[key] = CreateRegion(x, y);
        }

        return result.ToImmutable();
    }

    private (int X, int Y) FindFreeCell(HashSet<(int X, int Y)> used)
    {
        while (true)
        {
            for (int y = 0; y + CellSize <= Height; y += CellSize)
            {
                for (int x = 0; x + CellSize <= Width; x += CellSize)
                {
                    if (!used.Contains((x, y)))
                    {
                        return (x, y);
                    }
                }
            }

            Height = checked(Height * 2);
        }
    }

    private GpuMaskRegion CreateRegion(int x, int y) => new(
        x,
        y,
        CellSize,
        CellSize,
        new Vector4(
            CellSize / (float)Width,
            CellSize / (float)Height,
            x / (float)Width,
            y / (float)Height));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            Regions = ImmutableDictionary<MaskKey, GpuMaskRegion>.Empty;
        }
    }
}
