using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Motara.App.Backgrounds;
using Motara.App.Localization;
using Motara.App.ViewModels;
using Motara.Media;
using Motara.Persistence;
using Motara.Scene;

namespace Motara.App.Controls;

internal sealed class SceneAttachmentEditorControl : UserControl, IDisposable
{
    private readonly TabControl kindTabs = new()
    {
        Name = "KindTabs",
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Background = Brushes.Transparent,
        BorderBrush = Brushes.Transparent,
        BorderThickness = new Avalonia.Thickness(0),
    };
    private readonly StackPanel body = new() { Spacing = 14 };
    private readonly StackPanel mediaPanel = new() { Name = "MediaPanel", Spacing = 8 };
    private readonly StackPanel videoOptionsPanel = new() { Name = "VideoOptionsPanel", Spacing = 8, IsVisible = false };
    private readonly StackPanel signalPanel = new() { Name = "SignalPanel", Spacing = 8, IsVisible = false };
    private readonly TextBlock resourceStatus = new() { Name = "ResourceStatus", TextWrapping = TextWrapping.Wrap, MinHeight = 32 };
    private readonly Button chooseImage = new() { Name = "ChooseImageButton" };
    private readonly Button chooseVideo = new() { Name = "ChooseVideoButton" };
    private readonly ComboBox source = new() { Name = "SignalSourceComboBox", MinHeight = 32 };
    private readonly Button refresh = new() { Name = "RefreshSignalButton" };
    private readonly TextBlock signalStatus = new() { Name = "SignalStatus", TextWrapping = TextWrapping.Wrap, MinHeight = 24 };
    private readonly CheckBox enableAlpha = new() { Name = "EnableVideoAlphaCheckBox" };
    private readonly CheckBox loop = new() { Name = "LoopVideoCheckBox" };
    private readonly NumericUpDown playbackSpeed = new()
    {
        Name = "VideoSpeedInput",
        Minimum = 0.1m,
        Maximum = 8m,
        Increment = 0.05m,
        FormatString = "0.##",
        ShowButtonSpinner = false,
    };
    private readonly TextBox ffmpegArguments = new() { Name = "FfmpegArgumentsInput" };
    private readonly Button apply = new() { Name = "ApplyButton" };
    private readonly Button cancel = new() { Name = "CancelButton" };
    private readonly TextBlock status = new() { Name = "StatusText", TextWrapping = TextWrapping.Wrap, MinHeight = 24 };
    private readonly StackPanel recentImageItems = new() { Spacing = 6 };
    private readonly StackPanel recentVideoItems = new() { Spacing = 6 };
    private readonly StackPanel recentImageSection = new() { Spacing = 6, IsVisible = false };
    private readonly StackPanel recentVideoSection = new() { Spacing = 6, IsVisible = false };
    private readonly TextBlock recentImageLabel = new() { Name = "RecentImageLabel", FontSize = 12, Foreground = Brushes.Gray };
    private readonly TextBlock recentVideoLabel = new() { Name = "RecentVideoLabel", FontSize = 12, Foreground = Brushes.Gray };
    private readonly TextBlock playbackSpeedLabel = new() { Name = "PlaybackSpeedLabel", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock ffmpegArgumentsLabel = new() { Name = "FfmpegArgumentsLabel", VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<TabItem, SceneAttachmentKind> kindByTab = [];
    private readonly Dictionary<TabItem, string> labelKeyByTab = [];
    private SceneAttachmentEditorViewModel? viewModel;
    private LocalizationManager? localization;
    private bool updating;
    private CancellationTokenSource? lifetimeCancellation;

    internal SceneAttachmentEditorControl()
    {
        kindTabs.Classes.Add("background-editor-tabs");
        kindTabs.Classes.Add("workspace-header");
        kindTabs.SelectionChanged += OnKindSelectionChanged;
        source.ItemTemplate = new FuncDataTemplate<VideoSignalSourceDescriptor>(
            (item, _) => new TextBlock { Text = FormatSourceLabel(item) });
        source.SelectionChanged += (_, _) =>
        {
            if (!updating && viewModel is not null)
            {
                viewModel.SelectedSource = source.SelectedItem as VideoSignalSourceDescriptor;
            }
        };
        refresh.Classes.Add("signal-attachment-action");
        chooseImage.Classes.Add("signal-attachment-action");
        chooseVideo.Classes.Add("signal-attachment-action");
        apply.Classes.Add("signal-attachment-action");
        cancel.Classes.Add("signal-attachment-action");
        refresh.Click += async (_, _) => await RefreshSourcesAsync();
        chooseImage.Click += async (_, _) => await ChooseImageAsync();
        chooseVideo.Click += async (_, _) => await ChooseVideoAsync();
        apply.Click += async (_, _) => await ApplyAsync();
        cancel.Click += (_, _) => viewModel?.Cancel();
        enableAlpha.Classes.Add("scene-attachment-option");
        loop.Classes.Add("scene-attachment-option");
        enableAlpha.PropertyChanged += (_, args) =>
        {
            if (args.Property == ToggleButton.IsCheckedProperty) UpdateVideoOptions();
        };
        loop.PropertyChanged += (_, args) =>
        {
            if (args.Property == ToggleButton.IsCheckedProperty) UpdateVideoOptions();
        };
        playbackSpeed.ValueChanged += (_, _) => UpdateVideoOptions();
        ffmpegArguments.TextChanged += (_, _) => UpdateVideoOptions();

        recentImageSection.Children.Add(recentImageLabel);
        recentImageSection.Children.Add(recentImageItems);
        recentVideoSection.Children.Add(recentVideoLabel);
        recentVideoSection.Children.Add(recentVideoItems);

        var footer = new Grid
        {
            Classes = { "workspace-footer" },
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 10,
            Children = { new Border(), cancel, apply },
        };
        Grid.SetColumn(cancel, 1);
        Grid.SetColumn(apply, 2);

        var mediaRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 10,
            Children = { resourceStatus, chooseImage, chooseVideo },
        };
        Grid.SetColumn(chooseImage, 1);
        Grid.SetColumn(chooseVideo, 1);
        mediaPanel.Children.Add(mediaRow);
        body.Children.Add(mediaPanel);
        body.Children.Add(recentImageSection);
        body.Children.Add(recentVideoSection);
        CreateVideoOptionsPanel();
        CreateSignalPanel();
        body.Children.Add(videoOptionsPanel);
        body.Children.Add(signalPanel);
        body.Children.Add(status);
        Content = new StackPanel { Spacing = 18, Children = { kindTabs, body, footer } };
        AutomationProperties.SetAutomationId(this, "workspace.scene-attachment-editor");
    }

    internal Control InitialFocus => chooseImage;

    internal static string FormatSourceLabel(VideoSignalSourceDescriptor? item) =>
        item is null ? string.Empty : $"{item.DisplayName} ({item.Width}x{item.Height})";

    internal void Attach(SceneAttachmentEditorViewModel value, LocalizationManager manager)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(manager);
        Detach();
        viewModel = value;
        localization = manager;
        lifetimeCancellation = new CancellationTokenSource();
        value.PropertyChanged += OnChanged;
        BuildKindTabs();
        ApplyLocalization();
        Refresh();
        _ = value.LoadRecentAssetsAsync(lifetimeCancellation.Token);
        _ = value.RefreshAsync(lifetimeCancellation.Token);
    }

