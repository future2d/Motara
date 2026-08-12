using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Motara.App.Backgrounds;
using Motara.App.Localization;
using Motara.App.ViewModels;
using Motara.Media;
using Motara.Persistence;

namespace Motara.App.Controls;

internal sealed partial class BackgroundEditorControl : UserControl, IDisposable
{
    private readonly TabControl typeTabs;
    private readonly TextBlock solidTabText;
    private readonly TextBlock imageTabText;
    private readonly TextBlock videoTabText;
    private readonly TextBlock spout2TabText;
    private readonly TextBlock ndiTabText;
    private readonly TextBox solidColorInput;
    private readonly Border colorSwatch;
    private readonly BackgroundColorAdjustmentsControl colorAdjustments;
    private readonly Control solidEditor;
    private readonly Control imageEditor;
    private readonly Control videoEditor;
    private readonly Control signalEditor;
    private readonly ComboBox signalSourceComboBox;
    private readonly Button refreshSignalButton;
    private readonly TextBlock signalStatusText;
    private readonly Control layoutEditor;
    private readonly TextBlock imageStatusText;
    private readonly TextBlock statusText;
    private readonly Button chooseImageButton;
    private readonly Button chooseVideoButton;
    private readonly TextBlock videoStatusText;
    private readonly StackPanel recentImageSection;
    private readonly StackPanel recentImageItems;
    private readonly StackPanel recentVideoSection;
    private readonly StackPanel recentVideoItems;
    private readonly CheckBox enableVideoAlphaCheckBox;
    private readonly CheckBox loopVideoCheckBox;
    private readonly NumericUpDown videoSpeedInput;
    private readonly TextBox ffmpegArgumentsInput;
    private readonly Button resetButton;
    private readonly Button applyButton;
    private readonly Button cancelButton;
    private readonly Dictionary<ToggleButton, BackgroundLayoutMode> layoutButtons;
    private BackgroundEditorViewModel? viewModel;
    private LocalizationManager? localization;
    private bool updating;
    private VideoSignalProtocol signalProtocol = VideoSignalProtocol.Spout2;
    private CancellationTokenSource? lifetimeCancellation;

