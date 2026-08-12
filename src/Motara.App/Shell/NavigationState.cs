using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Motara.App.Shell;

/// <summary>Owns the framework-free rail, destination, and cascading-menu selection state.</summary>
public sealed class NavigationState : INotifyPropertyChanged
{
    private static readonly ImmutableArray<NavigationDestination> StandardDestinations =
    [
        NavigationDestination.Session,
        NavigationDestination.Model,
        NavigationDestination.Scene,
        NavigationDestination.Tracking,
        NavigationDestination.Mapping,
        NavigationDestination.Effects,
        NavigationDestination.Output,
        NavigationDestination.Collaboration,
        NavigationDestination.Shortcuts,
        NavigationDestination.Settings,
    ];

    private static readonly ImmutableArray<NavigationDestination> DeveloperDestinations =
        StandardDestinations.Add(NavigationDestination.Developer);

    private bool isDeveloperModeEnabled;

    public NavigationState(
        bool isDeveloperModeEnabled = false,
        bool isRailVisible = true)
    {
        this.isDeveloperModeEnabled = isDeveloperModeEnabled;
        IsRailVisible = isRailVisible;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsRailVisible { get; private set; }

    public NavigationDestination? SelectedDestination { get; private set; }

    public ImmutableArray<string> SelectedMenuPath { get; private set; } = [];

    public ImmutableArray<NavigationDestination> VisibleDestinations => isDeveloperModeEnabled
        ? DeveloperDestinations
        : StandardDestinations;

    public int OpenMenuLevelCount => SelectedDestination.HasValue
        ? SelectedMenuPath.Length + 1
        : 0;

    public void SelectDestination(NavigationDestination destination)
    {
        if (!Enum.IsDefined(destination))
        {
            throw new ArgumentOutOfRangeException(nameof(destination));
        }

        if (destination == NavigationDestination.Developer && !isDeveloperModeEnabled)
        {
            return;
        }

        if (SelectedDestination == destination)
        {
            ClearSelection();
            return;
        }

        SelectedDestination = destination;
        SelectedMenuPath = [];
        OnPropertyChanged(nameof(SelectedDestination));
        OnPropertyChanged(nameof(SelectedMenuPath));
        OnPropertyChanged(nameof(OpenMenuLevelCount));
    }

    public void SelectMenuNode(int level, string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        if (!SelectedDestination.HasValue)
        {
            throw new InvalidOperationException();
        }

        if (level < 0 || level > SelectedMenuPath.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        if (level < SelectedMenuPath.Length
            && StringComparer.Ordinal.Equals(SelectedMenuPath[level], nodeId))
        {
            SelectedMenuPath = SelectedMenuPath.Take(level).ToImmutableArray();
        }
        else
        {
            SelectedMenuPath = SelectedMenuPath.Take(level).Append(nodeId).ToImmutableArray();
        }

        OnPropertyChanged(nameof(SelectedMenuPath));
        OnPropertyChanged(nameof(OpenMenuLevelCount));
    }

    public void CloseNavigation()
    {
        if (IsRailVisible)
        {
            IsRailVisible = false;
            OnPropertyChanged(nameof(IsRailVisible));
        }

        ClearSelection();
    }

    public void RestoreNavigation()
    {
        if (IsRailVisible)
        {
            return;
        }

        IsRailVisible = true;
        OnPropertyChanged(nameof(IsRailVisible));
    }

    public void SetDeveloperMode(bool isEnabled)
    {
        if (isDeveloperModeEnabled == isEnabled)
        {
            return;
        }

        isDeveloperModeEnabled = isEnabled;
        OnPropertyChanged(nameof(VisibleDestinations));
        if (!isEnabled && SelectedDestination == NavigationDestination.Developer)
        {
            ClearSelection();
        }
    }

    private void ClearSelection()
    {
        bool hadDestination = SelectedDestination.HasValue;
        bool hadPath = !SelectedMenuPath.IsEmpty;
        SelectedDestination = null;
        SelectedMenuPath = [];

        if (hadDestination)
        {
            OnPropertyChanged(nameof(SelectedDestination));
        }

        if (hadPath)
        {
            OnPropertyChanged(nameof(SelectedMenuPath));
        }

        if (hadDestination || hadPath)
        {
            OnPropertyChanged(nameof(OpenMenuLevelCount));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
