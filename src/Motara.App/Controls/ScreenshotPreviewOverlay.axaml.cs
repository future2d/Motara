using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Motara.App.Screenshots;

namespace Motara.App.Controls;

public sealed partial class ScreenshotPreviewOverlay : UserControl
{
    private readonly Border countdownBadge;
    private readonly TextBlock countdownText;
    private readonly Border previewFrame;
    private readonly Image previewImage;
    private ScreenshotCoordinator? coordinator;
    private Bitmap? previewBitmap;

    public ScreenshotPreviewOverlay()
    {
        AvaloniaXamlLoader.Load(this);
        countdownBadge = this.FindControl<Border>("CountdownBadge")!;
        countdownText = this.FindControl<TextBlock>("CountdownText")!;
        previewFrame = this.FindControl<Border>("PreviewFrame")!;
        previewImage = this.FindControl<Image>("PreviewImage")!;
        previewFrame.PointerPressed += OnPreviewPressed;
        IsVisible = false;
        IsHitTestVisible = false;
    }

    internal void Attach(ScreenshotCoordinator value)
    {
        if (coordinator is not null)
        {
            coordinator.PropertyChanged -= OnCoordinatorPropertyChanged;
        }

        coordinator = value ?? throw new ArgumentNullException(nameof(value));
        coordinator.PropertyChanged += OnCoordinatorPropertyChanged;
        Refresh();
    }

    internal void Detach()
    {
        if (coordinator is not null)
        {
            coordinator.PropertyChanged -= OnCoordinatorPropertyChanged;
        }

        coordinator = null;
        ReplacePreview(null);
        IsVisible = false;
        IsHitTestVisible = false;
    }

    private void OnCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        Refresh();

    private void Refresh()
    {
        int? remaining = coordinator?.CountdownRemaining;
        countdownBadge.IsVisible = remaining is not null;
        countdownText.Text = remaining?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        byte[]? png = coordinator?.PreviewPng;
        if (png is null)
        {
            ReplacePreview(null);
        }
        else if (previewBitmap is null)
        {
            using var stream = new MemoryStream(png, writable: false);
            ReplacePreview(new Bitmap(stream));
        }

        bool previewVisible = coordinator?.IsPreviewVisible == true;
        bool captureLocked = coordinator?.IsCanvasLocked == true;
        previewFrame.IsVisible = previewVisible;
        IsVisible = remaining is not null || captureLocked || previewVisible;
        IsHitTestVisible = captureLocked || previewVisible;
    }

    private void ReplacePreview(Bitmap? bitmap)
    {
        Bitmap? previous = previewBitmap;
        previewBitmap = bitmap;
        previewImage.Source = bitmap;
        previous?.Dispose();
    }

    private void OnPreviewPressed(object? sender, PointerPressedEventArgs args)
    {
        if (coordinator?.IsPreviewVisible == true)
        {
            coordinator.DismissPreview();
            args.Handled = true;
        }
    }
}
