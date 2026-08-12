using Avalonia.Automation;
using Avalonia.Controls;

namespace Motara.App.Controls;

internal sealed class ModelAdvancedSettingsControl : UserControl
{
    internal ModelAdvancedSettingsControl()
    {
        Content = new Grid();
        AutomationProperties.SetAutomationId(this, "workspace.model-advanced");
    }
}
