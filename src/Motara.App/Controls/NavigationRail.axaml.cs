using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Motara.App.Shell;
using Motara.App.ViewModels;

namespace Motara.App.Controls;

public sealed partial class NavigationRail : UserControl
{
    private const double ButtonSize = 44;
    private const double ButtonSpacing = 8;
    private const double HorizontalPadding = 14;
    private const double VerticalChrome = 68;
    private readonly Grid destinationButtons;
    private readonly Button closeButton;
    private MainWindowViewModel? viewModel;
    private double availableHeight = double.PositiveInfinity;

    public event EventHandler? LayoutChanged;

    public int DestinationColumnCount { get; private set; } = 1;

    public double RequiredWidth => HorizontalPadding
        + (DestinationColumnCount * ButtonSize)
        + ((DestinationColumnCount - 1) * ButtonSpacing);

    public NavigationRail()
    {
        AvaloniaXamlLoader.Load(this);
        destinationButtons = this.FindControl<Grid>("DestinationButtons")!;
        closeButton = this.FindControl<Button>("CloseButton")!;
        closeButton.Click += (_, _) => viewModel?.CloseNavigationCommand.Execute(null);
    }

    public void Attach(MainWindowViewModel value)
    {
        viewModel = value;
        BuildDestinationButtons();
        ConfigureCloseButton();
    }

    public void Refresh()
    {
        UpdateLayoutMode();
        BuildDestinationButtons();
    }

    public void SetAvailableHeight(double height)
    {
        if (!double.IsFinite(height) || height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        availableHeight = height;
        int previous = DestinationColumnCount;
        UpdateLayoutMode();
        if (previous != DestinationColumnCount)
        {
            BuildDestinationButtons();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void BuildDestinationButtons()
    {
        destinationButtons.Children.Clear();
        destinationButtons.RowDefinitions.Clear();
        destinationButtons.ColumnDefinitions.Clear();
        if (viewModel is null)
        {
            return;
        }

        for (int column = 0; column < DestinationColumnCount; column++)
        {
            destinationButtons.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(ButtonSize)));
        }

        int rowCount = (int)Math.Ceiling((double)viewModel.Destinations.Length / DestinationColumnCount);
        for (int row = 0; row < rowCount; row++)
        {
            destinationButtons.RowDefinitions.Add(new RowDefinition(new GridLength(ButtonSize)));
        }

        for (int index = 0; index < viewModel.Destinations.Length; index++)
        {
            MainWindowViewModel.DestinationViewModel destination = viewModel.Destinations[index];
            bool isSelected = viewModel.Navigation.SelectedDestination == destination.Id;
            var button = new ToggleButton
            {
                Name = $"Destination{destination.Id}",
                Theme = (ControlTheme)this.FindResource("ShellIconButtonTheme")!,
                Command = viewModel.SelectDestinationCommand,
                CommandParameter = destination.Id,
                IsChecked = isSelected,
                BorderThickness = new Avalonia.Thickness(1),
            };
            button.Content = CreateIcon(GetIconResourceKey(destination.Id), button);
            AutomationProperties.SetAutomationId(button, $"navigation.{destination.Id.ToString().ToLowerInvariant()}");
            AutomationProperties.SetName(button, destination.AccessibilityName);
            ShellToolTip.Configure(this, button, destination.Label, PlacementMode.Right);
            Grid.SetRow(button, index / DestinationColumnCount);
            Grid.SetColumn(button, index % DestinationColumnCount);
            destinationButtons.Children.Add(button);
        }
    }

    private void UpdateLayoutMode()
    {
        int count = viewModel?.Destinations.Length ?? 0;
        double singleColumnHeight = VerticalChrome
            + (count * ButtonSize)
            + (Math.Max(0, count - 1) * ButtonSpacing);
        DestinationColumnCount = singleColumnHeight <= availableHeight ? 1 : 2;
        Width = RequiredWidth;
    }

    private void ConfigureCloseButton()
    {
        if (viewModel is null)
        {
            return;
        }

        string label = viewModel.Localization.GetString("Accessibility.CloseNavigation");
        AutomationProperties.SetName(closeButton, label);
        ShellToolTip.Configure(
            this,
            closeButton,
            viewModel.Localization.GetString("Command.CloseNavigation"),
            PlacementMode.Right);
    }

    private LucideIcon CreateIcon(string resourceKey, ToggleButton owner)
    {
        object geometry = this.FindResource(resourceKey)!;
        var icon = new LucideIcon
        {
            Width = 20,
            Height = 20,
            Data = (Geometry)geometry,
        };
        icon.Bind(LucideIcon.StrokeProperty, new Binding(nameof(owner.Foreground)) { Source = owner });
        return icon;
    }

    private static string GetIconResourceKey(NavigationDestination destination) => destination switch
    {
        NavigationDestination.Session => "Icon.Lucide.Activity",
        NavigationDestination.Collaboration => "Icon.Lucide.Users",
        NavigationDestination.Model => "Icon.Lucide.User",
        NavigationDestination.Scene => "Icon.Lucide.Layers",
        NavigationDestination.Tracking => "Icon.Lucide.Radio",
        NavigationDestination.Mapping => "Icon.Lucide.Waypoints",
        NavigationDestination.Effects => "Icon.Lucide.WandSparkles",
        NavigationDestination.Output => "Icon.Lucide.MonitorUp",
        NavigationDestination.Shortcuts => "Icon.Lucide.Keyboard",
        NavigationDestination.Settings => "Icon.Lucide.Settings",
        NavigationDestination.Developer => "Icon.Lucide.Wrench",
        _ => throw new ArgumentOutOfRangeException(nameof(destination)),
    };
}
