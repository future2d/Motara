using Motara.ModelRuntime.Abstractions;

namespace Motara.ModelRuntime.PurismCore;

internal readonly record struct GpuResourceIdentity(
    object TextureSet,
    object ShaderProgram,
    int VertexCapacity,
    int IndexCapacity);

internal sealed class GpuModelResources : IDisposable
{
    private readonly object textureSet;
    private int disposed;

    private GpuModelResources(ModelRenderFrame initialFrame)
    {
        textureSet = new object();
        MaskAtlas = GpuMaskAtlas.CreateForTest(1024, 1024);
        Shader = new GpuModelShader();
        EnsureGeometryCapacity(initialFrame);
        UpdateFrame(initialFrame, ModelRasterTransform.Identity);
    }

    internal int VertexCapacity { get; private set; }

    internal int IndexCapacity { get; private set; }

    internal GpuMaskAtlas MaskAtlas { get; }

    internal GpuModelShader Shader { get; }

    internal GpuResourceIdentity Identity => new(
        textureSet,
        Shader,
        VertexCapacity,
        IndexCapacity);

    internal static GpuModelResources CreateForTest(ModelRenderFrame initialFrame)
    {
        ArgumentNullException.ThrowIfNull(initialFrame);
        return new GpuModelResources(initialFrame);
    }

    internal void UpdateFrame(ModelRenderFrame frame, ModelRasterTransform transform)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (!transform.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(transform));
        }

        EnsureGeometryCapacity(frame);
        MaskAtlas.UpdateLayout(frame);
        Shader.SetTransform(transform);
    }

    private void EnsureGeometryCapacity(ModelRenderFrame frame)
    {
        int vertices = frame.Drawables.Sum(static drawable => drawable.Vertices.Length);
        int indices = frame.Drawables.Sum(static drawable => drawable.Indices.Length);
        VertexCapacity = GrowCapacity(VertexCapacity, vertices);
        IndexCapacity = GrowCapacity(IndexCapacity, indices);
    }

    private static int GrowCapacity(int current, int required)
    {
        if (required <= current)
        {
            return current;
        }

        int capacity = Math.Max(1, current);
        while (capacity < required)
        {
            capacity = checked(capacity * 2);
        }

        return capacity;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        MaskAtlas.Dispose();
        Shader.Dispose();
    }
}
