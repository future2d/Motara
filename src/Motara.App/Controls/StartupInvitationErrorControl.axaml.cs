using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Motara.App.Collaboration;
using Motara.App.Localization;

namespace Motara.App.Controls;

internal sealed partial class StartupInvitationErrorControl : UserControl
{
    private readonly TextBlock messageText;
    private readonly Button closeButton;
    private StartupInvitationErrorViewModel? viewModel;

    public StartupInvitationErrorControl()
    {
        AvaloniaXamlLoader.Load(this);
        messageText = this.FindControl<TextBlock>("MessageText")!;
        closeButton = this.FindControl<Button>("CloseButton")!;
        closeButton.Click += (_, _) => viewModel?.Close();
    }

    internal Control InitialFocus => closeButton;

    internal void Attach(StartupInvitationErrorViewModel value, LocalizationManager resources)
    {
        viewModel = value ?? throw new ArgumentNullException(nameof(value));
        ArgumentNullException.ThrowIfNull(resources);
        messageText.Text = resources.GetString("Workspace.Collaboration.Startup.Invalid");
        closeButton.Content = resources.GetString("Command.Confirm");
    }

    internal void Detach() => viewModel = null;
}
