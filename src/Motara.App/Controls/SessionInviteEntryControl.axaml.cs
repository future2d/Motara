using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Motara.App.Collaboration;
using Motara.App.Localization;

namespace Motara.App.Controls;

internal sealed partial class SessionInviteEntryControl : UserControl
{
    private readonly TextBlock summaryText;
    private readonly TextBlock inviteLabel;
    private readonly TextBox inviteInput;
    private readonly TextBlock validationText;
    private readonly Button cancelButton;
    private readonly Button confirmButton;
    private SessionInviteEntryViewModel? viewModel;
    private LocalizationManager? localization;
    private bool isRefreshing;

    public SessionInviteEntryControl()
    {
        AvaloniaXamlLoader.Load(this);
        summaryText = this.FindControl<TextBlock>("SummaryText")!;
        inviteLabel = this.FindControl<TextBlock>("InviteLabel")!;
        inviteInput = this.FindControl<TextBox>("InviteInput")!;
        validationText = this.FindControl<TextBlock>("ValidationText")!;
        cancelButton = this.FindControl<Button>("CancelButton")!;
        confirmButton = this.FindControl<Button>("ConfirmButton")!;
        cancelButton.Click += (_, _) => viewModel?.Close();
        confirmButton.Click += (_, _) => ValidateAndContinue();
        inviteInput.TextChanged += OnInvitationTextChanged;
    }

    internal Control InitialFocus => inviteInput;

    internal void Attach(SessionInviteEntryViewModel value, LocalizationManager resources)
    {
        viewModel = value ?? throw new ArgumentNullException(nameof(value));
        localization = resources ?? throw new ArgumentNullException(nameof(resources));
        value.PropertyChanged += OnPropertyChanged;
        summaryText.Text = resources.GetString("Workspace.Collaboration.Session.AcceptEntrySummary");
        inviteLabel.Text = resources.GetString("Workspace.Collaboration.InviteLink");
        cancelButton.Content = resources.GetString("Command.Cancel");
        confirmButton.Content = resources.GetString("Command.Accept");
        AutomationProperties.SetAutomationId(inviteInput, "workspace.collaboration.session.invite.input");
        AutomationProperties.SetAutomationId(confirmButton, "workspace.collaboration.session.invite.confirm");
        Refresh();
    }

    internal void Detach()
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnPropertyChanged;
        }

        viewModel = null;
        localization = null;
    }

    private void OnInvitationTextChanged(object? sender, TextChangedEventArgs args)
    {
        if (!isRefreshing && viewModel is not null)
        {
            viewModel.InvitationText = inviteInput.Text ?? string.Empty;
        }
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        Dispatcher.UIThread.Post(Refresh);

    private void ValidateAndContinue()
    {
        if (viewModel is null || localization is null)
        {
            return;
        }

        if (!viewModel.ValidateAndContinue())
        {
            validationText.Text = localization.GetString(viewModel.ValidationResourceKey!);
        }
    }

    private void Refresh()
    {
        if (viewModel is null)
        {
            return;
        }

        isRefreshing = true;
        try
        {
            inviteInput.Text = viewModel.InvitationText;
        }
        finally
        {
            isRefreshing = false;
        }
    }
}
