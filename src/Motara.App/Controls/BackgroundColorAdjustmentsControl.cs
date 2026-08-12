using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Motara.App.Controls;

internal sealed class BackgroundColorChangedEventArgs(Color color) : EventArgs
{
    internal Color Color { get; } = color;
}

internal sealed class BackgroundColorAdjustmentsControl : UserControl
{
    private static readonly string[] ChannelNames = ["R", "G", "B", "H", "S", "V", "A"];
    private readonly Dictionary<string, BackgroundColorSliderControl> sliders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NumericUpDown> inputs = new(StringComparer.Ordinal);
    private Color selectedColor = Colors.White;
    private double hue;
    private double saturation;
    private double value = 1;
    private bool updating;
    private bool suppressPropertySync;

    public static readonly StyledProperty<Color> SelectedColorProperty =
        AvaloniaProperty.Register<BackgroundColorAdjustmentsControl, Color>(nameof(SelectedColor), Colors.White);

    static BackgroundColorAdjustmentsControl()
    {
        SelectedColorProperty.Changed.AddClassHandler<BackgroundColorAdjustmentsControl>(
            static (control, args) => control.OnExternalColorChanged((Color)args.NewValue!));
    }

    public BackgroundColorAdjustmentsControl()
    {
        var grid = new Grid { RowSpacing = 6, ColumnDefinitions = new ColumnDefinitions("34,64,* ,54") };
        for (int i = 0; i < ChannelNames.Length; i++)
        {
            string name = ChannelNames[i];
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetRow(grid.Children[^1], i);
            var input = new NumericUpDown { Minimum = 0, Maximum = (decimal)Maximum(name), FormatString = "0", ShowButtonSpinner = false, Width = 58 };
            input.ValueChanged += (_, _) => SetChannel(name, (double)(input.Value ?? 0));
            grid.Children.Add(input); Grid.SetRow(input, i); Grid.SetColumn(input, 1); inputs[name] = input;
            var slider = new BackgroundColorSliderControl { Minimum = 0, Maximum = Maximum(name), HorizontalAlignment = HorizontalAlignment.Stretch };
            slider.ValueChanged += (_, _) => SetChannel(name, slider.Value);
            grid.Children.Add(slider); Grid.SetRow(slider, i); Grid.SetColumn(slider, 2); sliders[name] = slider;
            var unit = new TextBlock { Text = Unit(name), Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(unit); Grid.SetRow(unit, i); Grid.SetColumn(unit, 3);
        }

        Content = grid;
        SetHsv(selectedColor);
        RefreshControls();
    }

    public Color SelectedColor
    {
        get => GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    internal event EventHandler<BackgroundColorChangedEventArgs>? SelectedColorChanged;

    internal void SetChannelForTest(string channel, double value) => SetChannel(channel, value);

    internal double GetChannelForTest(string channel) => (double)(inputs[channel].Value ?? 0);

    private static double Maximum(string channel) => channel switch { "H" => 360, "S" or "V" => 100, _ => 255 };
    private static string Unit(string channel) => channel switch { "H" => "deg", "S" or "V" => "%", _ => string.Empty };

    private void SetChannel(string channel, double raw)
    {
        if (updating) return;
        double value = Math.Clamp(Math.Round(raw), 0, Maximum(channel));
        if (channel is "R" or "G" or "B" or "A")
        {
            byte r = channel == "R" ? ToByte(value) : selectedColor.R;
            byte g = channel == "G" ? ToByte(value) : selectedColor.G;
            byte b = channel == "B" ? ToByte(value) : selectedColor.B;
            byte a = channel == "A" ? ToByte(value) : selectedColor.A;
            SetSelected(Color.FromArgb(a, r, g, b));
            return;
        }

        if (channel == "H") hue = value;
        else if (channel == "S") saturation = value / 100d;
        else value = Math.Clamp(value / 100d, 0, 1);
        if (channel == "V") this.value = value;
        SetSelected(HsvToColor(hue, saturation, this.value, selectedColor.A), preserveHsv: true);
    }

    private void SetSelected(Color color, bool preserveHsv = false)
    {
        selectedColor = color;
        suppressPropertySync = true;
        SetValue(SelectedColorProperty, color);
        suppressPropertySync = false;
        if (!preserveHsv) SetHsv(color);
        RefreshControls();
        SelectedColorChanged?.Invoke(this, new BackgroundColorChangedEventArgs(color));
    }

    private void OnExternalColorChanged(Color color)
    {
        if (suppressPropertySync || updating) return;
        selectedColor = color;
        SetHsv(color);
        RefreshControls();
        SelectedColorChanged?.Invoke(this, new BackgroundColorChangedEventArgs(color));
    }

    private void RefreshControls()
    {
        updating = true;
        Dictionary<string, double> values = new(StringComparer.Ordinal)
        {
            ["R"] = selectedColor.R, ["G"] = selectedColor.G, ["B"] = selectedColor.B,
            ["H"] = Math.Round(hue), ["S"] = Math.Round(saturation * 100), ["V"] = Math.Round(value * 100), ["A"] = selectedColor.A,
        };
        foreach (string name in ChannelNames) { sliders[name].Value = values[name]; inputs[name].Value = (decimal)values[name]; }
        UpdateSliderBrushes();
        updating = false;
    }

    private void UpdateSliderBrushes()
    {
        Color opaque = Color.FromArgb(255, selectedColor.R, selectedColor.G, selectedColor.B);
        SetSliderBrush("R", Color.FromArgb(255, 0, selectedColor.G, selectedColor.B), Color.FromArgb(255, 255, selectedColor.G, selectedColor.B));
        SetSliderBrush("G", Color.FromArgb(255, selectedColor.R, 0, selectedColor.B), Color.FromArgb(255, selectedColor.R, 255, selectedColor.B));
        SetSliderBrush("B", Color.FromArgb(255, selectedColor.R, selectedColor.G, 0), Color.FromArgb(255, selectedColor.R, selectedColor.G, 255));
        sliders["H"].TrackBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Colors.Red, 0), new GradientStop(Colors.Yellow, 1d / 6),
                new GradientStop(Colors.Lime, 2d / 6), new GradientStop(Colors.Cyan, 3d / 6),
                new GradientStop(Colors.Blue, 4d / 6), new GradientStop(Colors.Magenta, 5d / 6),
                new GradientStop(Colors.Red, 1),
            ],
        };
        SetSliderBrush("S", HsvToColor(hue, 0, value, 255), HsvToColor(hue, 1, value, 255));
        SetSliderBrush("V", Colors.Black, HsvToColor(hue, saturation, 1, 255));
        SetSliderBrush("A", Color.FromArgb(0, opaque.R, opaque.G, opaque.B), opaque);
    }

    private void SetSliderBrush(string channel, Color start, Color end)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = [new GradientStop(start, 0), new GradientStop(end, 1)],
        };
        sliders[channel].TrackBrush = brush;
    }

    private void SetHsv(Color color) => (hue, saturation, value) = ColorToHsv(color);

    private static (double Hue, double Saturation, double Value) ColorToHsv(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        double hue = delta == 0 ? 0 : max == r ? 60 * ((g - b) / delta % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        return (hue < 0 ? hue + 360 : hue, max == 0 ? 0 : delta / max, max);
    }

    private static Color HsvToColor(double hue, double saturation, double value, byte alpha)
    {
        double c = value * saturation, segment = hue / 60, x = c * (1 - Math.Abs(segment % 2 - 1)), m = value - c;
        (double r, double g, double b) = segment switch { < 1 => (c, x, 0d), < 2 => (x, c, 0d), < 3 => (0d, c, x), < 4 => (0d, x, c), < 5 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromArgb(alpha, ToByte((r + m) * 255), ToByte((g + m) * 255), ToByte((b + m) * 255));
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
