using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Motara.App.Controls;

public sealed class LucideIcon : Viewbox
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<LucideIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<LucideIcon, IBrush?>(nameof(Stroke));

    public LucideIcon()
    {
        Stretch = Stretch.Uniform;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;

        var path = new ShapePath
        {
            Width = 24,
            Height = 24,
            Stretch = Stretch.None,
            StrokeThickness = 1.8,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        path.Bind(ShapePath.DataProperty, new Binding(nameof(Data)) { Source = this });
        path.Bind(ShapePath.StrokeProperty, new Binding(nameof(Stroke)) { Source = this });

        var viewport = new Canvas
        {
            Width = 24,
            Height = 24,
        };
        viewport.Children.Add(path);
        Child = viewport;
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }
}
