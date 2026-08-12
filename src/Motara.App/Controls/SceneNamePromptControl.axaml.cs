using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Motara.App.Localization;
using Motara.App.ViewModels;

namespace Motara.App.Controls;

internal sealed partial class SceneNamePromptControl : UserControl
{
    private TextBlock label = null!;
    private TextBox input = null!;
    private Button cancel = null!;
    private Button submit = null!;
    private SceneNamePromptViewModel? prompt;

    public SceneNamePromptControl()
    {
        AvaloniaXamlLoader.Load(this);
        label = this.FindControl<TextBlock>("Label")!;
        input = this.FindControl<TextBox>("Input")!;
        cancel = this.FindControl<Button>("Cancel")!;
        submit = this.FindControl<Button>("Submit")!;
        input.TextChanged += OnTextChanged;
    }

    public Control InitialFocus => input;

    public void Attach(SceneNamePromptViewModel value, LocalizationManager localization)
    {
        prompt = value;
        label.Text = localization.GetString("Workspace.Scene.NamePrompt");
        input.Text = value.Name;
        cancel.Content = localization.GetString("Command.Cancel");
        submit.Content = localization.GetString(value.IsRename ? "Command.Save" : "Command.Create");
        cancel.Command = value.CancelCommand;
        submit.Command = value.SubmitCommand;
        AutomationProperties.SetAutomationId(input, "workspace.scene.name");
        AutomationProperties.SetAutomationId(cancel, "workspace.scene.cancel");
        AutomationProperties.SetAutomationId(submit, "workspace.scene.submit");
        AutomationProperties.SetName(input, label.Text);
        AutomationProperties.SetName(cancel, cancel.Content?.ToString());
        AutomationProperties.SetName(submit, submit.Content?.ToString());
    }

    public void Detach()
    {
        input.TextChanged -= OnTextChanged;
        prompt = null;
    }

    private void OnTextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs args)
    {
        if (prompt is not null)
        {
            prompt.Name = input.Text ?? string.Empty;
        }
    }
}
