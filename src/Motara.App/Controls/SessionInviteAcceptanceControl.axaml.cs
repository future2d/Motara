using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Motara.App.Collaboration;
using Motara.App.Localization;

namespace Motara.App.Controls;

internal sealed partial class SessionInviteAcceptanceControl : UserControl
{
    private readonly TextBlock hostText;
    private readonly TextBlock policyText;
    private readonly TextBlock expiryText;
    private readonly TextBlock statusText;
    private readonly Button acknowledgeButton;
    private readonly Button joinButton;
    private readonly Border distributionConsentPanel;
    private readonly TextBlock distributionConsentText;
    private readonly Button declineDistributionButton;
    private readonly Button confirmDistributionButton;
    private SessionInviteAcceptanceViewModel? viewModel;

    public SessionInviteAcceptanceControl()
    {
        AvaloniaXamlLoader.Load(this);
        hostText = this.FindControl<TextBlock>("HostText")!;
        policyText = this.FindControl<TextBlock>("PolicyText")!;
        expiryText = this.FindControl<TextBlock>("ExpiryText")!;
        statusText = this.FindControl<TextBlock>("StatusText")!;
        acknowledgeButton = this.FindControl<Button>("AcknowledgeButton")!;
        joinButton = this.FindControl<Button>("JoinButton")!;
        distributionConsentPanel = this.FindControl<Border>("DistributionConsentPanel")!;
        distributionConsentText = this.FindControl<TextBlock>("DistributionConsentText")!;
        declineDistributionButton = this.FindControl<Button>("DeclineDistributionButton")!;
        confirmDistributionButton = this.FindControl<Button>("ConfirmDistributionButton")!;
        acknowledgeButton.Click += (_, _) => viewModel?.Acknowledge();
        joinButton.Click += (_, _) =>
        {
            viewModel?.PrepareJoin();
            Refresh();
        };
        declineDistributionButton.Click += (_, _) => viewModel?.DeclineModelDistributionConsent();
        confirmDistributionButton.Click += (_, _) =>
        {
            viewModel?.ConfirmModelDistributionConsent();
            Refresh();
        };
    }

    internal Control InitialFocus => acknowledgeButton;

    internal void Attach(SessionInviteAcceptanceViewModel value, LocalizationManager resources)
    {
        viewModel = value ?? throw new ArgumentNullException(nameof(value));
        ArgumentNullException.ThrowIfNull(resources);
        if (value.Invite is { } invite)
        {
            hostText.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                resources.GetString("Workspace.Collaboration.Session.Host"),
                ShortDeviceId(invite.HostDeviceId.Value));
            policyText.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                resources.GetString("Workspace.Collaboration.Session.PolicyFormat"),
                resources.GetString($"Workspace.Collaboration.Session.Policy.{invite.JoinPolicy}"));
            expiryText.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                resources.GetString("Workspace.Collaboration.Session.Expires"),
                invite.ExpiresAtUtc.ToLocalTime());
            statusText.Text = resources.GetString("Workspace.Collaboration.Session.ReadyToJoin");
        }
        else
        {
            hostText.Text = string.Empty;
            policyText.Text = string.Empty;
            expiryText.Text = string.Empty;
            statusText.Text = resources.GetString(value.ValidationResourceKey
                ?? "Workspace.Collaboration.InvalidInvite");
        }

        acknowledgeButton.Content = resources.GetString("Command.Confirm");
        joinButton.Content = resources.GetString("Workspace.Collaboration.Session.Join");
        distributionConsentText.Text = resources.GetString(
            "Workspace.Collaboration.Session.DistributionConsent");
        declineDistributionButton.Content = resources.GetString("Command.Cancel");
        confirmDistributionButton.Content = resources.GetString("Command.Confirm");
        AutomationProperties.SetAutomationId(acknowledgeButton, "workspace.collaboration.session.acknowledge");
        Refresh();
    }

    internal void Detach() => viewModel = null;

    private void Refresh()
    {
        if (viewModel is null)
        {
            return;
        }

        joinButton.IsVisible = viewModel.CanJoin && !viewModel.IsAwaitingDistributionConsent && !viewModel.IsActive;
        distributionConsentPanel.IsVisible = viewModel.IsAwaitingDistributionConsent;
        acknowledgeButton.IsVisible = !viewModel.CanJoin || viewModel.IsActive;
    }

    private static string ShortDeviceId(string value) => value.Length <= 20
        ? value
        : $"{value[..13]}...{value[^6..]}";
}
