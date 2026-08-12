using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Motara.App.Localization;
using Motara.App.ViewModels;
using Motara.Media;

namespace Motara.App.Controls;

internal sealed class SignalAttachmentEditorControl : UserControl, IDisposable
{
    private readonly TabControl protocolTabs = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Background = Brushes.Transparent,
        BorderBrush = Brushes.Transparent,
        BorderThickness = new Avalonia.Thickness(0),
    };
    private readonly ComboBox source = new() { MinHeight = 32 };
    private readonly Button refresh = new();
    private readonly Button apply = new();
    private readonly Button cancel = new();
    private readonly TextBlock status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly StackPanel contentRoot = new() { Spacing = 14 };
    private readonly Dictionary<TabItem, VideoSignalProtocol> protocolByTab = [];
    private readonly Dictionary<TabItem, string> labelKeyByTab = [];
    private SignalAttachmentEditorViewModel? viewModel;
    private LocalizationManager? localization;
    private bool updating;

    internal SignalAttachmentEditorControl()
    {
        protocolTabs.Classes.Add("background-editor-tabs");
        protocolTabs.Classes.Add("workspace-header");
        protocolTabs.SelectionChanged += OnProtocolSelectionChanged;
        source.SelectionChanged += OnSourceSelectionChanged;
        refresh.Classes.Add("signal-attachment-action");
        apply.Classes.Add("signal-attachment-action");
        cancel.Classes.Add("signal-attachment-action");
        refresh.Click += OnRefreshClicked;
        apply.Click += OnApplyClicked;
        cancel.Click += OnCancelClicked;
        source.ItemTemplate = new FuncDataTemplate<VideoSignalSourceDescriptor>(
            (item, _) => new TextBlock { Text = FormatSourceLabel(item) });

        var sourceRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 10,
            Children = { source, refresh },
        };
        contentRoot.Children.Add(sourceRow);
        contentRoot.Children.Add(new Border
        {
            Child = status,
            MinHeight = 24,
        });

        var footer = new Grid
        {
            Classes = { "workspace-footer" },
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 10,
            Children =
            {
                new Border(),
                cancel,
                apply,
            },
        };
        Grid.SetColumn(cancel, 1);
        Grid.SetColumn(apply, 2);

        Content = new StackPanel
        {
            Spacing = 18,
            Children = { protocolTabs, contentRoot, footer },
        };
        AutomationProperties.SetAutomationId(this, "workspace.signal-attachment-editor");
    }

    internal Control InitialFocus => source;

    internal static string FormatSourceLabel(VideoSignalSourceDescriptor? item) =>
        item is null ? string.Empty : $"{item.DisplayName} ({item.Width}x{item.Height})";

    internal void Attach(SignalAttachmentEditorViewModel value, LocalizationManager manager)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(manager);
        Detach();
        viewModel = value;
        localization = manager;
        value.PropertyChanged += OnChanged;
        BuildProtocolTabs();
        ApplyLocalization();
        Refresh();
        _ = value.RefreshAsync(CancellationToken.None);
    }

    internal void Detach()
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnChanged;
            viewModel.CancelPendingRefresh();
        }

        viewModel = null;
        localization = null;
        protocolByTab.Clear();
        labelKeyByTab.Clear();
        protocolTabs.ItemsSource = null;
        source.ItemsSource = null;
        status.Text = string.Empty;
    }

    public void Dispose() => Detach();

    private void BuildProtocolTabs()
    {
        protocolByTab.Clear();
        labelKeyByTab.Clear();
        var tabs = new List<TabItem>
        {
            CreateTab("Menu.Scene.Attachment.Image", enabled: false),
            CreateTab("Menu.Scene.Attachment.Video", enabled: false),
            CreateTab("Menu.Scene.Attachment.Live2D", enabled: false),
            CreateTab("Menu.Scene.Attachment.Spout2", VideoSignalProtocol.Spout2),
            CreateTab("Menu.Scene.Attachment.Ndi", VideoSignalProtocol.Ndi),
            CreateTab("Menu.Scene.Attachment.VirtualCamera", enabled: false),
        };
        protocolTabs.ItemsSource = tabs;
        updating = true;
        protocolTabs.SelectedItem = protocolByTab.FirstOrDefault(pair => pair.Value == viewModel?.Protocol).Key;
        updating = false;
    }

    private TabItem CreateTab(string labelKey, VideoSignalProtocol? protocol = null, bool enabled = true)
    {
        var tab = new TabItem
        {
            Header = new TextBlock { Text = localization?.GetString(labelKey) ?? labelKey },
            IsEnabled = enabled,
            Tag = labelKey,
        };
        labelKeyByTab[tab] = labelKey;
        if (protocol is { } value)
        {
            protocolByTab[tab] = value;
        }

        return tab;
    }

    private void ApplyLocalization()
    {
        if (localization is null)
        {
            return;
        }

        refresh.Content = localization.GetString("Workspace.SignalAttachment.Refresh");
        apply.Content = localization.GetString("Command.Confirm");
        cancel.Content = localization.GetString("Command.Cancel");
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
        if (!ReferenceEquals(source.ItemsSource, viewModel.Sources))
        {
            source.ItemsSource = viewModel.Sources;
        }
        if (!ReferenceEquals(source.SelectedItem, viewModel.SelectedSource))
        {
            source.SelectedItem = viewModel.SelectedSource;
        }
        status.Text = viewModel.Error switch
        {
            "MissingSource" => localization.GetString("Workspace.SignalAttachment.SelectSource"),
            null when viewModel.IsLoading => localization.GetString("Workspace.SignalAttachment.Discovering"),
            null when viewModel.Sources.Count == 0 => localization.GetString("Workspace.SignalAttachment.NoSources"),
            null => string.Empty,
            _ => localization.GetString("Workspace.SignalAttachment.Failed"),
        };
        source.IsEnabled = !viewModel.IsLoading;
        refresh.IsEnabled = !viewModel.IsLoading;
        apply.IsEnabled = !viewModel.IsLoading && viewModel.SelectedSource is not null;
        protocolTabs.IsEnabled = !viewModel.IsLoading;
        updating = false;
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

    private void OnProtocolSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (updating || viewModel is null || protocolTabs.SelectedItem is not TabItem tab
            || !protocolByTab.TryGetValue(tab, out VideoSignalProtocol protocol))
        {
            return;
        }

        viewModel.SelectProtocol(protocol);
        _ = viewModel.RefreshAsync(CancellationToken.None);
    }

    private void OnSourceSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!updating && viewModel is not null)
        {
            viewModel.SelectedSource = source.SelectedItem as VideoSignalSourceDescriptor;
        }
    }

    private async void OnRefreshClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (viewModel is not null)
        {
            await viewModel.RefreshAsync(CancellationToken.None);
        }
    }

    private async void OnApplyClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (viewModel is not null)
        {
            await viewModel.ApplyAsync(CancellationToken.None);
        }
    }

    private void OnCancelClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => viewModel?.Cancel();
}
