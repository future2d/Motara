using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;

namespace Motara.App.Rendering;

internal sealed class PresentationTrackingDrawOperation(
    ICustomDrawOperation inner,
    Action completed) : ICustomDrawOperation
{
    private ICustomDrawOperation? inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly Action completed = completed ?? throw new ArgumentNullException(nameof(completed));

    public Rect Bounds => GetInner().Bounds;

    public bool HitTest(Point point) => GetInner().HitTest(point);

    public void Render(ImmediateDrawingContext context)
    {
        GetInner().Render(context);
        completed();
    }

    public bool Equals(ICustomDrawOperation? other) =>
        other is PresentationTrackingDrawOperation operation
        && Equals(Volatile.Read(ref inner), Volatile.Read(ref operation.inner))
        && ReferenceEquals(completed, operation.completed);

    public void Dispose() => Interlocked.Exchange(ref inner, null)?.Dispose();

    private ICustomDrawOperation GetInner() => Volatile.Read(ref inner)
        ?? throw new ObjectDisposedException(nameof(PresentationTrackingDrawOperation));
}
