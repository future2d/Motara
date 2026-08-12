using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Motara.App.Localization;
using Motara.App.ViewModels;

namespace Motara.App.Controls;

internal sealed partial class ModelRenderingFallbackNoticeControl : UserControl
{
    private readonly TextBlock messageText;
    private readonly TextBlock reasonText;

    public ModelRenderingFallbackNoticeControl()
    {
        AvaloniaXamlLoader.Load(this);
        messageText = this.FindControl<TextBlock>("MessageText")!;
        reasonText = this.FindControl<TextBlock>("ReasonText")!;
        AutomationProperties.SetAutomationId(
            messageText,
            "workspace.model-rendering-fallback.message");
        AutomationProperties.SetAutomationId(
            reasonText,
            "workspace.model-rendering-fallback.reason");
    }

    internal void Attach(
        ModelRenderingFallbackNotice notice,
        LocalizationManager localization)
    {
        ArgumentNullException.ThrowIfNull(notice);
        ArgumentNullException.ThrowIfNull(localization);
        messageText.Text = localization.GetString("Dialog.ModelRenderingFallback.Message");
        reasonText.Text = localization.GetString(
            $"Dialog.ModelRenderingFallback.{notice.Reason}");
    }
}
