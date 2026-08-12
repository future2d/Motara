using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using System.ComponentModel;
using Motara.App.Localization;
using Motara.App.ViewModels;

namespace Motara.App.Controls;

internal sealed record ParameterPriorityDisplayItem(int Priority, string Label);

internal sealed partial class ParameterPriorityWorkspaceControl : UserControl
{
    private readonly ListBox providers;
    private readonly Button moveUp;
    private readonly Button moveDown;
    private readonly Border confirmationOverlay;
    private readonly TextBlock confirmationMessage;
    private ParameterPriorityWorkspaceViewModel? viewModel;
    private LocalizationManager? localization;
    private Action? close;
    private bool confirmingRestore;

    public ParameterPriorityWorkspaceControl()
    {
        AvaloniaXamlLoader.Load(this);
        providers = this.FindControl<ListBox>("Providers")!;
        moveUp = this.FindControl<Button>("MoveUp")!;
        moveDown = this.FindControl<Button>("MoveDown")!;
        confirmationOverlay = this.FindControl<Border>("ConfirmationOverlay")!;
        confirmationMessage = this.FindControl<TextBlock>("ConfirmationMessage")!;
        providers.SelectionChanged += (_, _) =>
        {
            if (viewModel is null) return;
            viewModel.SelectedIndex = providers.SelectedIndex;
            RefreshButtons();
        };
        moveUp.Click += (_, _) => Move(-1);
        moveDown.Click += (_, _) => Move(1);
        this.FindControl<Button>("Restore")!.Click += (_, _) => ShowRestoreConfirmation();
        this.FindControl<Button>("Apply")!.Click += async (_, _) => await ApplyAsync();
        this.FindControl<Button>("ConfirmationCancel")!.Click += (_, _) => CancelConfirmation();
        this.FindControl<Button>("ConfirmationConfirm")!.Click += (_, _) => Confirm();
        AutomationProperties.SetAutomationId(providers, "workspace.parameter-priority.providers");
        AutomationProperties.SetAutomationId(moveUp, "workspace.parameter-priority.move-up");
        AutomationProperties.SetAutomationId(moveDown, "workspace.parameter-priority.move-down");
        AutomationProperties.SetAutomationId(this.FindControl<Button>("Restore")!, "workspace.parameter-priority.restore");
        AutomationProperties.SetAutomationId(this.FindControl<Button>("Apply")!, "workspace.parameter-priority.apply");
    }

    internal void Attach(
        ParameterPriorityWorkspaceViewModel value,
        LocalizationManager localization,
        Action close)
    {
        viewModel = value;
        this.localization = localization;
        this.close = close;
        DataContext = value;
        value.PropertyChanged += OnViewModelPropertyChanged;
        this.FindControl<TextBlock>("Description")!.Text = localization.GetString("Workspace.ParameterPriority.Description");
        SetButton("MoveUp", "Workspace.ParameterPriority.MoveUp");
        SetButton("MoveDown", "Workspace.ParameterPriority.MoveDown");
        SetButton("Restore", "Command.RestoreDefaults");
        SetButton("Apply", "Command.Apply");
        SetButton("ConfirmationCancel", "Command.Cancel");
        SetButton("ConfirmationConfirm", "Command.Confirm");
        RefreshItems();
    }

    internal void Detach()
    {
        if (viewModel is not null) viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel = null;
        DataContext = null;
    }

    private void Move(int offset)
    {
        if (viewModel is null) return;
        bool changed = offset < 0 ? viewModel.MoveUp() : viewModel.MoveDown();
        if (!changed) return;
        RefreshItems();
        providers.SelectedIndex = viewModel.SelectedIndex;
    }

    private async Task ApplyAsync()
    {
        if (viewModel is null) return;
        _ = await viewModel.ApplyAsync(CancellationToken.None);
    }

    private void ShowRestoreConfirmation()
    {
        if (viewModel is null || localization is null) return;
        confirmingRestore = true;
        viewModel.RequestRestoreDefault();
        confirmationMessage.Text = localization.GetString("Workspace.ParameterPriority.RestoreConfirmation");
        confirmationOverlay.IsVisible = true;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ParameterPriorityWorkspaceViewModel.IsCloseConfirmationVisible)
            || viewModel?.IsCloseConfirmationVisible != true
            || localization is null)
        {
            return;
        }

        confirmingRestore = false;
        confirmationMessage.Text = localization.GetString("Workspace.ParameterPriority.CloseConfirmation");
        confirmationOverlay.IsVisible = true;
    }

    private void CancelConfirmation()
    {
        if (confirmingRestore) viewModel?.CancelRestoreDefault();
        else viewModel?.CancelClose();
        confirmationOverlay.IsVisible = false;
    }

    private void Confirm()
    {
        if (viewModel is null) return;
        if (confirmingRestore)
        {
            viewModel.ConfirmRestoreDefault();
            RefreshItems();
        }
        else
        {
            viewModel.DiscardAndClose();
            close?.Invoke();
        }
        confirmationOverlay.IsVisible = false;
    }

    private void RefreshItems()
    {
        if (viewModel is null || localization is null) return;
        providers.ItemsSource = viewModel.Items
            .Select((item, index) => new ParameterPriorityDisplayItem(
                index,
                localization.GetString(item.LabelResourceKey)))
            .ToArray();
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        moveUp.IsEnabled = viewModel?.CanMoveUp == true;
        moveDown.IsEnabled = viewModel?.CanMoveDown == true;
    }

    private void SetButton(string name, string key) =>
        this.FindControl<Button>(name)!.Content = localization!.GetString(key);
}
