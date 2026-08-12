using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Motara.App.Localization;
using Motara.App.ViewModels;
using Motara.Persistence;

namespace Motara.App.Controls;

public sealed partial class ScreenshotWorkspaceControl : UserControl
{
    private readonly Slider countdown;
    private readonly TextBlock countdownValue;
    private readonly ToggleSwitch transparent;
    private readonly ToggleSwitch customResolution;
    private readonly TextBox width;
    private readonly TextBox height;
    private readonly RadioButton extendCanvas;
    private readonly RadioButton centerCrop;
    private readonly TextBlock validation;
    private readonly TextBlock highResolutionWarning;
    private ScreenshotWorkspaceViewModel? viewModel;
    private LocalizationManager? localization;
    private bool updating;

    public ScreenshotWorkspaceControl()
    {
        AvaloniaXamlLoader.Load(this);
        countdown = this.FindControl<Slider>("CountdownSlider")!;
        countdownValue = this.FindControl<TextBlock>("CountdownValue")!;
        transparent = this.FindControl<ToggleSwitch>("TransparentToggle")!;
        customResolution = this.FindControl<ToggleSwitch>("CustomResolutionToggle")!;
        width = this.FindControl<TextBox>("WidthInput")!;
        height = this.FindControl<TextBox>("HeightInput")!;
        extendCanvas = this.FindControl<RadioButton>("ExtendCanvasChoice")!;
        centerCrop = this.FindControl<RadioButton>("CenterCropChoice")!;
        validation = this.FindControl<TextBlock>("ValidationText")!;
        highResolutionWarning = this.FindControl<TextBlock>("HighResolutionWarning")!;

        countdown.PropertyChanged += (_, args) =>
        {
            if (!updating && args.Property == Slider.ValueProperty && viewModel is not null)
            {
                viewModel.CountdownSeconds = (int)Math.Round(countdown.Value);
                countdownValue.Text = $"{viewModel.CountdownSeconds} s";
            }
        };
        transparent.IsCheckedChanged += (_, _) =>
        {
            if (!updating && viewModel is not null)
            {
                viewModel.UseTransparentBackground = transparent.IsChecked == true;
            }
        };
        customResolution.IsCheckedChanged += (_, _) =>
        {
            if (!updating && viewModel is not null)
            {
                viewModel.UseCustomResolution = customResolution.IsChecked == true;
                UpdateResolutionEnabled();
            }
        };
        width.TextChanged += (_, _) => UpdateTextDraft();
        height.TextChanged += (_, _) => UpdateTextDraft();
        extendCanvas.IsCheckedChanged += (_, _) => UpdateFraming();
        centerCrop.IsCheckedChanged += (_, _) => UpdateFraming();
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => viewModel?.Cancel();
        this.FindControl<Button>("OpenFolderButton")!.Click += (_, _) => viewModel?.OpenFolder();
        this.FindControl<Button>("CaptureButton")!.Click += async (_, _) =>
            await (viewModel?.CaptureAsync(CancellationToken.None) ?? Task.CompletedTask);
    }

    internal Control InitialFocus => countdown;

    internal void Attach(ScreenshotWorkspaceViewModel value, LocalizationManager manager)
    {
        viewModel = value ?? throw new ArgumentNullException(nameof(value));
        localization = manager ?? throw new ArgumentNullException(nameof(manager));
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = value;
        Localize();
        BuildPresetButtons();
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
        DataContext = null;
    }

    private void BuildPresetButtons()
    {
        (string Name, ScreenshotResolutionPreset Preset)[] presets =
        [
            ("Preset720", ScreenshotResolutionPreset.Hd720),
            ("Preset1080", ScreenshotResolutionPreset.FullHd1080),
            ("Preset2K", ScreenshotResolutionPreset.Qhd2K),
            ("Preset4K", ScreenshotResolutionPreset.Uhd4K),
            ("Preset8K", ScreenshotResolutionPreset.Uhd8K),
            ("Preset16K", ScreenshotResolutionPreset.Uhd16K),
        ];
        foreach ((string name, ScreenshotResolutionPreset preset) in presets)
        {
            Button button = this.FindControl<Button>(name)!;
            button.Click += (_, _) =>
            {
                viewModel?.ApplyPreset(preset);
                RefreshResolution();
            };
        }
    }

