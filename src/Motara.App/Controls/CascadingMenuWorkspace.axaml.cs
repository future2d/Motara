using System.Collections.Immutable;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Shell;
using Motara.App.Input;
using Motara.App.Shortcuts;
using Motara.App.ViewModels;
using Motara.Persistence;
using Motara.Scene;

namespace Motara.App.Controls;

public enum MenuScrollTarget
{
    None = 0,
    WorkspaceHorizontal = 1,
    ColumnVertical = 2,
}

public sealed partial class CascadingMenuWorkspace : UserControl
{
    public const double PanelWidth = 272;
    public const double PanelGap = 10;
    private readonly StackPanel panels;
    private MainWindowViewModel? viewModel;
    private MenuCatalog? menuCatalog;
    private readonly Dictionary<string, InformationBlockVisual> informationBlocks = new(StringComparer.Ordinal);
    private readonly List<SourceRowVisual> sourceRows = [];
    private SourceDragState? sourceDrag;
    private Border? insertionIndicator;
    private StackPanel? insertionPanel;
    private MenuWorkspaceLayout lastLayout;
    private NavigationDestination? offsetDestination;
    private double horizontalOffset;
    private string? capturingInputNodeId;
    private int shortcutRefreshPending;
    private InputActionRegistry inputActions = BuiltInInputActions.CreateRegistry();
    private ILogger<CascadingMenuWorkspace> logger = NullLogger<CascadingMenuWorkspace>.Instance;

    public CascadingMenuWorkspace()
    {
        AvaloniaXamlLoader.Load(this);
        panels = this.FindControl<StackPanel>("Panels")!;
        KeyDown += OnKeyDown;
        PointerWheelChanged += OnPointerWheelChanged;
    }

    public event EventHandler? HorizontalOffsetChanged;

    public int PanelCount => panels.Children.Count;

    public int ColumnCount => panels.Children
        .OfType<StackPanel>()
        .Sum(static group => group.Children.Count);

    public double HorizontalOffset => horizontalOffset;

    public void SetInputActions(InputActionRegistry registry) =>
        inputActions = registry ?? throw new ArgumentNullException(nameof(registry));

    internal void SetLogger(ILogger<CascadingMenuWorkspace>? value) =>
        logger = value ?? NullLogger<CascadingMenuWorkspace>.Instance;

    public void Attach(MainWindowViewModel value)
    {
        if (viewModel is not null)
        {
            viewModel.ShortcutMenuInvalidated -= OnShortcutMenuInvalidated;
        }
        viewModel = value ?? throw new ArgumentNullException(nameof(value));
        viewModel.ShortcutMenuInvalidated += OnShortcutMenuInvalidated;
        menuCatalog = new MenuCatalog(value);
        Refresh();
    }

