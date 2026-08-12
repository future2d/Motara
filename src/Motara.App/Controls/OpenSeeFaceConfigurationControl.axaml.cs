using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Motara.App.Localization;
using Motara.App.Tracking;
using Motara.App.ViewModels;

namespace Motara.App.Controls;

internal sealed partial class OpenSeeFaceConfigurationControl : UserControl
{
    private TextBlock cameraLabel = null!;
    private ComboBox cameraSelector = null!;
    private Button refreshButton = null!;
    private TextBlock widthLabel = null!;
    private TextBox widthInput = null!;
    private TextBlock heightLabel = null!;
    private TextBox heightInput = null!;
    private TextBlock fpsLabel = null!;
    private TextBox fpsInput = null!;
    private TextBlock hint = null!;
    private TextBlock error = null!;
    private Button cancel = null!;
    private Button save = null!;
    private OpenSeeFaceConfigurationViewModel? viewModel;
    private LocalizationManager? localization;
    private Action? close;
    private bool updating;

    public OpenSeeFaceConfigurationControl()
    {
        AvaloniaXamlLoader.Load(this);
        cameraLabel = this.FindControl<TextBlock>("CameraLabel")!;
        cameraSelector = this.FindControl<ComboBox>("CameraSelector")!;
        refreshButton = this.FindControl<Button>("RefreshButton")!;
        widthLabel = this.FindControl<TextBlock>("WidthLabel")!;
        widthInput = this.FindControl<TextBox>("WidthInput")!;
        heightLabel = this.FindControl<TextBlock>("HeightLabel")!;
        heightInput = this.FindControl<TextBox>("HeightInput")!;
        fpsLabel = this.FindControl<TextBlock>("FpsLabel")!;
        fpsInput = this.FindControl<TextBox>("FpsInput")!;
        hint = this.FindControl<TextBlock>("Hint")!;
        error = this.FindControl<TextBlock>("Error")!;
        cancel = this.FindControl<Button>("Cancel")!;
        save = this.FindControl<Button>("Save")!;
        cameraSelector.SelectionChanged += OnCameraChanged;
        widthInput.TextChanged += OnWidthChanged;
        heightInput.TextChanged += OnHeightChanged;
        fpsInput.TextChanged += OnFpsChanged;
        refreshButton.Click += OnRefresh;
        cancel.Click += OnCancel;
        save.Click += OnSave;
        AutomationProperties.SetAutomationId(this, "workspace.openseeface-configuration");
        AutomationProperties.SetAutomationId(cameraSelector, "workspace.openseeface-configuration.camera");
        AutomationProperties.SetAutomationId(refreshButton, "workspace.openseeface-configuration.refresh");
        AutomationProperties.SetAutomationId(widthInput, "workspace.openseeface-configuration.width");
        AutomationProperties.SetAutomationId(heightInput, "workspace.openseeface-configuration.height");
        AutomationProperties.SetAutomationId(fpsInput, "workspace.openseeface-configuration.fps");
        AutomationProperties.SetAutomationId(save, "workspace.openseeface-configuration.save");
    }

    internal Control InitialFocus => cameraSelector;

    internal void Attach(
        OpenSeeFaceConfigurationViewModel value,
        LocalizationManager manager,
        Action close)
    {
        Detach();
        viewModel = value;
        localization = manager;
        this.close = close;
        DataContext = value;
        value.PropertyChanged += OnViewModelPropertyChanged;
        ApplyLocalization();
        Refresh();
    }

    internal void Detach()
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        viewModel = null;
        localization = null;
        close = null;
        DataContext = null;
    }

    private void ApplyLocalization()
    {
        LocalizationManager manager = localization!;
        cameraLabel.Text = manager.GetString("Workspace.Tracking.OpenSeeFace.Camera");
        refreshButton.Content = manager.GetString("Workspace.Tracking.OpenSeeFace.Refresh");
        widthLabel.Text = manager.GetString("Workspace.Tracking.OpenSeeFace.Width");
        heightLabel.Text = manager.GetString("Workspace.Tracking.OpenSeeFace.Height");
        fpsLabel.Text = manager.GetString("Workspace.Tracking.OpenSeeFace.Fps");
        hint.Text = manager.GetString("Workspace.Tracking.OpenSeeFace.Hint");
        cancel.Content = manager.GetString("Command.Cancel");
        save.Content = manager.GetString("Command.SaveAndStart");
        AutomationProperties.SetName(cameraSelector, cameraLabel.Text);
        AutomationProperties.SetName(refreshButton, refreshButton.Content?.ToString());
        AutomationProperties.SetName(widthInput, widthLabel.Text);
        AutomationProperties.SetName(heightInput, heightLabel.Text);
        AutomationProperties.SetName(fpsInput, fpsLabel.Text);
        AutomationProperties.SetName(cancel, cancel.Content?.ToString());
        AutomationProperties.SetName(save, save.Content?.ToString());
    }

    private void Refresh()
    {
        if (viewModel is null || localization is null)
        {
            return;
        }

        updating = true;
        try
        {
            cameraSelector.ItemsSource = viewModel.Cameras;
            cameraSelector.SelectedItem = viewModel.Cameras
                .FirstOrDefault(camera => camera.Index == viewModel.SelectedCameraIndex);
            widthInput.Text = viewModel.WidthText;
            heightInput.Text = viewModel.HeightText;
            fpsInput.Text = viewModel.FpsText;
        }
        finally
        {
            updating = false;
        }

        error.Text = viewModel.ErrorResourceKey is null
            ? string.Empty
            : localization.GetString(viewModel.ErrorResourceKey);
        bool enabled = !viewModel.IsLoading && !viewModel.IsRefreshing && !viewModel.IsSubmitting;
        cameraSelector.IsEnabled = enabled;
        refreshButton.IsEnabled = enabled;
        widthInput.IsEnabled = enabled;
        heightInput.IsEnabled = enabled;
        fpsInput.IsEnabled = enabled;
        save.IsEnabled = enabled;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Refresh();
        }
        else
        {
            Dispatcher.UIThread.Post(Refresh);
        }
    }

    private void OnCameraChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!updating
            && viewModel is not null
            && cameraSelector.SelectedItem is OpenSeeFaceCamera camera)
        {
            viewModel.SelectedCameraIndex = camera.Index;
        }
    }

    private void OnWidthChanged(object? sender, TextChangedEventArgs args)
    {
        if (!updating && viewModel is not null)
        {
            viewModel.WidthText = widthInput.Text ?? string.Empty;
        }
    }

    private void OnHeightChanged(object? sender, TextChangedEventArgs args)
    {
        if (!updating && viewModel is not null)
        {
            viewModel.HeightText = heightInput.Text ?? string.Empty;
        }
    }

    private void OnFpsChanged(object? sender, TextChangedEventArgs args)
    {
        if (!updating && viewModel is not null)
        {
            viewModel.FpsText = fpsInput.Text ?? string.Empty;
        }
    }

    private async void OnRefresh(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (viewModel is not null)
        {
            await viewModel.RefreshCamerasAsync(CancellationToken.None);
        }
    }

    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => close?.Invoke();

    private async void OnSave(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (viewModel is not null
            && await viewModel.SaveAndStartAsync(CancellationToken.None))
        {
            close?.Invoke();
        }
    }
}
