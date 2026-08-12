using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Motara.App.Collaboration;
using Motara.App.Localization;

namespace Motara.App.Controls;

internal sealed partial class FriendInviteGenerationControl : UserControl
{
    private readonly TextBlock summaryText;
    private readonly TextBlock invitationLink;
    private readonly Button closeButton;
    private readonly Button copyButton;
    private FriendInviteGenerationViewModel? viewModel;

    public FriendInviteGenerationControl()
    {
        AvaloniaXamlLoader.Load(this);
        summaryText = this.FindControl<TextBlock>("SummaryText")!;
        invitationLink = this.FindControl<TextBlock>("InvitationLink")!;
        closeButton = this.FindControl<Button>("CloseButton")!;
        copyButton = this.FindControl<Button>("CopyButton")!;
        copyButton.Click += OnCopyClicked;
        closeButton.Click += (_, _) => viewModel?.Close();
    }

    internal Control InitialFocus => copyButton;

    internal void Attach(FriendInviteGenerationViewModel value, LocalizationManager localization)
    {
        viewModel = value;
        value.PropertyChanged += OnPropertyChanged;
        summaryText.Text = localization.GetString("Workspace.Collaboration.GenerateSummary");
        closeButton.Content = localization.GetString("Command.Close");
        copyButton.Content = localization.GetString("Command.Copy");
        invitationLink.Text = InvitationLinkDisplay.Format(value.InvitationLink);
        AutomationProperties.SetAutomationId(copyButton, "workspace.collaboration.invite.copy");
    }

    internal void Detach()
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnPropertyChanged;
        }

        viewModel = null;
    }

    private async void OnCopyClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (viewModel?.InvitationLink is not { Length: > 0 } text
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(text);
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(FriendInviteGenerationViewModel.InvitationLink))
        {
            Dispatcher.UIThread.Post(() => invitationLink.Text = InvitationLinkDisplay.Format(
                viewModel?.InvitationLink ?? string.Empty));
        }
    }
}
