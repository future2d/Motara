using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Motara.App.Localization;
using Motara.App.ViewModels;
using Motara.Persistence;

namespace Motara.App.Controls;

public sealed partial class WindowPresentationSettingsControl : UserControl
{
    private readonly TextBlock widthLabel;
    private readonly TextBlock heightLabel;
    private readonly TextBlock scaleLabel;
    private readonly TextBlock scaleHint;
    private readonly TextBlock frameRateLabel;
    private readonly TextBlock frameRateHint;
    private readonly TextBlock statusText;
    private readonly TextBox widthInput;
    private readonly TextBox heightInput;
    private readonly ComboBox scaleSelector;
    private readonly ComboBox frameRateSelector;
    private readonly Button restoreDefaultsButton;
    private readonly Button applyButton;
    private readonly DispatcherTimer statusClearTimer;
    private WindowPresentationSettingsViewModel? viewModel;
    private LocalizationManager? localization;
    private bool updating;

    public WindowPresentationSettingsControl()
    {
        AvaloniaXamlLoader.Load(this);
        widthLabel = this.FindControl<TextBlock>("WidthLabel")!;
        heightLabel = this.FindControl<TextBlock>("HeightLabel")!;
        scaleLabel = this.FindControl<TextBlock>("ScaleLabel")!;
        scaleHint = this.FindControl<TextBlock>("ScaleHint")!;
        frameRateLabel = this.FindControl<TextBlock>("FrameRateLabel")!;
        frameRateHint = this.FindControl<TextBlock>("FrameRateHint")!;
        statusText = this.FindControl<TextBlock>("StatusText")!;
        widthInput = this.FindControl<TextBox>("WidthInput")!;
        heightInput = this.FindControl<TextBox>("HeightInput")!;
        scaleSelector = this.FindControl<ComboBox>("ScaleSelector")!;
        frameRateSelector = this.FindControl<ComboBox>("FrameRateSelector")!;
        restoreDefaultsButton = this.FindControl<Button>("RestoreDefaultsButton")!;
        applyButton = this.FindControl<Button>("ApplyButton")!;
        statusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        statusClearTimer.Tick += (_, _) =>
        {
            statusClearTimer.Stop();
            if (viewModel?.StatusResourceKey == "Workspace.WindowPresentation.Applied")
            {
                statusText.Text = string.Empty;
            }
        };
        widthInput.TextChanged += (_, _) => UpdateDraftFromInputs();
        heightInput.TextChanged += (_, _) => UpdateDraftFromInputs();
        scaleSelector.SelectionChanged += (_, _) => UpdateDraftScale();
        frameRateSelector.SelectionChanged += (_, _) => UpdateDraftFrameRate();
        restoreDefaultsButton.Click += (_, _) => RestoreDefaults();
        applyButton.Click += async (_, _) => await ApplyAsync();
    }

