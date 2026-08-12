using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Motara.App.Localization;
using Motara.App.ViewModels;

namespace Motara.App.Controls;

internal sealed partial class SceneEffectEditor : UserControl
{
    private SceneEffectEditorViewModel? viewModel;
    private LocalizationManager? localization;
    private Action? close;
    private Border closeOverlay = null!;
    private Border deleteOverlay = null!;
    private NumericUpDown radius = null!;
    private ToggleSwitch enabled = null!;

    public SceneEffectEditor()
    {
        AvaloniaXamlLoader.Load(this);
        radius = this.FindControl<NumericUpDown>("Radius")!;
        enabled = this.FindControl<ToggleSwitch>("Enabled")!;
        closeOverlay = this.FindControl<Border>("CloseOverlay")!;
        deleteOverlay = this.FindControl<Border>("DeleteOverlay")!;
        radius.ValueChanged += (_, args) =>
        {
            if (viewModel is not null && args.NewValue is decimal value) viewModel.Radius = (double)value;
        };
        enabled.IsCheckedChanged += (_, _) =>
        {
            if (viewModel is not null) viewModel.IsEnabled = enabled.IsChecked == true;
        };
        this.FindControl<Button>("Apply")!.Click += async (_, _) =>
        {
            if (viewModel is not null) await viewModel.ApplyAsync(CancellationToken.None);
            Refresh();
        };
        this.FindControl<Button>("Cancel")!.Click += async (_, _) =>
        {
            if (viewModel is not null && await viewModel.RequestCloseAsync(CancellationToken.None)) close?.Invoke();
            Refresh();
        };
        this.FindControl<Button>("Delete")!.Click += (_, _) =>
        {
            viewModel?.RequestDelete();
            Refresh();
        };
        this.FindControl<Button>("CloseCancel")!.Click += (_, _) => { viewModel?.CancelClose(); Refresh(); };
        this.FindControl<Button>("Discard")!.Click += (_, _) => { viewModel?.DiscardChanges(); close?.Invoke(); };
        this.FindControl<Button>("DeleteCancel")!.Click += (_, _) => { viewModel?.CancelDelete(); Refresh(); };
        this.FindControl<Button>("DeleteConfirm")!.Click += async (_, _) =>
        {
            if (viewModel is not null && await viewModel.ConfirmDeleteAsync(CancellationToken.None)) close?.Invoke();
        };
        AutomationProperties.SetAutomationId(radius, "workspace.scene-effect.blur-radius");
        AutomationProperties.SetAutomationId(enabled, "workspace.scene-effect.enabled");
        AutomationProperties.SetAutomationId(this.FindControl<Button>("Apply")!, "workspace.scene-effect.apply");
    }

    public void Attach(SceneEffectEditorViewModel value, LocalizationManager localization, Action close)
    {
        viewModel = value;
        this.localization = localization;
        this.close = close;
        this.FindControl<TextBlock>("Description")!.Text = localization.GetString("Workspace.SceneEffect.Description");
        this.FindControl<TextBlock>("RadiusLabel")!.Text = localization.GetString("Workspace.SceneEffect.Radius");
        this.FindControl<TextBlock>("CloseMessage")!.Text = localization.GetString("Workspace.SceneEffect.UnsavedChanges");
        this.FindControl<TextBlock>("DeleteMessage")!.Text = localization.GetString("Workspace.SceneEffect.DeleteConfirmation");
        enabled.Content = localization.GetString("Workspace.SceneEffect.Enabled");
        this.FindControl<Button>("Delete")!.Content = localization.GetString("Command.Delete");
        this.FindControl<Button>("Cancel")!.Content = localization.GetString("Command.Cancel");
        this.FindControl<Button>("Apply")!.Content = localization.GetString("Command.Apply");
        this.FindControl<Button>("CloseCancel")!.Content = localization.GetString("Command.Cancel");
        this.FindControl<Button>("Discard")!.Content = localization.GetString("Command.Discard");
        this.FindControl<Button>("DeleteCancel")!.Content = localization.GetString("Command.Cancel");
        this.FindControl<Button>("DeleteConfirm")!.Content = localization.GetString("Command.Confirm");
        Refresh();
    }

    private void Refresh()
    {
        if (viewModel is null) return;
        radius.Value = (decimal)viewModel.Radius;
        enabled.IsChecked = viewModel.IsEnabled;
        this.FindControl<Button>("Delete")!.IsEnabled = viewModel.CanDelete;
        closeOverlay.IsVisible = viewModel.IsCloseConfirmationVisible;
        deleteOverlay.IsVisible = viewModel.IsDeleteConfirmationVisible;
    }
}