    private void OnShortcutMenuInvalidated()
    {
        if (Interlocked.Exchange(ref shortcutRefreshPending, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                Refresh();
            }
            finally
            {
                Volatile.Write(ref shortcutRefreshPending, 0);
            }
        });
    }

    public void Refresh()
    {
        capturingInputNodeId = null;
        informationBlocks.Clear();
        sourceRows.Clear();
        ClearSourceDragVisuals();
        sourceDrag = null;
        panels.Children.Clear();
        if (viewModel?.Navigation.SelectedDestination is not NavigationDestination destination)
        {
            horizontalOffset = 0;
            offsetDestination = null;
            IsVisible = false;
            return;
        }

        if (destination == NavigationDestination.Collaboration)
        {
            horizontalOffset = 0;
            offsetDestination = destination;
            IsVisible = false;
            return;
        }

        if (menuCatalog!.GetRootNodes(destination).IsEmpty)
        {
            horizontalOffset = 0;
            offsetDestination = destination;
            IsVisible = false;
            return;
        }

        if (offsetDestination != destination)
        {
            horizontalOffset = 0;
            offsetDestination = destination;
        }

        int previousLevelCount = panels.Children.Count;
        MenuLevelGroup group = menuCatalog.GetRootLevel(destination);
        panels.Children.Add(CreateGroup(0, group));
        for (int level = 0; level < viewModel.Navigation.SelectedMenuPath.Length; level++)
        {
            string selectedId = viewModel.Navigation.SelectedMenuPath[level];
            MenuNode? selected = group.Columns
                .SelectMany(static column => column.Nodes)
                .FirstOrDefault(
                entry => StringComparer.Ordinal.Equals(entry.Id, selectedId));
            if (selected is null || selected.Children.IsEmpty)
            {
                break;
            }

            group = MenuLevelGroup.SingleColumn(
                $"{selected.Id}.children",
                selected.IsLiteralLabel
                    ? "Menu.Scene.Source.Information"
                    : selected.LabelResourceKey,
                selected.Children);
            panels.Children.Add(CreateGroup(level + 1, group));
        }

        if (panels.Children.Count > previousLevelCount)
        {
            horizontalOffset = 0;
        }

        IsVisible = true;
        panels.UpdateLayout();
    }

    public void RefreshStatusValues()
    {
        if (viewModel?.Navigation.SelectedDestination is not NavigationDestination destination)
        {
            return;
        }

        MenuLevelGroup group = menuCatalog!.GetRootLevel(destination);
        UpdateStatusValues(group.Columns
            .SelectMany(static column => column.Nodes)
            .ToImmutableArray());
        for (int level = 0; level < viewModel.Navigation.SelectedMenuPath.Length; level++)
        {
            string selectedId = viewModel.Navigation.SelectedMenuPath[level];
            MenuNode? selected = group.Columns
                .SelectMany(static column => column.Nodes)
                .FirstOrDefault(
                entry => StringComparer.Ordinal.Equals(entry.Id, selectedId));
            if (selected is null || selected.Children.IsEmpty)
            {
                break;
            }

            group = MenuLevelGroup.SingleColumn(
                $"{selected.Id}.children",
                selected.IsLiteralLabel
                    ? "Menu.Scene.Source.Information"
                    : selected.LabelResourceKey,
                selected.Children);
            UpdateStatusValues(group.Columns
                .SelectMany(static column => column.Nodes)
                .ToImmutableArray());
        }
    }

    public double CalculateLeft(double railAnchor, double canvasWidth, double rightSafeMargin = 16)
    {
        double[] widths = panels.Children
            .OfType<StackPanel>()
            .Select(group => (group.Children.Count * PanelWidth)
                + (Math.Max(0, group.Children.Count - 1) * PanelGap))
            .ToArray();
        lastLayout = MenuWorkspaceState.CalculateLayout(
            widths,
            PanelGap,
            railAnchor,
            canvasWidth,
            rightSafeMargin,
            horizontalOffset);
        horizontalOffset = lastLayout.AppliedOffset;
        return lastLayout.Left;
    }

    public bool TryPanHorizontal(double delta)
    {
        if (!IsVisible || !lastLayout.HasOverflow || !double.IsFinite(delta))
        {
            return false;
        }

        double next = Math.Clamp(
            horizontalOffset + delta,
            lastLayout.MinimumOffset,
            lastLayout.MaximumOffset);
        if (Math.Abs(next - horizontalOffset) < 0.01)
        {
            return false;
        }

        horizontalOffset = next;
        HorizontalOffsetChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public static MenuScrollTarget ClassifyScroll(Vector delta, KeyModifiers modifiers)
    {
        if ((modifiers & KeyModifiers.Control) != 0 && Math.Abs(delta.Y) > 0)
        {
            return MenuScrollTarget.WorkspaceHorizontal;
        }

        if (Math.Abs(delta.X) > Math.Abs(delta.Y))
        {
            return MenuScrollTarget.WorkspaceHorizontal;
        }

        return Math.Abs(delta.Y) > 0
            ? MenuScrollTarget.ColumnVertical
            : MenuScrollTarget.None;
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs args)
    {
        MenuScrollTarget target = ClassifyScroll(args.Delta, args.KeyModifiers);
        if (target != MenuScrollTarget.WorkspaceHorizontal)
        {
            return;
        }

        bool isControlWheel = (args.KeyModifiers & KeyModifiers.Control) != 0;
        double sourceDelta = isControlWheel
            ? args.Delta.Y
            : args.Delta.X;
        InputDirection direction = sourceDelta >= 0
            ? InputDirection.Positive
            : InputDirection.Negative;
        InputGesture gesture = isControlWheel
            ? InputGesture.Wheel(InputAxis.Vertical, direction, InputModifiers.Control)
            : InputGesture.TouchpadAxis(InputAxis.Horizontal, direction);
        InputResolution? resolution = inputActions.Resolve(
            new InputContext(
                [InputBindingScope.MenuColumn, InputBindingScope.MenuWorkspace, InputBindingScope.Canvas],
                IsNativeControl: false),
            gesture);
        if (resolution is not { ShouldConsume: true } action)
        {
            return;
        }

        double signedDelta = action.ActionId switch
        {
            BuiltInInputActions.MenuPanLeft => Math.Abs(sourceDelta) * 48,
            BuiltInInputActions.MenuPanRight => -Math.Abs(sourceDelta) * 48,
            _ => 0,
        };
        if (signedDelta != 0 && TryPanHorizontal(signedDelta))
        {
            args.Handled = true;
        }
    }

    public void HandleEscape()
    {
        if (viewModel is null)
        {
            return;
        }

        if (!viewModel.Navigation.SelectedMenuPath.IsEmpty)
        {
            int level = viewModel.Navigation.SelectedMenuPath.Length - 1;
            string nodeId = viewModel.Navigation.SelectedMenuPath[level];
            viewModel.SelectMenuNode(level, nodeId);
            FocusMenuItem(nodeId);
            return;
        }

        if (viewModel.Navigation.SelectedDestination is NavigationDestination destination)
        {
            viewModel.SelectDestination(destination);
        }
    }

    private StackPanel CreateGroup(int level, MenuLevelGroup group)
    {
        var container = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = PanelGap,
            VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetAutomationId(container, $"menu.group.{group.Id}");
        for (int index = 0; index < group.Columns.Length; index++)
        {
            MenuColumn column = group.Columns[index];
            container.Children.Add(CreatePanel(level, column, index));
        }

        return container;
    }

    private Border CreatePanel(int level, MenuColumn column, int columnIndex)
    {
        string titleKey = column.TitleResourceKey;
        IReadOnlyList<MenuNode> entries = column.Nodes;
        MenuNode? pinnedSearch = level == 0
            ? entries.FirstOrDefault(entry => StringComparer.Ordinal.Equals(entry.Id, "shortcuts.search"))
            : null;
        string title = titleKey.Contains('.', StringComparison.Ordinal)
            ? viewModel!.Localization.GetString(titleKey)
            : viewModel!.Localization.GetString($"Navigation.{titleKey}");
        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
        };
        var heading = new StackPanel();
        var titleLabel = new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = (IBrush)this.FindResource("TextPrimary")!,
        };
        AutomationProperties.SetAutomationId(
            titleLabel,
            columnIndex == 0
                ? $"menu.level.{level + 1}.title"
                : $"menu.level.{level + 1}.column.{columnIndex + 1}.title");
        heading.Children.Add(titleLabel);
        if (pinnedSearch is not null)
        {
            heading.Children.Add(CreateEntryControl(level, pinnedSearch, heading));
        }
        content.Children.Add(new Border
        {
            Padding = new Avalonia.Thickness(14, 13, 14, 9),
            Child = heading,
        });

        var list = new StackPanel
        {
            Spacing = 3,
            Margin = new Avalonia.Thickness(7, 5, 7, 9),
        };

        foreach (MenuNode entry in entries.Where(entry => !ReferenceEquals(entry, pinnedSearch)))
        {
            list.Children.Add(CreateEntryControl(level, entry, list));
        }
        var scroll = new ScrollViewer
        {
            Content = list,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        };
        AutomationProperties.SetAutomationId(
            scroll,
            columnIndex == 0
                ? $"menu.level.{level + 1}.scroll"
                : $"menu.level.{level + 1}.column.{columnIndex + 1}.scroll");
        scroll.Tag = $"{column.Id}.scroll";
        scroll.ApplyTemplate();
        Grid.SetRow(scroll, 1);
        content.Children.Add(scroll);

        var contentClip = new Border
        {
            ClipToBounds = true,
            CornerRadius = new Avalonia.CornerRadius(11),
            Child = content,
        };

        var panel = new Border
        {
            Width = PanelWidth,
            Background = (IBrush)this.FindResource("SurfaceFloating")!,
            BorderBrush = (IBrush)this.FindResource("BorderSubtle")!,
            BorderThickness = new Avalonia.Thickness(1),
            BoxShadow = (BoxShadows)this.FindResource("FloatingSurfaceShadow")!,
            ClipToBounds = true,
            CornerRadius = new Avalonia.CornerRadius(12),
            Child = contentClip,
            Margin = new Avalonia.Thickness(0, level * 42, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetAutomationId(
            panel,
            columnIndex == 0
                ? $"menu.level.{level + 1}"
                : $"menu.level.{level + 1}.column.{columnIndex + 1}");
        AutomationProperties.SetName(panel, title);
        panel.Tag = column.Id;
        return panel;
    }

    private Control CreateEntryControl(int level, MenuNode entry, StackPanel ownerPanel)
    {
        if (entry.Kind == MenuNodeKind.Separator)
        {
            var separator = new Border
            {
                Height = 1,
                Margin = new Avalonia.Thickness(5, 6),
                Background = (IBrush)this.FindResource("DividerSubtle")!,
                Focusable = false,
                IsHitTestVisible = false,
            };
            AutomationProperties.SetAutomationId(separator, $"menu.separator.{entry.Id}");
            return separator;
        }

        if (entry.Kind == MenuNodeKind.SectionHeading)
        {
            if (entry.SectionActions is not null)
            {
                return CreateSectionHeading(entry);
            }

            string sectionLabel = viewModel!.Localization.GetString(entry.LabelResourceKey);
            var sectionHeading = new TextBlock
            {
                Text = sectionLabel,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = (IBrush)this.FindResource("TextSecondary")!,
                Margin = new Avalonia.Thickness(9, 7, 9, 2),
                Focusable = false,
                IsHitTestVisible = false,
                TextWrapping = TextWrapping.Wrap,
            };
            AutomationProperties.SetAutomationId(sectionHeading, $"menu.section.{entry.Id}");
            AutomationProperties.SetName(sectionHeading, sectionLabel);
            return sectionHeading;
        }

        if (entry.Kind == MenuNodeKind.TextInput)
        {
            return CreateTextInput(entry);
        }

        if (entry.Kind == MenuNodeKind.Choice)
        {
            return CreateChoice(entry);
        }

        if (entry.Kind == MenuNodeKind.InputCapture)
        {
            return CreateInputCapture(entry);
        }

        MainWindowViewModel currentViewModel = viewModel
            ?? throw new InvalidOperationException("The menu workspace is not attached.");
        string label = entry.IsLiteralLabel
            ? entry.LabelResourceKey
            : currentViewModel.Localization.GetString(entry.LabelResourceKey);
        if (entry.Kind == MenuNodeKind.Toggle)
        {
            return CreateToggle(
                entry,
                label,
                entry.ToggleValue,
                entry.ToggleChangeAsync
                    ?? throw new InvalidOperationException("A toggle node requires a change callback."));
        }

        if (entry.Kind == MenuNodeKind.InformationBlock)
        {
            return CreateInformationBlock(entry);
        }

        if (entry.ActionAsync is not null)
        {
            return CreateAsyncAction(entry, label, entry.ActionAsync);
        }

        if (entry.SourceActions is not null)
        {
            return CreateMainModelSourceRow(level, entry, label, ownerPanel);
        }

        bool isSelected = entry.IsSelected
            || (level < currentViewModel.Navigation.SelectedMenuPath.Length
                && StringComparer.Ordinal.Equals(
                    currentViewModel.Navigation.SelectedMenuPath[level],
                    entry.Id));
        System.Windows.Input.ICommand? command = entry.BeforeOpen is not null
            ? null
            : entry.Command
            ?? (entry.Children.IsEmpty ? null : currentViewModel.SelectMenuNodeCommand);
        var button = new Button
        {
            Theme = (ControlTheme)this.FindResource("MenuRowButtonTheme")!,
            Content = CreateRowContent(entry, label),
            Command = command,
            CommandParameter = entry.Command is not null
                ? entry.CommandParameter
                : entry.Children.IsEmpty
                ? null
                : new MainWindowViewModel.MenuSelection(level, entry.Id),
            IsEnabled = entry.IsEnabled,
            BorderThickness = new Avalonia.Thickness(1),
            FontSize = 14,
            FontWeight = isSelected ? FontWeight.SemiBold : FontWeight.Normal,
        };
        button.Classes.Set("selected", isSelected);
        button.Classes.Set("warning", entry.InformationState == MenuInformationState.Warning);
        AutomationProperties.SetAutomationId(button, $"menu.{entry.Id}");
        AutomationProperties.SetName(button, entry.AutomationName ?? label);
        string helpText = entry.HelpTextResourceKey is not null
            ? currentViewModel.Localization.GetString(entry.HelpTextResourceKey)
            : entry.Children.IsEmpty
                ? label
                : string.Format(
                currentViewModel.Localization.Culture,
                currentViewModel.Localization.GetString("Accessibility.SubmenuFormat"),
                label);
        AutomationProperties.SetHelpText(button, helpText);
        if (entry.BeforeOpen is not null)
        {
            button.Click += (_, _) =>
            {
                entry.BeforeOpen();
                currentViewModel.SelectMenuNode(level, entry.Id);
            };
        }
        return button;
    }

    private Grid CreateSectionHeading(MenuNode entry)
    {
        MainWindowViewModel currentViewModel = viewModel
            ?? throw new InvalidOperationException("The menu workspace is not attached.");
        MenuSectionActions actions = entry.SectionActions
            ?? throw new InvalidOperationException("An interactive section requires actions.");
        string label = currentViewModel.Localization.GetString(entry.LabelResourceKey);
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Avalonia.Thickness(2, 6, 2, 1),
        };
        var toggle = new Button
        {
            Theme = (ControlTheme)this.FindResource("MenuRowButtonTheme")!,
            Content = $"{label}  {actions.Count}",
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Padding = new Avalonia.Thickness(7, 5),
        };
        AutomationProperties.SetAutomationId(toggle, $"menu.{entry.Id}");
        AutomationProperties.SetName(toggle, label);
        toggle.Click += (_, _) => actions.Toggle();
        var create = new Button
        {
            Theme = (ControlTheme)this.FindResource("MenuRowButtonTheme")!,
            Content = "+",
            Width = 32,
            Height = 30,
            Padding = new Avalonia.Thickness(0),
            FontSize = 17,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = actions.CanCreate,
        };
        Grid.SetColumn(create, 1);
        AutomationProperties.SetAutomationId(create, $"menu.{entry.Id.Replace("section.", "create.", StringComparison.Ordinal)}");
        AutomationProperties.SetName(
            create,
            currentViewModel.Localization.GetString("Workspace.InputBindings.New"));
        create.Click += (_, args) =>
        {
            args.Handled = true;
            actions.Create();
        };
        grid.Children.Add(toggle);
        grid.Children.Add(create);
        return grid;
    }

    private StackPanel CreateTextInput(MenuNode entry)
    {
        MainWindowViewModel currentViewModel = viewModel
            ?? throw new InvalidOperationException("The menu workspace is not attached.");
        var panel = new StackPanel { Spacing = 5, Margin = new Avalonia.Thickness(7, 4) };
        if (!StringComparer.Ordinal.Equals(entry.Id, "shortcuts.search"))
        {
            panel.Children.Add(new TextBlock
            {
                Text = currentViewModel.Localization.GetString(entry.LabelResourceKey),
                FontSize = 12,
                Foreground = (IBrush)this.FindResource("TextSecondary")!,
            });
        }
        var input = new TextBox
        {
            Text = entry.TextValue,
            PlaceholderText = currentViewModel.Localization.GetString(entry.PlaceholderResourceKey!),
            FontSize = 14,
        };
        AutomationProperties.SetAutomationId(input, $"menu.{entry.Id}");
        input.TextChanged += (_, _) => entry.TextChanged?.Invoke(input.Text);
        panel.Children.Add(input);
        return panel;
    }

    private StackPanel CreateChoice(MenuNode entry)
    {
        MainWindowViewModel currentViewModel = viewModel
            ?? throw new InvalidOperationException("The menu workspace is not attached.");
        var panel = new StackPanel { Spacing = 5, Margin = new Avalonia.Thickness(7, 4) };
        if (!StringComparer.Ordinal.Equals(entry.Id, "shortcuts.action"))
        {
            panel.Children.Add(new TextBlock
            {
                Text = currentViewModel.Localization.GetString(entry.LabelResourceKey),
                FontSize = 12,
                Foreground = (IBrush)this.FindResource("TextSecondary")!,
            });
        }
        MenuChoiceDisplay[] items = entry.ChoiceOptions
            .Select(static option => new MenuChoiceDisplay(option.Id, option.Label))
            .ToArray();
        var choice = new ComboBox
        {
            ItemsSource = items,
            SelectedItem = items.FirstOrDefault(item =>
                StringComparer.Ordinal.Equals(item.Id, entry.SelectedChoiceId)),
            FontSize = 14,
        };
        AutomationProperties.SetAutomationId(choice, $"menu.{entry.Id}");
        choice.SelectionChanged += (_, _) =>
            entry.ChoiceChanged?.Invoke((choice.SelectedItem as MenuChoiceDisplay)?.Id);
        panel.Children.Add(choice);
        return panel;
    }

    private StackPanel CreateInputCapture(MenuNode entry)
    {
        MainWindowViewModel currentViewModel = viewModel
            ?? throw new InvalidOperationException("The menu workspace is not attached.");
        var panel = new StackPanel { Spacing = 5, Margin = new Avalonia.Thickness(7, 4) };
        panel.Children.Add(new TextBlock
        {
            Text = currentViewModel.Localization.GetString(entry.LabelResourceKey),
            FontSize = 12,
            Foreground = (IBrush)this.FindResource("TextSecondary")!,
        });
        bool capturing = StringComparer.Ordinal.Equals(capturingInputNodeId, entry.Id);
        var text = new TextBlock
        {
            Text = capturing
                ? currentViewModel.Localization.GetString("Workspace.InputBindings.Capturing")
                : entry.CapturedGesture is { } gesture
                    ? ShortcutGestureFormatter.Format(gesture)
                    : currentViewModel.Localization.GetString("Workspace.InputBindings.GestureUnset"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        var capture = new Border
        {
            Focusable = true,
            MinHeight = 42,
            Padding = new Avalonia.Thickness(10, 8),
            CornerRadius = new Avalonia.CornerRadius(4),
            BorderThickness = new Avalonia.Thickness(capturing ? 2 : 1),
            BorderBrush = (IBrush)this.FindResource(capturing ? "CategoryApricot" : "BorderSubtle")!,
            Background = (IBrush)this.FindResource("SurfaceFloatingHover")!,
            Child = text,
        };
        AutomationProperties.SetAutomationId(capture, $"menu.{entry.Id}");
        void CancelCapture()
        {
            if (!StringComparer.Ordinal.Equals(capturingInputNodeId, entry.Id)) return;
            capturingInputNodeId = null;
            text.Text = entry.CapturedGesture is { } gesture
                ? ShortcutGestureFormatter.Format(gesture)
                : currentViewModel.Localization.GetString("Workspace.InputBindings.GestureUnset");
            capture.BorderThickness = new Avalonia.Thickness(1);
            capture.BorderBrush = (IBrush)this.FindResource("BorderSubtle")!;
        }
        capture.PointerPressed += (_, args) =>
        {
            if (!StringComparer.Ordinal.Equals(capturingInputNodeId, entry.Id))
            {
                capturingInputNodeId = entry.Id;
                text.Text = currentViewModel.Localization.GetString("Workspace.InputBindings.Capturing");
                capture.BorderThickness = new Avalonia.Thickness(2);
                capture.BorderBrush = (IBrush)this.FindResource("CategoryApricot")!;
                capture.Focus();
                args.Handled = true;
                return;
            }

            string? button = args.GetCurrentPoint(capture).Properties.PointerUpdateKind switch
            {
                PointerUpdateKind.LeftButtonPressed => "Left",
                PointerUpdateKind.RightButtonPressed => "Right",
                PointerUpdateKind.MiddleButtonPressed => "Middle",
                PointerUpdateKind.XButton1Pressed => "XButton1",
                PointerUpdateKind.XButton2Pressed => "XButton2",
                _ => null,
            };
            if (button is null) return;
            entry.GestureChanged?.Invoke(InputGesture.MouseButton(button, ToInputModifiers(args.KeyModifiers)));
            CancelCapture();
            args.Handled = true;
        };
        capture.KeyDown += (_, args) =>
        {
            if (!StringComparer.Ordinal.Equals(capturingInputNodeId, entry.Id)) return;
            if (args.Key == Key.Escape)
            {
                CancelCapture();
                args.Handled = true;
                return;
            }
            if (IsModifier(args.Key)) return;
            entry.GestureChanged?.Invoke(InputGesture.KeyChord(args.Key.ToString(), ToInputModifiers(args.KeyModifiers)));
            CancelCapture();
            args.Handled = true;
        };
        capture.LostFocus += (_, _) => CancelCapture();
        panel.Children.Add(capture);
        return panel;
    }

    private Button CreateAsyncAction(
        MenuNode entry,
        string label,
        Func<Task<bool>> action)
    {
        var button = new Button
        {
            Theme = (ControlTheme)this.FindResource("MenuRowButtonTheme")!,
            Content = CreateRowContent(entry, label),
            IsEnabled = entry.IsEnabled,
            BorderThickness = new Avalonia.Thickness(1),
            FontSize = 14,
        };
        AutomationProperties.SetAutomationId(button, $"menu.{entry.Id}");
        AutomationProperties.SetName(button, label);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await action().ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }

    private Border CreateInformationBlock(MenuNode entry)
    {
        MainWindowViewModel currentViewModel = viewModel
            ?? throw new InvalidOperationException("The menu workspace is not attached.");
        string title = entry.IsLiteralLabel
            ? entry.LabelResourceKey
            : currentViewModel.Localization.GetString(entry.LabelResourceKey);
        var root = new StackPanel { Spacing = 10 };
        var header = new Grid
        {
            ColumnDefinitions = string.IsNullOrWhiteSpace(entry.IconResourceKey)
                ? new ColumnDefinitions("*")
                : new ColumnDefinitions("17,8,*"),
        };
        if (!string.IsNullOrWhiteSpace(entry.IconResourceKey))
        {
            header.Children.Add(CreateIcon(entry.IconResourceKey, 17));
        }

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = entry.InformationState == MenuInformationState.Neutral
                ? (IBrush)this.FindResource("TextPrimary")!
                : GetInformationStateBrush(entry.InformationState),
            TextWrapping = TextWrapping.Wrap,
        };
        if (!string.IsNullOrWhiteSpace(entry.IconResourceKey))
        {
            Grid.SetColumn(titleText, 2);
        }
        header.Children.Add(titleText);
        root.Children.Add(header);

        var fields = new StackPanel
        {
            Spacing = 8,
        };
        root.Children.Add(fields);
        InformationBlockVisual visual = PopulateInformationBlock(entry, fields);
        bool hasDisplayNameEditor = entry.SourceActions is
        {
            DisplayName: not null,
            SetDisplayNameAsync: not null,
        };
        if (hasDisplayNameEditor)
        {
            MenuSourceActions actions = entry.SourceActions!;
            string initialName = actions.DisplayName!;
            var nameLabel = new TextBlock
            {
                Text = currentViewModel.Localization.GetString("Menu.Scene.Source.DisplayName"),
                FontSize = 13,
                Foreground = (IBrush)this.FindResource("TextSecondary")!,
            };
            var editor = new TextBox
            {
                Text = initialName,
                MinHeight = 36,
                Padding = new Avalonia.Thickness(8, 6),
                FontSize = 13,
                Background = (IBrush)this.FindResource("SurfaceFloatingHover")!,
                BorderBrush = (IBrush)this.FindResource("BorderSubtle")!,
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(6),
            };
            AutomationProperties.SetAutomationId(editor, $"menu.{entry.Id}.display-name");
            AutomationProperties.SetName(editor, nameLabel.Text);
            var editorRoot = new StackPanel { Spacing = 5 };
            editorRoot.Children.Add(nameLabel);
            editorRoot.Children.Add(editor);
            root.Children.Add(editorRoot);

            string committedName = initialName;
            Func<string, Task<bool>> setDisplayNameAsync = actions.SetDisplayNameAsync!;
            bool commitInProgress = false;
            async Task CommitDisplayNameAsync()
            {
                if (commitInProgress || StringComparer.Ordinal.Equals(editor.Text?.Trim(), committedName))
                {
                    return;
                }

                string nextName = editor.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(nextName))
                {
                    editor.Text = committedName;
                    return;
                }

                commitInProgress = true;
                editor.IsEnabled = false;
                try
                {
                    if (await setDisplayNameAsync(nextName).ConfigureAwait(true))
                    {
                        committedName = nextName;
                    }
                    else
                    {
                        editor.Text = committedName;
                    }
                }
                catch (Exception)
                {
                    editor.Text = committedName;
                }
                finally
                {
                    editor.IsEnabled = true;
                    commitInProgress = false;
                }
            }

            editor.KeyDown += async (_, args) =>
            {
                if (args.Key != Key.Enter)
                {
                    return;
                }

                args.Handled = true;
                await CommitDisplayNameAsync().ConfigureAwait(true);
            };
            editor.LostFocus += async (_, _) => await CommitDisplayNameAsync().ConfigureAwait(true);
            visual = visual with { DisplayNameEditor = editor };
        }

        informationBlocks[entry.Id] = visual;

        if (entry.UnavailableReasonResourceKey is string reasonResourceKey)
        {
            var reason = new TextBlock
            {
                Text = currentViewModel.Localization.GetString(reasonResourceKey),
                FontSize = 12,
                Foreground = (IBrush)this.FindResource("TextSecondary")!,
                TextWrapping = TextWrapping.Wrap,
            };
            AutomationProperties.SetAutomationId(reason, $"menu.{entry.Id}.unavailable-reason");
            root.Children.Add(reason);
        }

        var block = new Border
        {
            Background = (IBrush)this.FindResource("SurfaceFloatingHover")!,
            BorderBrush = GetInformationStateBrush(entry.InformationState),
            BorderThickness = entry.InformationState == MenuInformationState.Neutral
                ? default
                : new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(12),
            Focusable = false,
            IsHitTestVisible = hasDisplayNameEditor,
            Child = root,
            Tag = "information-block",
        };
        AutomationProperties.SetAutomationId(block, $"menu.{entry.Id}");
        AutomationProperties.SetName(block, title);
        if (entry.UnavailableReasonResourceKey is string unavailableReason)
        {
            AutomationProperties.SetHelpText(
                block,
                currentViewModel.Localization.GetString(unavailableReason));
        }
        return block;
    }

    private InformationBlockVisual PopulateInformationBlock(MenuNode entry, StackPanel fields)
    {
        MainWindowViewModel currentViewModel = viewModel
            ?? throw new InvalidOperationException("The menu workspace is not attached.");
        fields.Children.Clear();
        if (entry.StatusFields.IsEmpty)
        {
            var empty = new TextBlock
            {
                Text = currentViewModel.Localization.GetString(
                    entry.EmptyValueResourceKey ?? "Menu.Common.NoData"),
                FontSize = 13,
                Foreground = (IBrush)this.FindResource("TextSecondary")!,
                TextWrapping = TextWrapping.Wrap,
            };
            AutomationProperties.SetAutomationId(empty, $"menu.{entry.Id}.empty");
            fields.Children.Add(empty);
            return new InformationBlockVisual(fields, []);
        }

        var valueControls = new TextBlock[entry.StatusFields.Length];
        for (int index = 0; index < entry.StatusFields.Length; index++)
        {
            MenuStatusField field = entry.StatusFields[index];
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("86,*"),
            };
            var label = new TextBlock
            {
                Text = currentViewModel.Localization.GetString(field.LabelResourceKey),
                FontSize = 13,
                Foreground = (IBrush)this.FindResource("TextSecondary")!,
                VerticalAlignment = VerticalAlignment.Top,
            };
            AutomationProperties.SetAutomationId(label, $"menu.{entry.Id}.field.{index}.label");
            row.Children.Add(label);

            var value = new TextBlock
            {
                Text = field.Value,
                FontSize = 14,
                FontWeight = FontWeight.Medium,
                Foreground = (IBrush)this.FindResource("TextPrimary")!,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top,
            };
            AutomationProperties.SetAutomationId(value, $"menu.{entry.Id}.field.{index}.value");
            AutomationProperties.SetName(
                value,
                $"{label.Text}: {field.Value}");
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            valueControls[index] = value;
            fields.Children.Add(row);
        }
        return new InformationBlockVisual(fields, valueControls);
    }

    private void UpdateStatusValues(IReadOnlyList<MenuNode> entries)
    {
        foreach (MenuNode entry in entries)
        {
            if (entry.Kind != MenuNodeKind.InformationBlock
                || !informationBlocks.TryGetValue(entry.Id, out InformationBlockVisual? visual))
            {
                continue;
            }

            if (visual.Values.Length != entry.StatusFields.Length)
            {
                informationBlocks[entry.Id] = PopulateInformationBlock(entry, visual.Fields);
                continue;
            }

            for (int index = 0; index < visual.Values.Length; index++)
            {
                visual.Values[index].Text = entry.StatusFields[index].Value;
                AutomationProperties.SetName(
                    visual.Values[index],
                    $"{viewModel!.Localization.GetString(entry.StatusFields[index].LabelResourceKey)}: {entry.StatusFields[index].Value}");
            }
        }
    }

    private IBrush GetInformationStateBrush(MenuInformationState state) =>
        (IBrush)this.FindResource(state switch
        {
            MenuInformationState.Positive => "StateConnected",
            MenuInformationState.Warning => "StateDegraded",
            MenuInformationState.Error => "StateFaulted",
            _ => "BorderSubtle",
        })!;

    private sealed record InformationBlockVisual(
        StackPanel Fields,
        TextBlock[] Values,
        TextBox? DisplayNameEditor = null);

    private sealed record SourceRowVisual(
        Guid SourceId,
        AttachmentPlacement Placement,
        int OrderIndex,
        StackPanel Panel,
        Grid Row,
        MenuSourceActions Actions)
    {
        public bool IsMainModel => Actions.IsMainModel;
    }

    internal readonly record struct SourceDropRow(
        Guid SourceId,
        AttachmentPlacement Placement,
        bool IsMainModel,
        double Top,
        double Height);

    internal readonly record struct SourceDropTarget(
        AttachmentPlacement Placement,
        int DestinationIndex,
        int VisualIndex,
        int MainModelBoundaryIndex = -1);

    private sealed record SourceDragState(
        SourceRowVisual Source,
        double StartY,
        bool IsDragging,
        SourceDropTarget? DropTarget = null);

    internal static SourceDropTarget CalculateSourceDropTarget(
        Guid sourceId,
        AttachmentPlacement sourcePlacement,
        Point pointerPosition,
        IReadOnlyList<SourceDropRow> rows)
    {
        SourceDropRow[] candidates = rows
            .Where(row => row.SourceId != sourceId)
            .OrderBy(row => row.Top)
            .ToArray();
        int sourceIndex = Array.FindIndex(rows.ToArray(), row => row.SourceId == sourceId);
        if (sourceIndex >= 0 && rows[sourceIndex].IsMainModel)
        {
            int mainBoundaryIndex = candidates.Count(row =>
                row.Top + row.Height / 2 < pointerPosition.Y);
            return new SourceDropTarget(
                AttachmentPlacement.AfterMainModel,
                0,
                mainBoundaryIndex,
                mainBoundaryIndex);
        }

        int mainIndex = Array.FindIndex(candidates, static row => row.IsMainModel);
        SourceDropRow? main = mainIndex >= 0 ? candidates[mainIndex] : null;
        AttachmentPlacement placement = sourcePlacement;
        if (main is { } mainRow)
        {
            placement = pointerPosition.Y <= mainRow.Top + mainRow.Height / 2
                ? AttachmentPlacement.AfterMainModel
                : AttachmentPlacement.BeforeMainModel;
        }

        SourceDropRow[] group = candidates
            .Where(row => !row.IsMainModel && row.Placement == placement)
            .ToArray();
        int visualIndex = group.Count(row =>
            row.Top + row.Height / 2 < pointerPosition.Y);
        return new SourceDropTarget(
            placement,
            group.Length - visualIndex,
            visualIndex);
    }

    private Grid CreateMainModelSourceRow(
        int level,
        MenuNode entry,
        string label,
        StackPanel ownerPanel)
    {
        MainWindowViewModel currentViewModel = viewModel
            ?? throw new InvalidOperationException("The menu workspace is not attached.");
        MenuSourceActions actions = entry.SourceActions
            ?? throw new InvalidOperationException("A source row requires source actions.");
        var row = new Grid
        {
            ColumnDefinitions = actions.IsMainModel
                ? new ColumnDefinitions("*,38,38")
                : new ColumnDefinitions("*,38,38"),
            MinHeight = 42,
        };
        var selectButton = new Button
        {
            Theme = (ControlTheme)this.FindResource("MenuRowButtonTheme")!,
            Content = CreateRowContent(entry, label),
            Command = entry.Command,
            CommandParameter = entry.CommandParameter,
            BorderThickness = new Avalonia.Thickness(1),
            FontSize = 14,
            FontWeight = entry.IsSelected ? FontWeight.SemiBold : FontWeight.Normal,
        };
        selectButton.Classes.Set("selected", entry.IsSelected);
        AutomationProperties.SetAutomationId(selectButton, $"menu.{entry.Id}");
        AutomationProperties.SetName(selectButton, entry.AutomationName ?? label);
        if (!entry.Children.IsEmpty)
        {
            selectButton.Click += (_, _) =>
            {
                currentViewModel.SelectMenuNode(level, entry.Id);
            };
        }
        row.Children.Add(selectButton);

        ToggleButton visibility = CreateSourceToggle(
            actions.IsMainModel
                ? "scene.main-model.visibility"
                : $"scene.source.{actions.SourceId:N}.visibility",
            actions.IsVisible,
            "Icon.Lucide.Eye",
            "Icon.Lucide.EyeOff",
            actions.IsMainModel
                ? "Tooltip.Scene.HideMainModel"
                : "Tooltip.Scene.HideAttachment",
            actions.IsMainModel
                ? "Tooltip.Scene.ShowMainModel"
                : "Tooltip.Scene.ShowAttachment",
            actions.SetVisibilityAsync);
        Grid.SetColumn(visibility, 1);
        row.Children.Add(visibility);

        ToggleButton sourceLock = CreateSourceToggle(
            actions.IsMainModel
                ? "scene.main-model.lock"
                : $"scene.source.{actions.SourceId:N}.lock",
            actions.IsLocked,
            "Icon.Lucide.Lock",
            "Icon.Lucide.Unlock",
            actions.IsMainModel
                ? "Tooltip.Scene.UnlockMainModel"
                : "Tooltip.Scene.UnlockAttachment",
            actions.IsMainModel
                ? "Tooltip.Scene.LockMainModel"
                : "Tooltip.Scene.LockAttachment",
            actions.SetLockAsync);
        Grid.SetColumn(sourceLock, 2);
        row.Children.Add(sourceLock);

        var sourceRow = new SourceRowVisual(
            actions.SourceId,
            actions.Placement,
            actions.OrderIndex ?? 0,
            ownerPanel,
            row,
            actions);
        sourceRows.Add(sourceRow);

        row.AddHandler(
            InputElement.PointerPressedEvent,
            (_, args) => OnSourcePointerPressed(row, args),
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        row.AddHandler(
            InputElement.PointerMovedEvent,
            (_, args) => OnSourcePointerMoved(row, args),
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        row.AddHandler(
            InputElement.PointerReleasedEvent,
            (_, args) => OnSourcePointerReleased(row, args),
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        row.AddHandler(
            InputElement.PointerCaptureLostEvent,
            (_, _) => OnSourcePointerCaptureLost(row),
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        return row;
    }

    private void OnSourcePointerPressed(Grid row, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(row).Properties.IsLeftButtonPressed
            || sourceRows.FirstOrDefault(candidate => ReferenceEquals(candidate.Row, row)) is not { } source)
        {
            return;
        }

        sourceDrag = new SourceDragState(source, args.GetPosition(source.Panel).Y, false);
        row.Children.OfType<Button>().FirstOrDefault()?.Classes.Set("dragging", false);
        if (logger.IsEnabled(LogLevel.Debug))
        {
            CascadingMenuWorkspaceLog.DragStarted(logger, source.SourceId);
        }
    }

    private void OnSourcePointerMoved(Grid row, PointerEventArgs args)
    {
        if (sourceDrag is not { Source.Row: var activeRow } drag
            || !ReferenceEquals(activeRow, row)
            || !args.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
        {
            return;
        }

        double y = args.GetPosition(drag.Source.Panel).Y;
        if (!drag.IsDragging && Math.Abs(y - drag.StartY) < 6)
        {
            return;
        }

        sourceDrag = drag with { IsDragging = true };
        if (!drag.IsDragging)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                CascadingMenuWorkspaceLog.DragThresholdReached(
                    logger,
                    drag.Source.SourceId);
            }
        }
        row.Classes.Set("dragging", true);
        row.Children.OfType<Button>().FirstOrDefault()?.Classes.Set("dragging", true);
        row.Opacity = 0.78;
        row.RenderTransform = new TranslateTransform(8, 0);
        row.ZIndex = 100;
        UpdateSourceDropTarget(drag.Source, args.GetPosition(drag.Source.Panel));
        args.Handled = true;
    }

    private async void OnSourcePointerReleased(Grid row, PointerReleasedEventArgs args)
    {
        if (sourceDrag is not { Source.Row: var activeRow } drag
            || !ReferenceEquals(activeRow, row))
        {
            return;
        }

        SourceDropTarget? target = drag.DropTarget;
        sourceDrag = null;
        ClearSourceDragVisuals(row);
        if (!drag.IsDragging)
        {
            return;
        }

        args.Handled = true;
        target ??= CalculateSourceDropTarget(
            drag.Source.SourceId,
            drag.Source.Placement,
            args.GetPosition(drag.Source.Panel),
            GetSourceDropRows(drag.Source.Panel));
        try
        {
            if (drag.Source.IsMainModel && drag.Source.Actions.MoveMainToAsync is not null)
            {
                await drag.Source.Actions.MoveMainToAsync(target.Value.MainModelBoundaryIndex)
                    .ConfigureAwait(true);
            }
            else if (drag.Source.Actions.MoveToAsync is not null)
            {
                await drag.Source.Actions.MoveToAsync(target.Value.Placement, target.Value.DestinationIndex)
                    .ConfigureAwait(true);
            }
            else if (drag.Source.Actions.MoveAsync is not null)
            {
                await drag.Source.Actions.MoveAsync(target.Value.DestinationIndex).ConfigureAwait(true);
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                CascadingMenuWorkspaceLog.DragCompleted(
                    logger,
                    drag.Source.SourceId,
                    target.Value.Placement,
                    target.Value.DestinationIndex);
            }
        }
        catch (OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                CascadingMenuWorkspaceLog.DragCancelled(
                    logger,
                    drag.Source.SourceId);
            }
        }
        catch (Exception)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                CascadingMenuWorkspaceLog.DragFailed(
                    logger,
                    drag.Source.SourceId);
            }
            Refresh();
        }
    }

    private void OnSourcePointerCaptureLost(Grid row)
    {
        if (sourceDrag is not { Source.Row: var activeRow }
            || !ReferenceEquals(activeRow, row))
        {
            return;
        }

        Guid sourceId = sourceDrag.Source.SourceId;
        sourceDrag = null;
        ClearSourceDragVisuals(row);
        if (logger.IsEnabled(LogLevel.Debug))
        {
            CascadingMenuWorkspaceLog.DragCancelled(logger, sourceId);
        }
    }

    private void UpdateSourceDropTarget(SourceRowVisual source, Point pointerPosition)
    {
        SourceDropTarget? previousTarget = sourceDrag?.DropTarget;
        SourceDropTarget target = CalculateSourceDropTarget(
            source.SourceId,
            source.Placement,
            pointerPosition,
            GetSourceDropRows(source.Panel));
        sourceDrag = sourceDrag is { } drag
            ? drag with { DropTarget = target }
            : sourceDrag;
        if (previousTarget != target)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                CascadingMenuWorkspaceLog.DropTargetChanged(
                    logger,
                    source.SourceId,
                    target.Placement,
                    target.DestinationIndex);
            }
        }

        if (previousTarget == target
            && insertionIndicator is not null
            && ReferenceEquals(insertionPanel, source.Panel))
        {
            return;
        }

        RemoveInsertionIndicator();

        int insertionIndex = CalculateInsertionChildIndex(source, target);
        var indicator = new Border
        {
            Height = 3,
            MinHeight = 3,
            Margin = new Avalonia.Thickness(4, 1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = (IBrush)this.FindResource("CategoryApricot")!,
            CornerRadius = new Avalonia.CornerRadius(2),
            IsHitTestVisible = false,
            Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Border.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(120),
                },
            },
            Opacity = 0.95,
        };
        AutomationProperties.SetAutomationId(indicator, "menu.scene.source.insertion-indicator");
        source.Panel.Children.Insert(insertionIndex, indicator);
        insertionPanel = source.Panel;
        insertionIndicator = indicator;
    }

    private SourceDropRow[] GetSourceDropRows(StackPanel panel) => sourceRows
        .Where(source => ReferenceEquals(source.Panel, panel))
        .Select(source => new SourceDropRow(
            source.SourceId,
            source.Placement,
            source.IsMainModel,
            source.Row.Bounds.Top,
            source.Row.Bounds.Height))
        .ToArray();

    private int CalculateInsertionChildIndex(SourceRowVisual source, SourceDropTarget target)
    {
        SourceRowVisual[] candidates = sourceRows
            .Where(candidate => ReferenceEquals(candidate.Panel, source.Panel)
                && candidate.SourceId != source.SourceId)
            .OrderBy(candidate => candidate.Row.Bounds.Top)
            .ToArray();
        if (source.IsMainModel)
        {
            SourceRowVisual[] attachments = candidates
                .Where(static candidate => !candidate.IsMainModel)
                .ToArray();
            if (target.VisualIndex < attachments.Length)
            {
                int index = source.Panel.Children.IndexOf(attachments[target.VisualIndex].Row);
                return index < 0 ? source.Panel.Children.Count : index;
            }

            if (attachments.Length > 0)
            {
                int index = source.Panel.Children.IndexOf(attachments[^1].Row);
                return index < 0 ? source.Panel.Children.Count : index + 1;
            }

            int sourceIndex = source.Panel.Children.IndexOf(source.Row);
            return sourceIndex >= 0 ? sourceIndex : source.Panel.Children.Count;
        }

        SourceRowVisual[] group = candidates
            .Where(candidate => !candidate.IsMainModel && candidate.Placement == target.Placement)
            .ToArray();
        if (target.VisualIndex < group.Length)
        {
            int index = source.Panel.Children.IndexOf(group[target.VisualIndex].Row);
            return index < 0 ? source.Panel.Children.Count : index;
        }

        if (group.Length > 0)
        {
            int index = source.Panel.Children.IndexOf(group[^1].Row);
            return index < 0 ? source.Panel.Children.Count : index + 1;
        }

        SourceRowVisual? main = candidates.FirstOrDefault(static candidate => candidate.IsMainModel);
        if (main is not null)
        {
            int mainIndex = source.Panel.Children.IndexOf(main.Row);
            if (mainIndex >= 0)
            {
                return target.Placement == AttachmentPlacement.AfterMainModel
                    ? mainIndex
                    : mainIndex + 1;
            }
        }

        return source.Panel.Children.Count;
    }

    private void RemoveInsertionIndicator()
    {
        if (insertionIndicator is not null && insertionPanel is not null)
        {
            insertionPanel.Children.Remove(insertionIndicator);
        }

        insertionIndicator = null;
        insertionPanel = null;
    }

    private void ClearSourceDragVisuals(Grid? row = null)
    {
        RemoveInsertionIndicator();
        Grid? activeRow = row
            ?? sourceDrag?.Source.Row;
        if (activeRow is null)
        {
            return;
        }

        activeRow.Classes.Set("dragging", false);
        activeRow.Children.OfType<Button>().FirstOrDefault()?.Classes.Set("dragging", false);
        activeRow.Opacity = 1;
        activeRow.RenderTransform = null;
        activeRow.ZIndex = 0;
    }

    private ToggleButton CreateSourceToggle(
        string automationId,
        bool initialValue,
        string checkedIconKey,
        string uncheckedIconKey,
        string checkedTooltipKey,
        string uncheckedTooltipKey,
        Func<bool, Task<bool>> change)
    {
        var toggle = new ToggleButton
        {
            Theme = (ControlTheme)this.FindResource("ShellIconButtonTheme")!,
            Width = 36,
            Height = 36,
            Padding = new Avalonia.Thickness(8),
            IsChecked = initialValue,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(toggle, automationId);

        void ApplyPresentation(bool value)
        {
            toggle.Content = CreateIcon(value ? checkedIconKey : uncheckedIconKey, 17);
            string tooltip = viewModel!.Localization.GetString(
                value ? checkedTooltipKey : uncheckedTooltipKey);
            ToolTip.SetTip(toggle, tooltip);
            AutomationProperties.SetName(toggle, tooltip);
        }

        bool committedValue = initialValue;
        bool applyingCommittedValue = false;
        toggle.IsCheckedChanged += async (_, _) =>
        {
            if (applyingCommittedValue)
            {
                return;
            }

            bool requestedValue = toggle.IsChecked == true;
            ApplyPresentation(requestedValue);
            bool changed = await change(requestedValue);
            if (changed)
            {
                committedValue = requestedValue;
                return;
            }

            applyingCommittedValue = true;
            toggle.IsChecked = committedValue;
            ApplyPresentation(committedValue);
            applyingCommittedValue = false;
        };
        ApplyPresentation(initialValue);
        return toggle;
    }

    private Grid CreateRowContent(MenuNode entry, string label)
    {
        IBrush primaryBrush = entry.InformationState == MenuInformationState.Neutral
            ? (IBrush)this.FindResource("TextPrimary")!
            : GetInformationStateBrush(entry.InformationState);
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(17) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });

        LucideIcon icon = CreateIcon(entry.IconResourceKey, 17);
        icon.Stroke = primaryBrush;
        content.Children.Add(icon);

        Control labelContent;
        if (string.IsNullOrWhiteSpace(entry.SecondaryText))
        {
            labelContent = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = primaryBrush,
            };
        }
        else
        {
            var labels = new StackPanel { Spacing = 1 };
            labels.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = primaryBrush,
                TextWrapping = TextWrapping.Wrap,
            });
            labels.Children.Add(new TextBlock
            {
                Text = entry.SecondaryText,
                FontSize = 12,
                Foreground = (IBrush)this.FindResource("TextSecondary")!,
                TextWrapping = TextWrapping.Wrap,
            });
            labelContent = labels;
        }
        Grid.SetColumn(labelContent, 2);
        content.Children.Add(labelContent);

        if (!entry.Children.IsEmpty)
        {
            LucideIcon chevron = CreateIcon("Icon.Lucide.ChevronRight", 14);
            chevron.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(chevron, 3);
            content.Children.Add(chevron);
        }
        else if (entry.IsSelected)
        {
            LucideIcon selectedIndicator = CreateIcon("Icon.Lucide.CircleDot", 14);
            selectedIndicator.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(selectedIndicator, 3);
            content.Children.Add(selectedIndicator);
        }

        return content;
    }

    private LucideIcon CreateIcon(string resourceKey, double size) => new()
    {
        Width = size,
        Height = size,
        Data = (Geometry)this.FindResource(resourceKey)!,
        Stroke = (IBrush)this.FindResource("TextPrimary")!,
    };

    private ToggleSwitch CreateToggle(
        MenuNode entry,
        string label,
        bool initialValue,
        Func<bool, Task<bool>> change)
    {
        var toggle = new ToggleSwitch
        {
            Theme = (ControlTheme)this.FindResource("MenuRowToggleTheme")!,
            Content = CreateToggleContent(entry, label),
            FontSize = 14,
            IsChecked = initialValue,
        };
        AutomationProperties.SetAutomationId(toggle, entry.Id);
        AutomationProperties.SetName(toggle, label);
        bool desiredValue = initialValue;
        bool changeInProgress = false;
        bool applyingCommittedValue = false;
        toggle.IsCheckedChanged += async (_, _) =>
        {
            if (applyingCommittedValue)
            {
                return;
            }

            desiredValue = toggle.IsChecked == true;
            if (changeInProgress)
            {
                return;
            }

            changeInProgress = true;
            try
            {
                while (desiredValue != initialValue)
                {
                    bool valueToPersist = desiredValue;
                    bool changed = await change(valueToPersist);
                    if (!changed)
                    {
                        desiredValue = initialValue;
                        applyingCommittedValue = true;
                        toggle.IsChecked = initialValue;
                        applyingCommittedValue = false;
                        break;
                    }

                    initialValue = valueToPersist;
                }
            }
            finally
            {
                changeInProgress = false;
            }
        };
        return toggle;
    }

    private Grid CreateToggleContent(MenuNode entry, string label)
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(17) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        content.Children.Add(CreateIcon(entry.IconResourceKey, 17));

        var labelText = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (IBrush)this.FindResource("TextPrimary")!,
        };
        Grid.SetColumn(labelText, 2);
        content.Children.Add(labelText);
        return content;
    }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        InputResolution? resolution = inputActions.Resolve(
            new InputContext(
                [InputBindingScope.MenuColumn, InputBindingScope.MenuWorkspace],
                IsNativeControl: args.Source is TextBox or ToggleSwitch),
            InputGesture.KeyChord(args.Key.ToString(), ToInputModifiers(args.KeyModifiers)));
        if (resolution is not { ShouldConsume: true } action)
        {
            return;
        }

        if (action.ActionId is BuiltInInputActions.MenuCloseAll
            or BuiltInInputActions.MenuCloseSubmenu)
        {
            HandleEscape();
            args.Handled = true;
        }
        else if (action.ActionId == BuiltInInputActions.MenuOpenSubmenu
            && args.Source is Button
            {
                Command: not null,
                CommandParameter: MainWindowViewModel.MenuSelection selection,
            } button)
        {
            bool isAlreadyOpen = viewModel is not null
                && selection.Level < viewModel.Navigation.SelectedMenuPath.Length
                && StringComparer.Ordinal.Equals(
                    viewModel.Navigation.SelectedMenuPath[selection.Level],
                    selection.NodeId);
            if (!isAlreadyOpen)
            {
                button.Command.Execute(button.CommandParameter);
            }

            FocusFirstEntry(selection.Level + 1);
            args.Handled = true;
        }
    }

    private static InputModifiers ToInputModifiers(KeyModifiers modifiers)
    {
        InputModifiers result = InputModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control)) result |= InputModifiers.Control;
        if (modifiers.HasFlag(KeyModifiers.Alt)) result |= InputModifiers.Alt;
        if (modifiers.HasFlag(KeyModifiers.Shift)) result |= InputModifiers.Shift;
        if (modifiers.HasFlag(KeyModifiers.Meta)) result |= InputModifiers.Meta;
        return result;
    }

    private static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin;

    private sealed record MenuChoiceDisplay(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private void FocusFirstEntry(int panelLevel)
    {
        if (panelLevel >= panels.Children.Count)
        {
            return;
        }

        Control? firstEntry = panels.Children[panelLevel]
            .GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(static control => control is Button or ToggleSwitch);
        firstEntry?.Focus();
    }

    private void FocusMenuItem(string nodeId)
    {
        Button? button = panels.Children
            .SelectMany(static panel => panel.GetVisualDescendants())
            .OfType<Button>()
            .FirstOrDefault(candidate => AutomationProperties.GetAutomationId(candidate) == $"menu.{nodeId}");
        button?.Focus();
    }

}

