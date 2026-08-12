using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Motara.App.Input;
using Motara.Persistence;
using Motara.Scene;

namespace Motara.App.Controls;

internal static class MainModelTransformMath
{
    internal const double MinimumScale = 0.1;
    internal const double MaximumScale = 8;
    private const double WheelScaleFactor = 1.1;
    private const double WheelRotationDegrees = 5;

    internal static SceneTransform Translate(
        SceneTransform transform,
        double deltaX,
        double deltaY,
        double viewportHeight,
        double referenceHeight)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (!double.IsFinite(deltaX)
            || !double.IsFinite(deltaY)
            || !double.IsFinite(viewportHeight)
            || viewportHeight <= 0
            || !double.IsFinite(referenceHeight)
            || referenceHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportHeight));
        }

        double logicalUnitsPerDip = referenceHeight / viewportHeight;
        return new SceneTransform(
            transform.X + deltaX * logicalUnitsPerDip,
            transform.Y + deltaY * logicalUnitsPerDip,
            transform.Scale,
            transform.RotationDegrees);
    }

    internal static SceneTransform Scale(SceneTransform transform, double wheelSteps)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (!double.IsFinite(wheelSteps))
        {
            throw new ArgumentOutOfRangeException(nameof(wheelSteps));
        }

        double factor = Math.Pow(WheelScaleFactor, Math.Clamp(wheelSteps, -100, 100));
        double scale = Math.Clamp(
            transform.Scale * factor,
            MinimumScale,
            MaximumScale);
        return new SceneTransform(
            transform.X,
            transform.Y,
            scale,
            transform.RotationDegrees);
    }

    internal static SceneTransform ScaleAt(
        SceneTransform transform,
        double wheelSteps,
        Point cursor,
        double viewportWidth,
        double viewportHeight,
        double referenceHeight)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (!double.IsFinite(cursor.X)
            || !double.IsFinite(cursor.Y)
            || !double.IsFinite(viewportWidth)
            || viewportWidth <= 0
            || !double.IsFinite(viewportHeight)
            || viewportHeight <= 0
            || !double.IsFinite(referenceHeight)
            || referenceHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportHeight));
        }

        SceneTransform scaled = Scale(transform, wheelSteps);
        double actualScaleFactor = scaled.Scale / transform.Scale;
        double logicalUnitsPerDip = referenceHeight / viewportHeight;
        double translationX = transform.X / logicalUnitsPerDip;
        double translationY = transform.Y / logicalUnitsPerDip;
        double anchorOffsetX = cursor.X - (viewportWidth / 2) - translationX;
        double anchorOffsetY = cursor.Y - (viewportHeight / 2) - translationY;
        return new SceneTransform(
            transform.X + ((1 - actualScaleFactor) * anchorOffsetX * logicalUnitsPerDip),
            transform.Y + ((1 - actualScaleFactor) * anchorOffsetY * logicalUnitsPerDip),
            scaled.Scale,
            transform.RotationDegrees);
    }

    internal static SceneTransform Rotate(SceneTransform transform, double wheelSteps)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (!double.IsFinite(wheelSteps))
        {
            throw new ArgumentOutOfRangeException(nameof(wheelSteps));
        }

        double angle = NormalizeDegrees(
            transform.RotationDegrees + wheelSteps * WheelRotationDegrees);
        return new SceneTransform(transform.X, transform.Y, transform.Scale, angle);
    }

    private static double NormalizeDegrees(double value)
    {
        double normalized = (value + 180) % 360;
        if (normalized < 0)
        {
            normalized += 360;
        }

        return normalized - 180;
    }
}

internal sealed class MainModelTransformCommit(Guid sourceId, SceneTransform transform)
    : EventArgs
{
    internal Guid SourceId { get; } = sourceId != Guid.Empty
        ? sourceId
        : throw new ArgumentException("Source ID cannot be empty.", nameof(sourceId));

    internal SceneTransform Transform { get; } =
        transform ?? throw new ArgumentNullException(nameof(transform));
}

internal sealed class MainModelTransformPreview(Guid sourceId, SceneTransform transform)
    : EventArgs
{
    internal Guid SourceId { get; } = sourceId != Guid.Empty
        ? sourceId
        : throw new ArgumentException("Source ID cannot be empty.", nameof(sourceId));

    internal SceneTransform Transform { get; } =
        transform ?? throw new ArgumentNullException(nameof(transform));
}

