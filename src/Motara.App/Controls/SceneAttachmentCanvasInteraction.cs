using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Motara.App.Input;
using Motara.App.Scenes;
using Motara.Persistence;
using Motara.Scene;

namespace Motara.App.Controls;

internal sealed class AttachmentTransformCommit(Guid sourceId, SceneTransform transform)
    : EventArgs
{
    internal Guid SourceId { get; } = sourceId != Guid.Empty
        ? sourceId
        : throw new ArgumentException("Source ID cannot be empty.", nameof(sourceId));

    internal SceneTransform Transform { get; } =
        transform ?? throw new ArgumentNullException(nameof(transform));
}

internal sealed class AttachmentAnchorSelectionRequested(Guid sourceId, Point point) : EventArgs
{
    internal Guid SourceId { get; } = sourceId != Guid.Empty
        ? sourceId
        : throw new ArgumentException("Source ID cannot be empty.", nameof(sourceId));

    internal Point Point { get; } = point;
}

internal sealed class AttachmentAnchorSelectorPreviewChanged(Guid sourceId, Point point) : EventArgs
{
    internal Guid SourceId { get; } = sourceId != Guid.Empty
        ? sourceId
        : throw new ArgumentException("Source ID cannot be empty.", nameof(sourceId));

    internal Point Point { get; } = point;
}

/// <summary>Handles direct manipulation of scene attachments without rebuilding playback.</summary>
internal sealed class SceneAttachmentCanvasInteraction : IDisposable
{
    private static readonly TimeSpan WheelCommitDelay = TimeSpan.FromMilliseconds(250);
    private readonly Control surface;
    private readonly SignalAttachmentScenePresenter presenter;
    private readonly InputActionRegistry inputActions;
    private readonly DispatcherTimer wheelCommitTimer;
    private SceneDocument? scene;
    private double referenceHeight = 1080;
    private SceneTransform? mainModelTransform;
    private SignalAttachmentVisual? activeVisual;
    private Guid? activeSourceId;
    private SceneTransform? currentWorldTransform;
    private SceneTransform? pendingStoredTransform;
    private Guid? anchorDragSourceId;
    private Point? anchorDragPoint;
    private IPointer? capturedPointer;
    private Point lastPointerPosition;
    private bool dragChanged;
    private int disposed;

    internal SceneAttachmentCanvasInteraction(
        Control surface,
        SignalAttachmentScenePresenter presenter,
        InputActionRegistry inputActions)
    {
        this.surface = surface ?? throw new ArgumentNullException(nameof(surface));
        this.presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        this.inputActions = inputActions ?? throw new ArgumentNullException(nameof(inputActions));
        wheelCommitTimer = new DispatcherTimer { Interval = WheelCommitDelay };
        wheelCommitTimer.Tick += OnWheelCommitTimerTick;
        surface.PointerPressed += OnPointerPressed;
        surface.PointerMoved += OnPointerMoved;
        surface.PointerReleased += OnPointerReleased;
        surface.PointerCaptureLost += OnPointerCaptureLost;
        surface.PointerWheelChanged += OnPointerWheelChanged;
    }

    internal event EventHandler<AttachmentTransformCommit>? CommitRequested;
    internal event EventHandler<AttachmentAnchorSelectionRequested>? AnchorSelectionRequested;
    internal event EventHandler<AttachmentAnchorSelectorPreviewChanged>? AnchorSelectorPreviewChanged;