    public BackgroundEditorControl()
    {
        AvaloniaXamlLoader.Load(this);
        typeTabs = this.FindControl<TabControl>("TypeTabs")!;
        solidTabText = this.FindControl<TextBlock>("SolidTabText")!;
        imageTabText = this.FindControl<TextBlock>("ImageTabText")!;
        videoTabText = this.FindControl<TextBlock>("VideoTabText")!;
        spout2TabText = this.FindControl<TextBlock>("Spout2TabText")!;
        ndiTabText = this.FindControl<TextBlock>("NdiTabText")!;
        solidColorInput = this.FindControl<TextBox>("SolidColorInput")!;
        colorSwatch = this.FindControl<Border>("ColorSwatch")!;
        colorAdjustments = this.FindControl<BackgroundColorAdjustmentsControl>("ColorAdjustments")!;
        solidEditor = this.FindControl<Control>("SolidEditor")!;
        imageEditor = this.FindControl<Control>("ImageEditor")!;
        videoEditor = this.FindControl<Control>("VideoEditor")!;
        signalEditor = this.FindControl<Control>("SignalEditor")!;
        signalSourceComboBox = this.FindControl<ComboBox>("SignalSourceComboBox")!;
        refreshSignalButton = this.FindControl<Button>("RefreshSignalButton")!;
        signalStatusText = this.FindControl<TextBlock>("SignalStatusText")!;
        layoutEditor = this.FindControl<Control>("LayoutEditor")!;
        imageStatusText = this.FindControl<TextBlock>("ImageStatusText")!;
        statusText = this.FindControl<TextBlock>("StatusText")!;
        chooseImageButton = this.FindControl<Button>("ChooseImageButton")!;
        chooseVideoButton = this.FindControl<Button>("ChooseVideoButton")!;
        videoStatusText = this.FindControl<TextBlock>("VideoStatusText")!;
        recentImageSection = this.FindControl<StackPanel>("RecentImageSection")!;
        recentImageItems = this.FindControl<StackPanel>("RecentImageItems")!;
        recentVideoSection = this.FindControl<StackPanel>("RecentVideoSection")!;
        recentVideoItems = this.FindControl<StackPanel>("RecentVideoItems")!;
        enableVideoAlphaCheckBox = this.FindControl<CheckBox>("EnableVideoAlphaCheckBox")!;
        loopVideoCheckBox = this.FindControl<CheckBox>("LoopVideoCheckBox")!;
        videoSpeedInput = this.FindControl<NumericUpDown>("VideoSpeedInput")!;
        ffmpegArgumentsInput = this.FindControl<TextBox>("FfmpegArgumentsInput")!;
        resetButton = this.FindControl<Button>("ResetButton")!;
        applyButton = this.FindControl<Button>("ApplyButton")!;
        cancelButton = this.FindControl<Button>("CancelButton")!;
        layoutButtons = new Dictionary<ToggleButton, BackgroundLayoutMode>
        {
            [this.FindControl<ToggleButton>("FillLayoutButton")!] = BackgroundLayoutMode.Fill,
            [this.FindControl<ToggleButton>("FitLayoutButton")!] = BackgroundLayoutMode.Fit,
            [this.FindControl<ToggleButton>("StretchLayoutButton")!] = BackgroundLayoutMode.Stretch,
            [this.FindControl<ToggleButton>("CenterLayoutButton")!] = BackgroundLayoutMode.Center,
            [this.FindControl<ToggleButton>("TileLayoutButton")!] = BackgroundLayoutMode.Tile,
        };
        typeTabs.SelectionChanged += (_, _) => OnTypeTabSelectionChanged();
        solidColorInput.TextChanged += (_, _) => UpdateColorDraft();
        solidColorInput.LostFocus += (_, _) => CommitColorDraft();
        colorAdjustments.SelectedColorChanged += (_, args) => SetSelectedColor(args.Color);
        foreach ((ToggleButton button, BackgroundLayoutMode mode) in layoutButtons)
            button.Click += (_, _) => SetLayout(mode);
        chooseImageButton.Click += async (_, _) => await ChooseImageAsync();
        chooseVideoButton.Click += async (_, _) => await ChooseVideoAsync();
        refreshSignalButton.Click += async (_, _) => await RefreshSignalSourcesAsync();
        signalSourceComboBox.SelectionChanged += (_, _) => SelectSignalSource();
        enableVideoAlphaCheckBox.PropertyChanged += (_, args) =>
        {
            if (args.Property == ToggleButton.IsCheckedProperty) UpdateVideoOptions();
        };
        loopVideoCheckBox.PropertyChanged += (_, args) =>
        {
            if (args.Property == ToggleButton.IsCheckedProperty) UpdateVideoOptions();
        };
        videoSpeedInput.ValueChanged += (_, _) => UpdateVideoOptions();
        ffmpegArgumentsInput.TextChanged += (_, _) => UpdateVideoOptions();
        resetButton.Click += (_, _) =>
        {
            viewModel?.RestoreDefault();
            signalProtocol = viewModel?.SignalSource?.Protocol ?? VideoSignalProtocol.Spout2;
            Refresh();
        };
        applyButton.Click += async (_, _) => await ApplyAsync();
        cancelButton.Click += (_, _) => viewModel?.Cancel();
        AutomationProperties.SetAutomationId(this, "workspace.background-editor");
        AutomationProperties.SetAutomationId(solidColorInput, "workspace.background-editor.color");
        AutomationProperties.SetAutomationId(colorAdjustments, "workspace.background-editor.color-adjustments");
        AutomationProperties.SetAutomationId(chooseImageButton, "workspace.background-editor.choose-image");
    }

    internal Control InitialFocus => viewModel?.Kind switch { BackgroundKind.Image => chooseImageButton, BackgroundKind.Video => chooseVideoButton, _ => solidColorInput };