internal sealed class MainModelDragPhysicsInput(
    Guid sourceId,
    double normalizedX,
    double normalizedY) : EventArgs
{
    internal Guid SourceId { get; } = sourceId != Guid.Empty
        ? sourceId
        : throw new ArgumentException("Source ID cannot be empty.", nameof(sourceId));

    internal double NormalizedX { get; } = double.IsFinite(normalizedX)
        ? normalizedX
        : throw new ArgumentOutOfRangeException(nameof(normalizedX));

    internal double NormalizedY { get; } = double.IsFinite(normalizedY)
        ? normalizedY
        : throw new ArgumentOutOfRangeException(nameof(normalizedY));
}

internal sealed class MainModelCanvasInteraction : IDisposable
{
    private static readonly TimeSpan WheelCommitDelay = TimeSpan.FromMilliseconds(250);
    private readonly Control surface;
    private readonly ModelCanvas modelCanvas;
    private readonly InputActionRegistry inputActions;
    private readonly DispatcherTimer wheelCommitTimer;
    private Guid? sourceId;
    private SceneTransform? currentTransform;
    private double referenceHeight = 1080;
    private bool isInteractionEnabled;
    private IPointer? capturedPointer;
    private Point lastPointerPosition;
    private bool dragChanged;
    private int disposed;

    internal MainModelCanvasInteraction(
        Control surface,
        ModelCanvas modelCanvas,
        InputActionRegistry inputActions)
    {
        this.surface = surface ?? throw new ArgumentNullException(nameof(surface));
        this.modelCanvas = modelCanvas ?? throw new ArgumentNullException(nameof(modelCanvas));
        this.inputActions = inputActions ?? throw new ArgumentNullException(nameof(inputActions));
        wheelCommitTimer = new DispatcherTimer { Interval = WheelCommitDelay };
        wheelCommitTimer.Tick += OnWheelCommitTimerTick;
        surface.PointerPressed += OnPointerPressed;
        surface.PointerMoved += OnPointerMoved;
        surface.PointerReleased += OnPointerReleased;
        surface.PointerCaptureLost += OnPointerCaptureLost;
        surface.PointerWheelChanged += OnPointerWheelChanged;
    }

    internal event EventHandler<MainModelTransformCommit>? CommitRequested;

    internal event EventHandler<MainModelTransformPreview>? PreviewChanged;

    internal event EventHandler<MainModelDragPhysicsInput>? DragPhysicsInputRequested;

    internal SceneTransform? CurrentTransform => currentTransform;