    internal void Configure(SceneDocument? scene)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        CancelPendingInput();
        this.scene = scene;
        referenceHeight = scene?.ReferenceHeight is > 0 and var value ? value : 1080;
        mainModelTransform = scene?.MainModel?.Transform;
    }

    internal void UpdateMainModelTransformPreview(SceneTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        mainModelTransform = transform;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        CancelPendingInput();
        wheelCommitTimer.Tick -= OnWheelCommitTimerTick;
        surface.PointerPressed -= OnPointerPressed;
        surface.PointerMoved -= OnPointerMoved;
        surface.PointerReleased -= OnPointerReleased;
        surface.PointerCaptureLost -= OnPointerCaptureLost;
        surface.PointerWheelChanged -= OnPointerWheelChanged;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (args.Handled || !IsSurfaceSource(args.Source))
        {
            return;
        }

        PointerPoint point = args.GetCurrentPoint(surface);
        if (TryStartAnchorDrag(args, point))
        {
            return;
        }

        if (!point.Properties.IsLeftButtonPressed
            || !TryResolveMoveAction(args.KeyModifiers)
            || !presenter.TryGetTopmostVisual(
                point.Position,
                surface.Bounds.Size,
                out SignalAttachmentVisual? visual,
                includeBehindMainModel: scene?.MainModel?.IsVisible != true)
            || visual is null)
        {
            return;
        }

        if (visual.IsLocked
            || visual.MountMode == AttachmentMountMode.MainModelAnchor && mainModelTransform is null)
        {
            return;
        }

        activeVisual = visual;
        activeSourceId = visual.SourceId;
        currentWorldTransform = visual.Transform;
        pendingStoredTransform = null;
        lastPointerPosition = point.Position;
        dragChanged = false;
        capturedPointer = args.Pointer;
        capturedPointer.Capture(surface);
        args.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs args)
    {
        if (capturedPointer != args.Pointer)
        {
            return;
        }

        Point position = args.GetPosition(surface);
        Vector delta = position - lastPointerPosition;
        lastPointerPosition = position;
        if (delta.X == 0 && delta.Y == 0)
        {
            return;
        }

        if (anchorDragSourceId is Guid anchorSourceId)
        {
            UpdateAnchorDrag(anchorSourceId, delta);
            args.Handled = true;
            return;
        }

        if (currentWorldTransform is null)
        {
            return;
        }

        currentWorldTransform = MainModelTransformMath.Translate(
            currentWorldTransform,
            delta.X,
            delta.Y,
            surface.Bounds.Height,
            referenceHeight);
        pendingStoredTransform = ToStoredTransform(currentWorldTransform);
        presenter.UpdateAttachmentTransformPreview(activeSourceId!.Value, currentWorldTransform);
        dragChanged = true;
        args.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (capturedPointer != args.Pointer)
        {
            return;
        }

        if (anchorDragSourceId is not null)
        {
            FinishAnchorDrag();
            args.Handled = true;
            return;
        }

        FinishDrag(commit: true);
        args.Handled = true;
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs args)
    {
        if (capturedPointer is not null)
        {
            if (anchorDragSourceId is not null)
            {
                FinishAnchorDrag();
            }
            else
            {
                FinishDrag(commit: true);
            }
        }
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs args)
    {
        if (args.Handled
            || !IsSurfaceSource(args.Source)
            || !double.IsFinite(args.Delta.Y)
            || args.Delta.Y == 0
            || !presenter.TryGetTopmostVisual(
                args.GetPosition(surface),
                surface.Bounds.Size,
                out SignalAttachmentVisual? visual,
                includeBehindMainModel: scene?.MainModel?.IsVisible != true)
            || visual is null)
        {
            return;
        }

        if (visual.IsLocked)
        {
            return;
        }

        InputResolution? resolution = ResolveWheelAction(args);
        if (resolution is not { } action)
        {
            return;
        }

        SceneTransform current = visual.Transform;
        double magnitude = Math.Abs(args.Delta.Y);
        Point cursor = args.GetPosition(surface);
        SceneTransform updated = action.ActionId switch
        {
            BuiltInInputActions.CanvasScaleUp => MainModelTransformMath.ScaleAt(
                current, magnitude, cursor, surface.Bounds.Width, surface.Bounds.Height, referenceHeight),
            BuiltInInputActions.CanvasScaleDown => MainModelTransformMath.ScaleAt(
                current, -magnitude, cursor, surface.Bounds.Width, surface.Bounds.Height, referenceHeight),
            BuiltInInputActions.CanvasRotateLeft => MainModelTransformMath.Rotate(current, -magnitude),
            BuiltInInputActions.CanvasRotateRight => MainModelTransformMath.Rotate(current, magnitude),
            _ => current,
        };
        if (updated == current)
        {
            return;
        }

        activeVisual = visual;
        activeSourceId = visual.SourceId;
        currentWorldTransform = updated;
        pendingStoredTransform = ToStoredTransform(updated);
        presenter.UpdateAttachmentTransformPreview(visual.SourceId, updated);
        wheelCommitTimer.Stop();
        wheelCommitTimer.Start();
        args.Handled = action.ShouldConsume;
    }

    private InputResolution? ResolveWheelAction(PointerWheelEventArgs args)
    {
        InputDirection direction = args.Delta.Y > 0
            ? InputDirection.Positive
            : InputDirection.Negative;
        return inputActions.Resolve(
            new InputContext([InputBindingScope.Canvas], IsNativeControl: false),
            InputGesture.Wheel(
                InputAxis.Vertical,
                direction,
                ToInputModifiers(args.KeyModifiers)));
    }

    private bool TryResolveMoveAction(KeyModifiers modifiers) =>
        inputActions.Resolve(
            new InputContext([InputBindingScope.Canvas], IsNativeControl: false),
            InputGesture.MouseButton("Left", ToInputModifiers(modifiers)))?.ActionId
        == BuiltInInputActions.CanvasMoveModel;

    private SceneTransform ToStoredTransform(SceneTransform world)
    {
        if (activeSourceId is Guid sourceId
            && activeVisual?.MountMode == AttachmentMountMode.MainModelAnchor
            && presenter.TryGetAttachmentTransformParent(sourceId, out SceneTransform parent))
        {
            return AttachmentMountTransform.RelativeTo(world, parent);
        }

        return world;
    }

    private bool IsSurfaceSource(object? source) => ReferenceEquals(source, surface);

    private void FinishDrag(bool commit)
    {
        IPointer? pointer = capturedPointer;
        capturedPointer = null;
        pointer?.Capture(null);
        bool shouldCommit = commit && dragChanged;
        dragChanged = false;
        if (shouldCommit && activeSourceId is Guid sourceId && pendingStoredTransform is { } transform)
        {
            CommitRequested?.Invoke(this, new AttachmentTransformCommit(sourceId, transform));
        }

        activeVisual = null;
        activeSourceId = null;
        currentWorldTransform = null;
        pendingStoredTransform = null;
    }

    private void OnWheelCommitTimerTick(object? sender, EventArgs args)
    {
        wheelCommitTimer.Stop();
        if (activeSourceId is Guid sourceId && pendingStoredTransform is { } transform)
        {
            CommitRequested?.Invoke(this, new AttachmentTransformCommit(sourceId, transform));
        }

        activeVisual = null;
        activeSourceId = null;
        currentWorldTransform = null;
        pendingStoredTransform = null;
    }

    private void CancelPendingInput()
    {
        wheelCommitTimer.Stop();
        if (anchorDragSourceId is not null)
        {
            FinishAnchorDrag(commit: false);
        }
        FinishDrag(commit: false);
    }

    private bool TryStartAnchorDrag(PointerPressedEventArgs args, PointerPoint point)
    {
        if (!args.KeyModifiers.HasFlag(KeyModifiers.Control)
            || !point.Properties.IsLeftButtonPressed
            || !presenter.TryGetTopmostAttachmentAnchorSelector(
                point.Position,
                surface.Bounds.Size,
                144,
                out Guid sourceId,
                out _))
        {
            return false;
        }

        anchorDragSourceId = sourceId;
        anchorDragPoint = point.Position;
        lastPointerPosition = point.Position;
        capturedPointer = args.Pointer;
        capturedPointer.Capture(surface);
        args.Handled = true;
        return true;
    }

    private void UpdateAnchorDrag(Guid sourceId, Vector delta)
    {
        if (anchorDragPoint is not { } currentPoint)
        {
            return;
        }

        Point nextPoint = currentPoint + delta;
        anchorDragPoint = nextPoint;
        AnchorSelectorPreviewChanged?.Invoke(
            this,
            new AttachmentAnchorSelectorPreviewChanged(sourceId, nextPoint));
    }

    private void FinishAnchorDrag(bool commit = true)
    {
        IPointer? pointer = capturedPointer;
        capturedPointer = null;
        pointer?.Capture(null);
        Guid? sourceId = anchorDragSourceId;
        anchorDragSourceId = null;
        Point? point = anchorDragPoint;
        anchorDragPoint = null;
        if (!commit || sourceId is null || point is null)
        {
            return;
        }

        AnchorSelectionRequested?.Invoke(
            this,
            new AttachmentAnchorSelectionRequested(sourceId.Value, point.Value));
    }

    private static InputModifiers ToInputModifiers(KeyModifiers modifiers)
    {
        InputModifiers result = InputModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control)) result |= InputModifiers.Control;
        if (modifiers.HasFlag(KeyModifiers.Alt)) result |= InputModifiers.Alt;
        if (modifiers.HasFlag(KeyModifiers.Shift)) result |= InputModifiers.Shift;
        if (modifiers.HasFlag(KeyModifiers.Meta)) result |= InputModifiers.Meta;
        return result;
    }
}