    public void Attach(WindowPresentationSettingsViewModel value, LocalizationManager manager)
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        viewModel = value ?? throw new ArgumentNullException(nameof(value));
        localization = manager ?? throw new ArgumentNullException(nameof(manager));
        DataContext = value;
        value.PropertyChanged += OnViewModelPropertyChanged;
        ApplyLocalization();
        UpdateInputs();
        UpdateStatus();
    }

    public void Detach()
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        viewModel = null;
        localization = null;
        DataContext = null;
        statusClearTimer.Stop();
    }

    private void UpdateDraftFromInputs()
    {
        if (updating || viewModel is null)
        {
            return;
        }

        viewModel.WidthText = widthInput.Text ?? string.Empty;
        viewModel.HeightText = heightInput.Text ?? string.Empty;
    }

    private void UpdateDraftScale()
    {
        if (updating
            || viewModel is null
            || scaleSelector.SelectedItem is not ContentScaleOption option)
        {
            return;
        }

        viewModel.ContentScaleMode = option.Mode;
        if (option.Mode == ContentScaleMode.Fixed)
        {
            viewModel.ContentScale = option.Value;
        }
    }

    private void UpdateDraftFrameRate()
    {
        if (updating
            || viewModel is null
            || frameRateSelector.SelectedItem is not FrameRateOption option)
        {
            return;
        }

        viewModel.FrameRateMode = option.Value;
    }

    private void RestoreDefaults()
    {
        if (viewModel is null)
        {
            return;
        }

        viewModel.RestoreDefaults();
        UpdateInputs();
        UpdateStatus();
    }

    private async Task ApplyAsync()
    {
        if (viewModel is null)
        {
            return;
        }

        UpdateDraftFromInputs();
        await viewModel.ApplyAsync(CancellationToken.None);
        UpdateStatus();
    }

    private void ApplyLocalization()
    {
        LocalizationManager manager = localization!;
        widthLabel.Text = manager.GetString("Workspace.WindowPresentation.Width");
        heightLabel.Text = manager.GetString("Workspace.WindowPresentation.Height");
        scaleLabel.Text = manager.GetString("Workspace.WindowPresentation.Scale");
        scaleHint.Text = manager.GetString("Workspace.WindowPresentation.ScaleHint");
        scaleSelector.ItemsSource = new[]
        {
            new ContentScaleOption(
                manager.GetString("Workspace.WindowPresentation.ScaleAutomatic"),
                ContentScaleMode.Automatic,
                1),
            new ContentScaleOption("1/4", 0.25),
            new ContentScaleOption("1/2", 0.5),
            new ContentScaleOption("3/4", 0.75),
            new ContentScaleOption("1", 1),
            new ContentScaleOption("3/2", 1.5),
            new ContentScaleOption("2", 2),
            new ContentScaleOption("3", 3),
            new ContentScaleOption("4", 4),
        };
        frameRateLabel.Text = manager.GetString("Workspace.WindowPresentation.FrameRate");
        frameRateHint.Text = manager.GetString("Workspace.WindowPresentation.FrameRateHint");
        frameRateSelector.ItemsSource = new[]
        {
            new FrameRateOption(manager.GetString("Workspace.WindowPresentation.FrameRate60"), FrameRateMode.FramesPerSecond60),
            new FrameRateOption(manager.GetString("Workspace.WindowPresentation.FrameRate30"), FrameRateMode.FramesPerSecond30),
            new FrameRateOption(manager.GetString("Workspace.WindowPresentation.FrameRateVSync"), FrameRateMode.VSync),
            new FrameRateOption(manager.GetString("Workspace.WindowPresentation.FrameRateVSyncHalf"), FrameRateMode.VSyncHalf),
        };
        restoreDefaultsButton.Content = manager.GetString("Command.RestoreDefaults");
        applyButton.Content = manager.GetString("Command.Confirm");
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(WindowPresentationSettingsViewModel.StatusResourceKey))
        {
            UpdateStatus();
        }
    }

    private void UpdateInputs()
    {
        if (viewModel is null)
        {
            return;
        }

        updating = true;
        widthInput.Text = viewModel.WidthText;
        heightInput.Text = viewModel.HeightText;
        scaleSelector.SelectedItem = scaleSelector.Items
            .OfType<ContentScaleOption>()
            .Single(option => option.Mode == viewModel.ContentScaleMode
                && (option.Mode == ContentScaleMode.Automatic
                    || option.Value.Equals(viewModel.ContentScale)));
        frameRateSelector.SelectedItem = frameRateSelector.Items
            .OfType<FrameRateOption>()
            .Single(option => option.Value == viewModel.FrameRateMode);
        updating = false;
    }

    private void UpdateStatus()
    {
        string? key = viewModel?.StatusResourceKey;
        statusText.Text = key is not null ? localization!.GetString(key) : string.Empty;
        statusClearTimer.Stop();
        if (key == "Workspace.WindowPresentation.Applied")
        {
            statusClearTimer.Start();
        }
    }

    private sealed record ContentScaleOption(string Label, ContentScaleMode Mode, double Value)
    {
        public ContentScaleOption(string label, double value)
            : this(label, ContentScaleMode.Fixed, value)
        {
        }

        public override string ToString() => Label;
    }

    private sealed record FrameRateOption(string Label, FrameRateMode Value)
    {
        public override string ToString() => Label;
    }
}