    internal void Configure(MainModelInstance? mainModel, double referenceHeight)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!double.IsFinite(referenceHeight) || referenceHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(referenceHeight));
        }

        CancelPendingInput();
        this.referenceHeight = referenceHeight;
        sourceId = mainModel?.SourceId;
        currentTransform = mainModel?.Transform;
        isInteractionEnabled = mainModel is { IsLocked: false, IsVisible: true };
        modelCanvas.SetSceneTransform(
            currentTransform ?? SceneTransform.Default,
            referenceHeight);
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
        if (args.Handled || !CanHandle(args.Source) || currentTransform is null)
        {
            return;
        }

        PointerPoint point = args.GetCurrentPoint(surface);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        InputResolution? resolution = inputActions.Resolve(
            new InputContext([InputBindingScope.Canvas], IsNativeControl: false),
            InputGesture.MouseButton("Left", ToInputModifiers(args.KeyModifiers)));
        if (resolution?.ActionId != BuiltInInputActions.CanvasMoveModel)
        {
            return;
        }

        capturedPointer = args.Pointer;
        lastPointerPosition = point.Position;
        dragChanged = false;
        capturedPointer.Capture(surface);
        args.Handled = resolution.Value.ShouldConsume;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs args)
    {
        if (capturedPointer != args.Pointer || currentTransform is null)
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

        currentTransform = MainModelTransformMath.Translate(
            currentTransform,
            delta.X,
            delta.Y,
            surface.Bounds.Height,
            referenceHeight);
        dragChanged = true;
        modelCanvas.SetSceneTransform(currentTransform, referenceHeight);
        if (sourceId is Guid previewId)
        {
            PreviewChanged?.Invoke(this, new MainModelTransformPreview(previewId, currentTransform));
        }
        if (sourceId is Guid id && surface.Bounds.Height > 0)
        {
            DragPhysicsInputRequested?.Invoke(
                this,
                new MainModelDragPhysicsInput(
                    id,
                    delta.X / surface.Bounds.Height,
                    delta.Y / surface.Bounds.Height));
        }
        args.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (capturedPointer != args.Pointer)
        {
            return;
        }

        FinishDrag(commit: true);
        args.Handled = true;
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs args)
    {
        if (capturedPointer is not null)
        {
            FinishDrag(commit: true);
        }
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs args)
    {
        if (args.Handled
            || !CanHandle(args.Source)
            || currentTransform is null
            || !double.IsFinite(args.Delta.Y)
            || args.Delta.Y == 0)
        {
            return;
        }

        InputDirection direction = args.Delta.Y > 0
            ? InputDirection.Positive
            : InputDirection.Negative;
        InputResolution? resolution = inputActions.Resolve(
            new InputContext([InputBindingScope.Canvas], IsNativeControl: false),
            InputGesture.Wheel(
                InputAxis.Vertical,
                direction,
                ToInputModifiers(args.KeyModifiers)));
        if (resolution is not { } action)
        {
            return;
        }

        double magnitude = Math.Abs(args.Delta.Y);
        Point cursor = args.GetPosition(surface);
        currentTransform = action.ActionId switch
        {
            BuiltInInputActions.CanvasScaleUp =>
                MainModelTransformMath.ScaleAt(
                    currentTransform,
                    magnitude,
                    cursor,
                    surface.Bounds.Width,
                    surface.Bounds.Height,
                    referenceHeight),
            BuiltInInputActions.CanvasScaleDown =>
                MainModelTransformMath.ScaleAt(
                    currentTransform,
                    -magnitude,
                    cursor,
                    surface.Bounds.Width,
                    surface.Bounds.Height,
                    referenceHeight),
            BuiltInInputActions.CanvasRotateLeft =>
                MainModelTransformMath.Rotate(currentTransform, -magnitude),
            BuiltInInputActions.CanvasRotateRight =>
                MainModelTransformMath.Rotate(currentTransform, magnitude),
            _ => currentTransform,
        };
        if (action.ActionId is not (BuiltInInputActions.CanvasScaleUp
                or BuiltInInputActions.CanvasScaleDown
                or BuiltInInputActions.CanvasRotateLeft
                or BuiltInInputActions.CanvasRotateRight))
        {
            return;
        }

        modelCanvas.SetSceneTransform(currentTransform, referenceHeight);
        if (sourceId is Guid previewId)
        {
            PreviewChanged?.Invoke(this, new MainModelTransformPreview(previewId, currentTransform));
        }
        wheelCommitTimer.Stop();
        wheelCommitTimer.Start();
        args.Handled = action.ShouldConsume;
    }

    private bool CanHandle(object? inputSource)
    {
        if (!isInteractionEnabled
            || !sourceId.HasValue
            || !IsCanvasInputSource(inputSource, surface))
        {
            return false;
        }

        return true;
    }

    internal static bool IsCanvasInputSource(object? inputSource, Control surface) =>
        ReferenceEquals(inputSource, surface);

    private void FinishDrag(bool commit)
    {
        IPointer? pointer = capturedPointer;
        capturedPointer = null;
        pointer?.Capture(null);
        bool shouldCommit = commit && dragChanged;
        dragChanged = false;
        if (shouldCommit)
        {
            PublishCommit();
        }
    }

    private void OnWheelCommitTimerTick(object? sender, EventArgs args)
    {
        wheelCommitTimer.Stop();
        PublishCommit();
    }

    private void PublishCommit()
    {
        if (sourceId is Guid id && currentTransform is { } transform)
        {
            CommitRequested?.Invoke(this, new MainModelTransformCommit(id, transform));
        }
    }

    private void CancelPendingInput()
    {
        wheelCommitTimer.Stop();
        FinishDrag(commit: false);
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