internal static partial class CascadingMenuWorkspaceLog
{
    [LoggerMessage(
        EventId = 6900,
        Level = LogLevel.Debug,
        EventName = "SceneSourceDragStarted",
        Message = "Scene source drag started for {SourceId}")]
    internal static partial void DragStarted(ILogger logger, Guid sourceId);

    [LoggerMessage(
        EventId = 6901,
        Level = LogLevel.Debug,
        EventName = "SceneSourceDragThresholdReached",
        Message = "Scene source drag threshold reached for {SourceId}")]
    internal static partial void DragThresholdReached(ILogger logger, Guid sourceId);

    [LoggerMessage(
        EventId = 6902,
        Level = LogLevel.Debug,
        EventName = "SceneSourceDropTargetChanged",
        Message = "Scene source drop target changed for {SourceId}: placement={Placement}, destinationIndex={DestinationIndex}")]
    internal static partial void DropTargetChanged(
        ILogger logger,
        Guid sourceId,
        AttachmentPlacement placement,
        int destinationIndex);

    [LoggerMessage(
        EventId = 6903,
        Level = LogLevel.Information,
        EventName = "SceneSourceDragCompleted",
        Message = "Scene source drag completed for {SourceId}: placement={Placement}, destinationIndex={DestinationIndex}")]
    internal static partial void DragCompleted(
        ILogger logger,
        Guid sourceId,
        AttachmentPlacement placement,
        int destinationIndex);

    [LoggerMessage(
        EventId = 6904,
        Level = LogLevel.Debug,
        EventName = "SceneSourceDragCancelled",
        Message = "Scene source drag cancelled for {SourceId}")]
    internal static partial void DragCancelled(ILogger logger, Guid sourceId);

    [LoggerMessage(
        EventId = 6905,
        Level = LogLevel.Warning,
        EventName = "SceneSourceDragFailed",
        Message = "Scene source drag failed for {SourceId}")]
    internal static partial void DragFailed(ILogger logger, Guid sourceId);
}