    internal void Detach()
    {
        lifetimeCancellation?.Cancel();
        lifetimeCancellation?.Dispose();
        lifetimeCancellation = null;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnChanged;
            viewModel.CancelPendingRefresh();
        }

        viewModel = null;
        localization = null;
        kindByTab.Clear();
        labelKeyByTab.Clear();
        kindTabs.ItemsSource = null;
        source.ItemsSource = null;
    }

    public void Dispose() => Detach();

    private void CreateVideoOptionsPanel()
    {
        videoOptionsPanel.Children.Add(enableAlpha);
        videoOptionsPanel.Children.Add(loop);
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 10,
            RowSpacing = 8,
        };
        grid.Children.Add(playbackSpeedLabel);
        Grid.SetRow(ffmpegArgumentsLabel, 1);
        grid.Children.Add(ffmpegArgumentsLabel);
        Grid.SetColumn(playbackSpeed, 1);
        Grid.SetRow(playbackSpeed, 0);
        grid.Children.Add(playbackSpeed);
        Grid.SetColumn(ffmpegArguments, 1);
        Grid.SetRow(ffmpegArguments, 1);
        grid.Children.Add(ffmpegArguments);
        videoOptionsPanel.Children.Add(grid);
    }

    private void CreateSignalPanel()
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 10,
            Children = { source, refresh },
        };
        Grid.SetColumn(refresh, 1);
        signalPanel.Children.Add(row);
        signalPanel.Children.Add(signalStatus);
    }

    private void BuildKindTabs()
    {
        kindByTab.Clear();
        labelKeyByTab.Clear();
        TabItem[] tabs =
        [
            CreateTab("Menu.Scene.Attachment.Image", SceneAttachmentKind.Image),
            CreateTab("Menu.Scene.Attachment.Video", SceneAttachmentKind.Video),
            CreateTab("Menu.Scene.Attachment.Live2D", SceneAttachmentKind.Live2D, false),
            CreateTab("Menu.Scene.Attachment.Spout2", SceneAttachmentKind.Spout2),
            CreateTab("Menu.Scene.Attachment.Ndi", SceneAttachmentKind.Ndi),
            CreateTab("Menu.Scene.Attachment.VirtualCamera", SceneAttachmentKind.VirtualCamera, false),
        ];
        kindTabs.ItemsSource = tabs;
        updating = true;
        kindTabs.SelectedItem = kindByTab.FirstOrDefault(pair => pair.Value == viewModel?.Kind).Key;
        updating = false;
    }

    private TabItem CreateTab(string labelKey, SceneAttachmentKind kind, bool enabled = true)
    {
        var tab = new TabItem
        {
            Header = new TextBlock { Text = localization?.GetString(labelKey) ?? labelKey },
            IsEnabled = enabled,
            Tag = labelKey,
        };
        kindByTab[tab] = kind;
        labelKeyByTab[tab] = labelKey;
        return tab;
    }

    private void ApplyLocalization()
    {
        if (localization is null)
        {
            return;
        }

        chooseImage.Content = localization.GetString("Workspace.Background.ChooseImage");
        chooseVideo.Content = localization.GetString("Workspace.Background.ChooseVideo");
        refresh.Content = localization.GetString("Workspace.SignalAttachment.Refresh");
        apply.Content = localization.GetString("Command.Confirm");
        cancel.Content = localization.GetString("Command.Cancel");
        enableAlpha.Content = localization.GetString("Workspace.Background.EnableAlpha");
        loop.Content = localization.GetString("Workspace.Background.Loop");
        recentImageLabel.Text = localization.GetString("Workspace.Background.Recent");
        recentVideoLabel.Text = localization.GetString("Workspace.Background.Recent");
        playbackSpeedLabel.Text = localization.GetString("Workspace.Background.PlaybackSpeed");
        ffmpegArgumentsLabel.Text = localization.GetString("Workspace.Background.FfmpegArguments");
        foreach ((TabItem tab, string labelKey) in labelKeyByTab)
        {
            if (tab.Header is TextBlock text)
            {
                text.Text = localization.GetString(labelKey);
            }
        }
    }

    private void Refresh()
    {
        if (viewModel is null || localization is null)
        {
            return;
        }

        updating = true;
        kindTabs.SelectedItem = kindByTab.FirstOrDefault(pair => pair.Value == viewModel.Kind).Key;
        bool image = viewModel.Kind == SceneAttachmentKind.Image;
        bool video = viewModel.Kind == SceneAttachmentKind.Video;
        bool signal = viewModel.Kind is SceneAttachmentKind.Spout2 or SceneAttachmentKind.Ndi;
        resourceStatus.Text = image
            ? viewModel.ImageAssetId is null
                ? localization.GetString("Workspace.Background.NoImage")
                : localization.GetString("Workspace.Background.ImageSelected") + (viewModel.SelectedImageDisplayName ?? viewModel.ImageAssetId)
            : video
                ? viewModel.VideoAssetId is null
                    ? localization.GetString("Workspace.Background.NoVideo")
                    : localization.GetString("Workspace.Background.VideoSelected") + (viewModel.SelectedVideoDisplayName ?? viewModel.VideoAssetId)
                : string.Empty;
        videoOptionsPanel.IsVisible = video;
        signalPanel.IsVisible = signal;
        source.ItemsSource = viewModel.Sources;
        source.SelectedItem = viewModel.SelectedSource;
        signalStatus.Text = viewModel.Error switch
        {
            "MissingSource" => localization.GetString("Workspace.SignalAttachment.SelectSource"),
            null when viewModel.IsLoading => localization.GetString("Workspace.SignalAttachment.Discovering"),
            null when viewModel.Sources.Count == 0 => localization.GetString("Workspace.SignalAttachment.NoSources"),
            null => string.Empty,
            _ => localization.GetString("Workspace.SignalAttachment.Failed"),
        };
        source.IsEnabled = !viewModel.IsLoading;
        refresh.IsEnabled = !viewModel.IsLoading;
        chooseImage.IsVisible = image;
        chooseVideo.IsVisible = video;
        mediaPanel.IsVisible = image || video;
        enableAlpha.IsChecked = viewModel.VideoOptions.EnableAlpha;
        loop.IsChecked = viewModel.VideoOptions.Loop;
        playbackSpeed.Value = (decimal)viewModel.VideoOptions.PlaybackSpeed;
        ffmpegArguments.Text = viewModel.VideoOptions.FfmpegArguments;
        PopulateRecent(recentImageSection, recentImageItems, BackgroundRecentAssetKind.Image, viewModel.RecentImages);
        PopulateRecent(recentVideoSection, recentVideoItems, BackgroundRecentAssetKind.Video, viewModel.RecentVideos);
        status.Text = viewModel.Error switch
        {
            "MissingSource" => localization.GetString("Workspace.SignalAttachment.SelectSource"),
            "RecentAssetUnavailable" => localization.GetString("Workspace.Background.RecentUnavailable"),
            null => string.Empty,
            _ => localization.GetString("Workspace.SignalAttachment.Failed"),
        };
        apply.IsEnabled = !viewModel.IsLoading && viewModel.HasSelectedContent;
        updating = false;
    }

    private void PopulateRecent(
        StackPanel section,
        StackPanel items,
        BackgroundRecentAssetKind kind,
        IReadOnlyList<BackgroundRecentAsset> recent)
    {
        section.IsVisible = recent.Count > 0 && viewModel?.Kind == (kind == BackgroundRecentAssetKind.Image
            ? SceneAttachmentKind.Image
            : SceneAttachmentKind.Video);
        items.Children.Clear();
        foreach (BackgroundRecentAsset asset in recent)
        {
            var button = new Button { Content = asset.DisplayName };
            button.Classes.Add("recent-background-item");
            button.Click += async (_, _) => await SelectRecentAsync(kind, asset);
            items.Children.Add(button);
        }
    }

    private async Task SelectRecentAsync(BackgroundRecentAssetKind kind, BackgroundRecentAsset asset)
    {
        if (viewModel is null || lifetimeCancellation is null)
        {
            return;
        }

        try
        {
            await viewModel.SelectRecentAssetAsync(kind, asset, lifetimeCancellation.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Refresh();
        }
    }

    private void OnKindSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (updating || viewModel is null || kindTabs.SelectedItem is not TabItem tab
            || !kindByTab.TryGetValue(tab, out SceneAttachmentKind kind))
        {
            return;
        }

        viewModel.SelectKind(kind);
        _ = viewModel.RefreshAsync(lifetimeCancellation?.Token ?? CancellationToken.None);
    }

    private async Task ChooseImageAsync()
    {
        if (viewModel is null || localization is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await provider.OpenFilePickerAsync(
            BackgroundEditorControl.CreateImagePickerOptions(localization.GetString("Workspace.Background.ChooseImage")));
        string? path = files.Count == 0 ? null : files[0].TryGetLocalPath();
        if (path is null || lifetimeCancellation is null)
        {
            return;
        }

        try
        {
            await viewModel.ImportImageAsync(path, lifetimeCancellation.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Refresh();
        }
    }

    private async Task ChooseVideoAsync()
    {
        if (viewModel is null || localization is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await provider.OpenFilePickerAsync(
            BackgroundEditorControl.CreateVideoPickerOptions(localization.GetString("Workspace.Background.ChooseVideo")));
        string? path = files.Count == 0 ? null : files[0].TryGetLocalPath();
        if (path is null || lifetimeCancellation is null)
        {
            return;
        }

        try
        {
            await viewModel.ImportVideoAsync(path, lifetimeCancellation.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Refresh();
        }
    }

    private async Task RefreshSourcesAsync()
    {
        if (viewModel is not null)
        {
            await viewModel.RefreshAsync(lifetimeCancellation?.Token ?? CancellationToken.None);
        }
    }

    private async Task ApplyAsync()
    {
        if (viewModel is not null)
        {
            await viewModel.ApplyAsync(lifetimeCancellation?.Token ?? CancellationToken.None);
        }
    }

    private void UpdateVideoOptions()
    {
        if (updating || viewModel is null)
        {
            return;
        }

        try
        {
            viewModel.VideoOptions = new BackgroundVideoOptions(
                enableAlpha.IsChecked == true,
                loop.IsChecked == true,
                playbackSpeed.Value is decimal speed ? (double)speed : 1,
                ffmpegArguments.Text ?? string.Empty);
        }
        catch (ArgumentException)
        {
        }
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs args)
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

}