    internal void Attach(BackgroundEditorViewModel value, LocalizationManager manager)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(manager);
        Detach(); viewModel = value; localization = manager; DataContext = value;
        signalProtocol = value.SignalSource?.Protocol ?? VideoSignalProtocol.Spout2;
        lifetimeCancellation = new CancellationTokenSource();
        value.PropertyChanged += OnViewModelPropertyChanged; ApplyLocalization(); Refresh();
        _ = value.LoadRecentAssetsAsync(lifetimeCancellation.Token);
        _ = value.LoadSignalSourcesAsync(signalProtocol, lifetimeCancellation.Token);
    }

    internal void Detach()
    {
        lifetimeCancellation?.Cancel();
        lifetimeCancellation?.Dispose();
        lifetimeCancellation = null;
        if (viewModel is not null) viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel = null; localization = null; DataContext = null;
    }

    public void Dispose() => Detach();

    internal static FilePickerOpenOptions CreateImagePickerOptions(string title) => new()
    {
        Title = title, AllowMultiple = false,
        FileTypeFilter = [new FilePickerFileType(title) { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"], MimeTypes = ["image/png", "image/jpeg", "image/webp", "image/bmp"] }],
    };

    internal static FilePickerOpenOptions CreateVideoPickerOptions(string title) => new()
    {
        Title = title, AllowMultiple = false,
        FileTypeFilter = [new FilePickerFileType(title) { Patterns = ["*.mp4", "*.mov", "*.webm", "*.mkv", "*.avi", "*.m4v"], MimeTypes = ["video/mp4", "video/quicktime", "video/webm", "video/x-matroska", "video/x-msvideo"] }],
    };

    private void SetKind(BackgroundKind kind) { if (!updating && viewModel is not null) viewModel.Kind = kind; }
    private void SetLayout(BackgroundLayoutMode mode) { if (!updating && viewModel is not null) viewModel.Layout = mode; }
    private void UpdateColorDraft() { if (!updating && viewModel is not null) viewModel.SolidColor = solidColorInput.Text ?? string.Empty; }

    internal void CommitColorDraft()
    {
        if (updating || viewModel is null) return;
        try { viewModel.SolidColor = BackgroundDefinition.Solid(solidColorInput.Text ?? string.Empty).SolidColor; }
        catch (ArgumentException) { }
    }

    private void SetSelectedColor(Color color)
    {
        if (updating || viewModel is null) return;
        string value = $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";
        viewModel.SolidColor = value;
    }

    private async Task ChooseImageAsync()
    {
        if (viewModel is null || localization is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider) return;
        IReadOnlyList<IStorageFile> files = await provider.OpenFilePickerAsync(CreateImagePickerOptions(localization.GetString("Workspace.Background.ChooseImage")));
        string? path = files.Count == 0 ? null : files[0].TryGetLocalPath();
        if (path is null) return;
        try { await viewModel.ImportImageAsync(path, CancellationToken.None); } catch (Exception e) when (e is not OperationCanceledException) { Refresh(); }
    }

    private async Task ChooseVideoAsync()
    {
        if (viewModel is null || localization is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider) return;
        IReadOnlyList<IStorageFile> files = await provider.OpenFilePickerAsync(CreateVideoPickerOptions(localization.GetString("Workspace.Background.ChooseVideo")));
        string? path = files.Count == 0 ? null : files[0].TryGetLocalPath();
        if (path is null) return;
        try { await viewModel.ImportVideoAsync(path, CancellationToken.None); } catch (Exception e) when (e is not OperationCanceledException) { Refresh(); }
    }

    private async Task ApplyAsync()
    {
        if (viewModel is null) return;
        CommitColorDraft();
        try { await viewModel.ApplyAsync(CancellationToken.None); } catch (Exception e) when (e is not OperationCanceledException) { Refresh(); }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    { if (Dispatcher.UIThread.CheckAccess()) Refresh(); else Dispatcher.UIThread.Post(Refresh); }

    private void ApplyLocalization()
    {
        LocalizationManager manager = localization!;
        solidTabText.Text = manager.GetString("Workspace.Background.Solid"); imageTabText.Text = manager.GetString("Workspace.Background.Image");
        videoTabText.Text = manager.GetString("Workspace.Background.Video");
        spout2TabText.Text = manager.GetString("Menu.Scene.Attachment.Spout2"); ndiTabText.Text = manager.GetString("Menu.Scene.Attachment.Ndi");
        chooseVideoButton.Content = manager.GetString("Workspace.Background.ChooseVideo");
        enableVideoAlphaCheckBox.Content = manager.GetString("Workspace.Background.EnableAlpha");
        loopVideoCheckBox.Content = manager.GetString("Workspace.Background.Loop");
        this.FindControl<TextBlock>("VideoSpeedLabel")!.Text = manager.GetString("Workspace.Background.PlaybackSpeed");
        this.FindControl<TextBlock>("FfmpegArgumentsLabel")!.Text = manager.GetString("Workspace.Background.FfmpegArguments");
        refreshSignalButton.Content = manager.GetString("Workspace.Background.RefreshSignal");
        chooseImageButton.Content = manager.GetString("Workspace.Background.ChooseImage");
        this.FindControl<TextBlock>("RecentImageLabel")!.Text = manager.GetString("Workspace.Background.Recent");
        this.FindControl<TextBlock>("RecentVideoLabel")!.Text = manager.GetString("Workspace.Background.Recent");
        this.FindControl<TextBlock>("LayoutLabel")!.Text = manager.GetString("Workspace.Background.Layout");
        this.FindControl<ToggleButton>("FillLayoutButton")!.Content = manager.GetString("Menu.Output.Background.Fill");
        this.FindControl<ToggleButton>("FitLayoutButton")!.Content = manager.GetString("Menu.Output.Background.Fit");
        this.FindControl<ToggleButton>("StretchLayoutButton")!.Content = manager.GetString("Menu.Output.Background.Stretch");
        this.FindControl<ToggleButton>("CenterLayoutButton")!.Content = manager.GetString("Menu.Output.Background.Center");
        this.FindControl<ToggleButton>("TileLayoutButton")!.Content = manager.GetString("Menu.Output.Background.Tile");
        resetButton.Content = manager.GetString("Command.RestoreDefaults");
        applyButton.Content = manager.GetString("Command.Confirm"); cancelButton.Content = manager.GetString("Command.Cancel");
    }

    private void Refresh()
    {
        if (viewModel is null || localization is null) return;
        updating = true;
        typeTabs.SelectedIndex = viewModel.Kind switch
        {
            BackgroundKind.Image => 1,
            BackgroundKind.Video => 2,
            BackgroundKind.Signal when signalProtocol == VideoSignalProtocol.Ndi => 4,
            BackgroundKind.Signal => 3,
            _ => 0,
        };
        if (solidColorInput.Text != viewModel.SolidColor) solidColorInput.Text = viewModel.SolidColor;
        solidEditor.IsVisible = viewModel.Kind == BackgroundKind.Solid;
        imageEditor.IsVisible = viewModel.Kind == BackgroundKind.Image;
        videoEditor.IsVisible = viewModel.Kind == BackgroundKind.Video;
        signalEditor.IsVisible = viewModel.Kind == BackgroundKind.Signal;
        layoutEditor.IsVisible = viewModel.Kind is BackgroundKind.Image or BackgroundKind.Video or BackgroundKind.Signal;
        IReadOnlyList<VideoSignalSourceDescriptor> protocolSources = viewModel.GetSignalSources(signalProtocol);
        signalSourceComboBox.ItemsSource = protocolSources;
        signalSourceComboBox.SelectedItem = protocolSources.FirstOrDefault(source =>
            viewModel.SignalSource is { } selected
            && source.Protocol == selected.Protocol
            && StringComparer.Ordinal.Equals(source.Id, selected.SourceId));
        VideoSignalSourceSelection? selectedSource = viewModel.SignalSource;
        bool hasSelectedProtocolSource = selectedSource is { } && selectedSource.Protocol == signalProtocol;
        signalStatusText.Text = hasSelectedProtocolSource && selectedSource is { }
            ? $"{selectedSource.Protocol}: {selectedSource.SourceId}"
            : viewModel.GetSignalSourceError(signalProtocol) is { } error
                ? FormatSignalStatus("Workspace.Background.SignalSourceUnavailable", error)
                : protocolSources.Count == 0
                    ? FormatSignalStatus("Workspace.Background.NoSignalSources")
                    : FormatSignalStatus("Workspace.Background.SelectSignal");
        imageStatusText.Text = viewModel.ImageAssetId is null
            ? localization.GetString("Workspace.Background.NoImage")
            : localization.GetString("Workspace.Background.ImageSelected") + (viewModel.SelectedImageDisplayName ?? viewModel.ImageAssetId);
        videoStatusText.Text = viewModel.VideoAssetId is null
            ? localization.GetString("Workspace.Background.NoVideo")
            : localization.GetString("Workspace.Background.VideoSelected") + (viewModel.SelectedVideoDisplayName ?? viewModel.VideoAssetId);
        enableVideoAlphaCheckBox.IsChecked = viewModel.VideoOptions.EnableAlpha;
        loopVideoCheckBox.IsChecked = viewModel.VideoOptions.Loop;
        decimal speed = (decimal)viewModel.VideoOptions.PlaybackSpeed;
        if (videoSpeedInput.Value != speed) videoSpeedInput.Value = speed;
        if (ffmpegArgumentsInput.Text != viewModel.VideoOptions.FfmpegArguments) ffmpegArgumentsInput.Text = viewModel.VideoOptions.FfmpegArguments;
        PopulateRecentItems(
            recentImageSection,
            recentImageItems,
            BackgroundRecentAssetKind.Image,
            viewModel.RecentImages);
        PopulateRecentItems(
            recentVideoSection,
            recentVideoItems,
            BackgroundRecentAssetKind.Video,
            viewModel.RecentVideos);
        foreach ((ToggleButton button, BackgroundLayoutMode mode) in layoutButtons) button.IsChecked = viewModel.Layout == mode;
        try
        {
            Color color = BackgroundColorParser.Parse(viewModel.SolidColor);
            colorSwatch.Background = new SolidColorBrush(color);
            colorAdjustments.SelectedColor = color;
        }
        catch (ArgumentException) { colorSwatch.Background = Brushes.Transparent; }
        statusText.Text = viewModel.ErrorCode switch { BackgroundEditorErrorCode.InvalidDefinition => localization.GetString("Workspace.Background.InvalidDefinition"), BackgroundEditorErrorCode.ImportFailed => localization.GetString("Workspace.Background.ImportFailed"), BackgroundEditorErrorCode.SaveFailed => localization.GetString("Workspace.Background.SaveFailed"), BackgroundEditorErrorCode.RecentAssetUnavailable => localization.GetString("Workspace.Background.RecentUnavailable"), _ => string.Empty };
        chooseImageButton.IsEnabled = !viewModel.IsApplying; chooseVideoButton.IsEnabled = !viewModel.IsApplying; resetButton.IsEnabled = !viewModel.IsApplying;
        applyButton.IsEnabled = viewModel.HasChanges
            && !viewModel.IsApplying
            && (viewModel.Kind != BackgroundKind.Signal || hasSelectedProtocolSource);
        cancelButton.IsEnabled = !viewModel.IsApplying;
        updating = false;
    }

    private void SelectSignalSource()
    {
        if (updating || viewModel is null || signalSourceComboBox.SelectedItem is not VideoSignalSourceDescriptor source)
        {
            return;
        }

        viewModel.SelectSignalSource(source);
    }

    private void OnTypeTabSelectionChanged()
    {
        if (updating || viewModel is null)
        {
            return;
        }

        switch (typeTabs.SelectedIndex)
        {
            case 1:
                SetKind(BackgroundKind.Image);
                break;
            case 2:
                SetKind(BackgroundKind.Video);
                break;
            case 3:
                SetSignalProtocol(VideoSignalProtocol.Spout2);
                SetKind(BackgroundKind.Signal);
                _ = RefreshSignalSourcesAsync();
                break;
            case 4:
                SetSignalProtocol(VideoSignalProtocol.Ndi);
                SetKind(BackgroundKind.Signal);
                _ = RefreshSignalSourcesAsync();
                break;
            default:
                SetKind(BackgroundKind.Solid);
                break;
        }
    }

    private void SetSignalProtocol(VideoSignalProtocol protocol)
    {
        if (updating || signalProtocol == protocol)
        {
            return;
        }

        signalProtocol = protocol;
        Refresh();
    }

    private async Task RefreshSignalSourcesAsync()
    {
        if (viewModel is null || lifetimeCancellation is null)
        {
            return;
        }

        await viewModel.LoadSignalSourcesAsync(signalProtocol, lifetimeCancellation.Token);
    }

    private string FormatSignalStatus(string resourceKey, string? detail = null)
    {
        string protocolName = localization!.GetString(
            signalProtocol == VideoSignalProtocol.Ndi
                ? "Menu.Scene.Attachment.Ndi"
                : "Menu.Scene.Attachment.Spout2");
        string template = localization.GetString(resourceKey);
        return detail is null
            ? string.Format(CultureInfo.CurrentCulture, template, protocolName)
            : string.Format(CultureInfo.CurrentCulture, template, protocolName, detail);
    }

    private void UpdateVideoOptions()
    {
        if (updating || viewModel is null)
        {
            return;
        }

        double speed = videoSpeedInput.Value is decimal value
            ? (double)value
            : BackgroundVideoOptions.DefaultPlaybackSpeed;
        try
        {
            viewModel.VideoOptions = new BackgroundVideoOptions(
                enableVideoAlphaCheckBox.IsChecked == true,
                loopVideoCheckBox.IsChecked == true,
                speed,
                ffmpegArgumentsInput.Text ?? string.Empty);
        }
        catch (ArgumentException)
        {
        }
    }

    private void PopulateRecentItems(
        StackPanel section,
        StackPanel items,
        BackgroundRecentAssetKind kind,
        IReadOnlyList<BackgroundRecentAsset> recentAssets)
    {
        section.IsVisible = recentAssets.Count > 0;
        items.Children.Clear();
        foreach (BackgroundRecentAsset asset in recentAssets)
        {
            var button = new Button
            {
                Content = asset.DisplayName,
            };
            button.Classes.Add("recent-background-item");
            button.Click += async (_, _) => await SelectRecentAssetAsync(kind, asset);
            items.Children.Add(button);
        }
    }

    private async Task SelectRecentAssetAsync(
        BackgroundRecentAssetKind kind,
        BackgroundRecentAsset asset)
    {
        if (viewModel is null || lifetimeCancellation is null)
        {
            return;
        }

        CancellationToken cancellationToken = lifetimeCancellation.Token;
        try
        {
            await viewModel.SelectRecentAssetAsync(kind, asset, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            Refresh();
        }
    }
}
