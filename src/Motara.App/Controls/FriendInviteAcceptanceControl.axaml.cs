using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Motara.App.Collaboration;
using Motara.App.Localization;
using Motara.Collaboration.Friends;

namespace Motara.App.Controls;

internal sealed partial class FriendInviteAcceptanceControl : UserControl
{
    private readonly TextBox inviteInput;
    private readonly TextBox nameInput;
    private readonly TextBlock inviteLabel;
    private readonly TextBlock nameLabel;
    private readonly TextBlock validationText;
    private readonly TextBlock resultText;
    private readonly Border resultPanel;
    private readonly Button cancelButton;
    private readonly Button confirmButton;
    private readonly Button acknowledgeButton;
    private FriendInviteAcceptanceViewModel? viewModel;
    private LocalizationManager? localization;
    private bool isRefreshing;

    public FriendInviteAcceptanceControl()
    {
        AvaloniaXamlLoader.Load(this);
        inviteInput = this.FindControl<TextBox>("InviteInput")!;
        nameInput = this.FindControl<TextBox>("NameInput")!;
        inviteLabel = this.FindControl<TextBlock>("InviteLabel")!;
        nameLabel = this.FindControl<TextBlock>("NameLabel")!;
        validationText = this.FindControl<TextBlock>("ValidationText")!;
        resultText = this.FindControl<TextBlock>("ResultText")!;
        resultPanel = this.FindControl<Border>("ResultPanel")!;
        cancelButton = this.FindControl<Button>("CancelButton")!;
        confirmButton = this.FindControl<Button>("ConfirmButton")!;
        acknowledgeButton = this.FindControl<Button>("AcknowledgeButton")!;
        cancelButton.Click += (_, _) => viewModel?.Close();
        acknowledgeButton.Click += (_, _) => viewModel?.Close();
        confirmButton.Click += OnConfirmClicked;
    }

    internal Control InitialFocus => inviteInput;

    internal void Attach(FriendInviteAcceptanceViewModel value, LocalizationManager resources)
    {
        viewModel = value;
        localization = resources;
        value.PropertyChanged += OnPropertyChanged;
        inviteLabel.Text = resources.GetString("Workspace.Collaboration.InviteLink");
        nameLabel.Text = resources.GetString("Workspace.Collaboration.ContactName");
        cancelButton.Content = resources.GetString("Command.Cancel");
        confirmButton.Content = resources.GetString("Command.Confirm");
        acknowledgeButton.Content = resources.GetString("Command.Confirm");
        AutomationProperties.SetAutomationId(inviteInput, "workspace.collaboration.invite.input");
        AutomationProperties.SetAutomationId(confirmButton, "workspace.collaboration.invite.confirm");
        RefreshFromViewModel(
            nameof(FriendInviteAcceptanceViewModel.InvitationText),
            nameof(FriendInviteAcceptanceViewModel.LocalDisplayName));
        inviteInput.TextChanged += OnInvitationTextChanged;
        nameInput.TextChanged += OnDisplayNameTextChanged;
    }

    internal void Detach()
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnPropertyChanged;
        }

        inviteInput.TextChanged -= OnInvitationTextChanged;
        nameInput.TextChanged -= OnDisplayNameTextChanged;
        viewModel = null;
        localization = null;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not nameof(FriendInviteAcceptanceViewModel.InvitationText)
            and not nameof(FriendInviteAcceptanceViewModel.LocalDisplayName))
        {
            return;
        }

        string? propertyName = args.PropertyName;
        Dispatcher.UIThread.Post(() => RefreshFromViewModel(propertyName));
    }

    private void OnInvitationTextChanged(object? sender, TextChangedEventArgs args)
    {
        if (!isRefreshing && viewModel is not null)
        {
            viewModel.InvitationText = inviteInput.Text ?? string.Empty;
        }
    }

    private void OnDisplayNameTextChanged(object? sender, TextChangedEventArgs args)
    {
        if (!isRefreshing && viewModel is not null)
        {
            viewModel.LocalDisplayName = nameInput.Text ?? string.Empty;
        }
    }

    private void RefreshFromViewModel(params string?[] propertyNames)
    {
        if (viewModel is null)
        {
            return;
        }

        isRefreshing = true;
        try
        {
            foreach (string? propertyName in propertyNames)
            {
                if (propertyName == nameof(FriendInviteAcceptanceViewModel.InvitationText))
                {
                    inviteInput.Text = viewModel.InvitationText;
                }
                else if (propertyName == nameof(FriendInviteAcceptanceViewModel.LocalDisplayName))
                {
                    nameInput.Text = viewModel.LocalDisplayName;
                }
            }
        }
        finally
        {
            isRefreshing = false;
        }
    }

    private async void OnConfirmClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (viewModel is null || localization is null)
        {
            return;
        }

        if (!viewModel.ValidateCandidate())
        {
            validationText.Text = localization.GetString(viewModel.ValidationResourceKey!);
            return;
        }

        FriendAcceptanceResult result = await viewModel.AcceptAsync(CancellationToken.None);
        resultText.Text = localization.GetString($"Workspace.Collaboration.Result.{result.Code}");
        resultPanel.IsVisible = true;
    }
}
