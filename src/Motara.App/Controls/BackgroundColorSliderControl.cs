using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Motara.App.Controls;

internal sealed class BackgroundColorSliderControl : Control
{
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<BackgroundColorSliderControl, double>(nameof(Minimum));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<BackgroundColorSliderControl, double>(nameof(Maximum), 255);
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<BackgroundColorSliderControl, double>(nameof(Value));
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<BackgroundColorSliderControl, IBrush?>(nameof(TrackBrush));
    private IPointer? pointer;

    static BackgroundColorSliderControl()
    {
        AffectsRender<BackgroundColorSliderControl>(MinimumProperty, MaximumProperty, ValueProperty, TrackBrushProperty);
        ValueProperty.Changed.AddClassHandler<BackgroundColorSliderControl>(
            static (control, _) => control.ValueChanged?.Invoke(control, EventArgs.Empty));
    }

    public BackgroundColorSliderControl()
    {
        MinHeight = 28;
        Focusable = true;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => pointer = null;
    }

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, Math.Clamp(value, Minimum, Maximum)); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    internal event EventHandler? ValueChanged;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double width = Bounds.Width, center = Bounds.Height / 2, normalized = Normalize(Value);
        if (width <= 0) return;
        var track = new Rect(8, center - 4, Math.Max(0, width - 16), 8);
        context.DrawRectangle(TrackBrush ?? Brushes.Gray, null, track);
        double x = track.Left + normalized * track.Width;
        context.DrawEllipse(Brushes.White, new Pen(Brushes.Black, 1), new Point(x, center), 8, 8);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        double step = (Maximum - Minimum) / 100;
        if (e.Key is Key.Left or Key.Down) { Value -= step; e.Handled = true; }
        else if (e.Key is Key.Right or Key.Up) { Value += step; e.Handled = true; }
        else if (e.Key == Key.Home) { Value = Minimum; e.Handled = true; }
        else if (e.Key == Key.End) { Value = Maximum; e.Handled = true; }
        base.OnKeyDown(e);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Focus(); pointer = e.Pointer; pointer.Capture(this); SetValueAt(e.GetPosition(this).X); e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (pointer != e.Pointer) return;
        SetValueAt(e.GetPosition(this).X); e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (pointer != e.Pointer) return;
        pointer.Capture(null); pointer = null; e.Handled = true;
    }

    private void SetValueAt(double x) => Value = Minimum + Math.Clamp((x - 8) / Math.Max(1, Bounds.Width - 16), 0, 1) * (Maximum - Minimum);
    private double Normalize(double value) => Maximum <= Minimum ? 0 : Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1);
}