    private void Localize()
    {
        LocalizationManager manager = localization!;
        this.FindControl<TextBlock>("CountdownLabel")!.Text = manager.GetString("Workspace.Screenshot.Countdown");
        this.FindControl<TextBlock>("TransparentLabel")!.Text = manager.GetString("Workspace.Screenshot.Transparent");
        this.FindControl<TextBlock>("CustomResolutionLabel")!.Text = manager.GetString("Workspace.Screenshot.CustomResolution");
        this.FindControl<TextBlock>("WidthLabel")!.Text = manager.GetString("Workspace.Screenshot.Width");
        this.FindControl<TextBlock>("HeightLabel")!.Text = manager.GetString("Workspace.Screenshot.Height");
        this.FindControl<TextBlock>("FramingLabel")!.Text = manager.GetString("Workspace.Screenshot.Framing");
        extendCanvas.Content = manager.GetString("Workspace.Screenshot.ExtendCanvas");
        centerCrop.Content = manager.GetString("Workspace.Screenshot.CenterCrop");
        this.FindControl<Button>("CancelButton")!.Content = manager.GetString("Command.Cancel");
        Button openFolder = this.FindControl<Button>("OpenFolderButton")!;
        openFolder.Content = manager.GetString("Workspace.Screenshot.OpenFolder");
        openFolder.IsEnabled = viewModel?.CanOpenFolder == true;
        this.FindControl<Button>("CaptureButton")!.Content = manager.GetString("Workspace.Screenshot.Capture");
        highResolutionWarning.Text = manager.GetString("Workspace.Screenshot.HighResolutionWarning");
    }

    private void Refresh()
    {
        updating = true;
        countdown.Value = viewModel!.CountdownSeconds;
        countdownValue.Text = $"{viewModel.CountdownSeconds} s";
        transparent.IsChecked = viewModel.UseTransparentBackground;
        customResolution.IsChecked = viewModel.UseCustomResolution;
        RefreshResolution();
        extendCanvas.IsChecked = viewModel.FramingMode == ScreenshotFramingMode.ExtendCanvas;
        centerCrop.IsChecked = viewModel.FramingMode == ScreenshotFramingMode.CenterCrop;
        updating = false;
        RefreshState();
    }

    private void RefreshResolution()
    {
        updating = true;
        width.Text = viewModel!.WidthText;
        height.Text = viewModel.HeightText;
        updating = false;
        UpdateResolutionEnabled();
    }

    private void UpdateResolutionEnabled()
    {
        bool enabled = viewModel?.UseCustomResolution == true;
        width.IsEnabled = enabled;
        height.IsEnabled = enabled;
        this.FindControl<WrapPanel>("PresetButtons")!.IsEnabled = enabled;
    }

    private void UpdateTextDraft()
    {
        if (updating || viewModel is null)
        {
            return;
        }

        viewModel.WidthText = width.Text ?? string.Empty;
        viewModel.HeightText = height.Text ?? string.Empty;
    }

    private void UpdateFraming()
    {
        if (updating || viewModel is null)
        {
            return;
        }

        viewModel.FramingMode = centerCrop.IsChecked == true
            ? ScreenshotFramingMode.CenterCrop
            : ScreenshotFramingMode.ExtendCanvas;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ScreenshotWorkspaceViewModel.ValidationResourceKey)
            or nameof(ScreenshotWorkspaceViewModel.IsHighResolutionWarningVisible))
        {
            RefreshState();
        }
    }

    private void RefreshState()
    {
        validation.Text = viewModel?.ValidationResourceKey is { } key
            ? localization!.GetString(key)
            : string.Empty;
        highResolutionWarning.Opacity = viewModel?.IsHighResolutionWarningVisible == true ? 1 : 0;
    }
}
