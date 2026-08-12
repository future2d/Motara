using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Motara.App.Localization;
using Motara.App.ViewModels;

namespace Motara.App.Controls;

internal sealed partial class SceneDeleteConfirmationControl : UserControl
{
    private TextBlock message = null!;
    private Button cancel = null!;
    private Button confirm = null!;

    public SceneDeleteConfirmationControl()
    {
        AvaloniaXamlLoader.Load(this);
        message = this.FindControl<TextBlock>("Message")!;
        cancel = this.FindControl<Button>("Cancel")!;
        confirm = this.FindControl<Button>("Confirm")!;
    }

    public Control InitialFocus => confirm;

    public void Attach(SceneDeleteConfirmationViewModel value, LocalizationManager localization)
    {
        message.Text = string.Format(
            localization.Culture,
            localization.GetString("Workspace.Scene.DeleteMessage"),
            value.SceneName);
        cancel.Content = localization.GetString("Command.Cancel");
        confirm.Content = localization.GetString("Command.Delete");
        cancel.Command = value.CancelCommand;
        confirm.Command = value.ConfirmCommand;
        AutomationProperties.SetAutomationId(cancel, "workspace.scene.delete.cancel");
        AutomationProperties.SetAutomationId(confirm, "workspace.scene.delete.confirm");
        AutomationProperties.SetName(cancel, cancel.Content?.ToString());
        AutomationProperties.SetName(confirm, confirm.Content?.ToString());
    }

    public void Detach()
    {
        cancel.Command = null;
        confirm.Command = null;
    }
}
