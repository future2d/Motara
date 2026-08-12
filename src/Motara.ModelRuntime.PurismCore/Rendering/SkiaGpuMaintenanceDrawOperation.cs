using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;

namespace Motara.ModelRuntime.PurismCore;

internal sealed class SkiaGpuMaintenanceDrawOperation(
    GpuResourceRetirementQueue queue,
    Rect bounds) : ICustomDrawOperation
{
    private readonly GpuResourceRetirementQueue queue = queue
        ?? throw new ArgumentNullException(nameof(queue));

    public Rect Bounds { get; } = bounds;

    public bool HitTest(Point point) => false;

    public void Render(ImmediateDrawingContext context)
    {
        ISkiaSharpApiLeaseFeature? leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (leaseFeature is null)
        {
            queue.DrainAbandoned();
            return;
        }

        using ISkiaSharpApiLease lease = leaseFeature.Lease();
        if (lease.GrContext is not null)
        {
            queue.Drain(lease.GrContext);
            return;
        }

        queue.DrainAbandoned();
    }

    public bool Equals(ICustomDrawOperation? other) =>
        other is SkiaGpuMaintenanceDrawOperation operation
        && ReferenceEquals(queue, operation.queue)
        && Bounds == operation.Bounds;

    public void Dispose()
    {
    }
}
