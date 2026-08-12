using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Motara.App.Collaboration;
using Motara.App.Localization;

namespace Motara.App.Controls;

internal sealed partial class FriendDetailsControl : UserControl
{
    private readonly TextBlock identityText;
    private readonly TextBlock trustText;
    private readonly TextBlock nameLabel;
    private readonly TextBlock noteLabel;
    private readonly TextBlock handshakeHeading;
    private readonly TextBlock incomingLabel;
    private readonly TextBlock statusText;
    private readonly TextBlock confirmationText;
    private readonly TextBox nameInput;
    private readonly TextBox noteInput;
    private readonly TextBox offerOutput;
    private readonly TextBox incomingInput;
    private readonly TextBox responseOutput;
    private readonly StackPanel handshakePanel;
    private readonly Border confirmationOverlay;
    private readonly Button saveButton;
    private readonly Button createOfferButton;
    private readonly Button acceptOfferButton;
    private readonly Button completeOfferButton;
    private readonly Button blockButton;
    private readonly Button deleteButton;
    private readonly Button closeButton;
    private readonly Button cancelConfirmationButton;
    private readonly Button confirmActionButton;
    private FriendDetailsViewModel? viewModel;
    private LocalizationManager? localization;
    private bool updating;

    public FriendDetailsControl()
    {
        AvaloniaXamlLoader.Load(this);
        identityText = this.FindControl<TextBlock>("IdentityText")!;
        trustText = this.FindControl<TextBlock>("TrustText")!;
        nameLabel = this.FindControl<TextBlock>("NameLabel")!;
        noteLabel = this.FindControl<TextBlock>("NoteLabel")!;
        handshakeHeading = this.FindControl<TextBlock>("HandshakeHeading")!;
        incomingLabel = this.FindControl<TextBlock>("IncomingLabel")!;
        statusText = this.FindControl<TextBlock>("StatusText")!;
        confirmationText = this.FindControl<TextBlock>("ConfirmationText")!;
        nameInput = this.FindControl<TextBox>("NameInput")!;
        noteInput = this.FindControl<TextBox>("NoteInput")!;
        offerOutput = this.FindControl<TextBox>("OfferOutput")!;
        incomingInput = this.FindControl<TextBox>("IncomingInput")!;
        responseOutput = this.FindControl<TextBox>("ResponseOutput")!;
        handshakePanel = this.FindControl<StackPanel>("HandshakePanel")!;
        confirmationOverlay = this.FindControl<Border>("ConfirmationOverlay")!;
        saveButton = this.FindControl<Button>("SaveButton")!;
        createOfferButton = this.FindControl<Button>("CreateOfferButton")!;
        acceptOfferButton = this.FindControl<Button>("AcceptOfferButton")!;
        completeOfferButton = this.FindControl<Button>("CompleteOfferButton")!;
        blockButton = this.FindControl<Button>("BlockButton")!;
        deleteButton = this.FindControl<Button>("DeleteButton")!;
        closeButton = this.FindControl<Button>("CloseButton")!;
        cancelConfirmationButton = this.FindControl<Button>("CancelConfirmationButton")!;
        confirmActionButton = this.FindControl<Button>("ConfirmActionButton")!;
        nameInput.TextChanged += (_, _) => { if (!updating && viewModel is not null) viewModel.DisplayName = nameInput.Text ?? string.Empty; };
        noteInput.TextChanged += (_, _) => { if (!updating && viewModel is not null) viewModel.Note = noteInput.Text ?? string.Empty; };
        incomingInput.TextChanged += (_, _) => { if (!updating && viewModel is not null) viewModel.IncomingHandshakeText = incomingInput.Text ?? string.Empty; };
        saveButton.Click += async (_, _) => await RunAsync(token => viewModel!.SaveMetadataAsync(token));
        createOfferButton.Click += async (_, _) => await RunAsync(token => viewModel!.BeginOfferAsync(token));
        acceptOfferButton.Click += async (_, _) => await RunAsync(token => viewModel!.AcceptOfferAsync(token));
        completeOfferButton.Click += async (_, _) => await RunAsync(token => viewModel!.CompleteOfferAsync(token));
        blockButton.Click += (_, _) => viewModel?.RequestBlock();
        deleteButton.Click += (_, _) => viewModel?.RequestDelete();
        closeButton.Click += (_, _) => viewModel?.Close();
        cancelConfirmationButton.Click += (_, _) => CancelConfirmation();
        confirmActionButton.Click += async (_, _) => await ConfirmActionAsync();
    }

    internal Control InitialFocus => nameInput;

    internal void Attach(FriendDetailsViewModel value, LocalizationManager resources)
    {
        viewModel = value ?? throw new ArgumentNullException(nameof(value));
        localization = resources ?? throw new ArgumentNullException(nameof(resources));
        value.PropertyChanged += OnViewModelPropertyChanged;
        nameLabel.Text = resources.GetString("Workspace.Collaboration.Friend.Name");
        noteLabel.Text = resources.GetString("Workspace.Collaboration.Friend.Note");
        handshakeHeading.Text = resources.GetString("Workspace.Collaboration.Handshake.Title");
        incomingLabel.Text = resources.GetString("Workspace.Collaboration.Handshake.Incoming");
        saveButton.Content = resources.GetString("Command.Save");
        createOfferButton.Content = resources.GetString("Workspace.Collaboration.Handshake.CreateOffer");
        acceptOfferButton.Content = resources.GetString("Workspace.Collaboration.Handshake.AcceptOffer");
        completeOfferButton.Content = resources.GetString("Workspace.Collaboration.Handshake.CompleteOffer");
        blockButton.Content = resources.GetString("Workspace.Collaboration.Friend.Block");
        deleteButton.Content = resources.GetString("Command.Delete");
        closeButton.Content = resources.GetString("Command.Close");
        cancelConfirmationButton.Content = resources.GetString("Command.Cancel");
        confirmActionButton.Content = resources.GetString("Command.Confirm");
        AutomationProperties.SetAutomationId(nameInput, "workspace.collaboration.friend.name");
        Refresh();
    }

    internal void Detach()
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        viewModel = null;
        localization = null;
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (viewModel is null)
        {
            return;
        }

        await action(CancellationToken.None);
        Refresh();
    }

    private void CancelConfirmation()
    {
        if (viewModel?.IsBlockConfirmationVisible == true) viewModel.CancelBlock();
        if (viewModel?.IsDeleteConfirmationVisible == true) viewModel.CancelDelete();
    }

    private async Task ConfirmActionAsync()
    {
        if (viewModel?.IsBlockConfirmationVisible == true)
        {
            await RunAsync(token => viewModel.ConfirmBlockAsync(token));
        }
        else if (viewModel?.IsDeleteConfirmationVisible == true)
        {
            await RunAsync(token => viewModel.ConfirmDeleteAsync(token));
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args) => Refresh();

    private void Refresh()
    {
        if (viewModel is null || localization is null)
        {
            return;
        }

        updating = true;
        identityText.Text = ShortDeviceId(viewModel.Contact.DeviceId.Value);
        trustText.Text = localization.GetString($"Menu.Collaboration.Status.{viewModel.Contact.Status}");
        nameInput.Text = viewModel.DisplayName;
        noteInput.Text = viewModel.Note;
        offerOutput.Text = viewModel.OutgoingOfferText;
        incomingInput.Text = viewModel.IncomingHandshakeText;
        responseOutput.Text = viewModel.OutgoingResponseText;
        statusText.Text = viewModel.StatusResourceKey is { } key ? localization.GetString(key) : string.Empty;
        handshakePanel.IsVisible = viewModel.CanHandshake;
        completeOfferButton.IsEnabled = viewModel.HasPendingOffer && !viewModel.IsBusy;
        confirmationOverlay.IsVisible = viewModel.IsBlockConfirmationVisible || viewModel.IsDeleteConfirmationVisible;
        confirmationText.Text = localization.GetString(viewModel.IsDeleteConfirmationVisible
            ? "Workspace.Collaboration.Friend.DeleteConfirmation"
            : "Workspace.Collaboration.Friend.BlockConfirmation");
        saveButton.IsEnabled = !viewModel.IsBusy;
        createOfferButton.IsEnabled = !viewModel.IsBusy;
        acceptOfferButton.IsEnabled = !viewModel.IsBusy;
        blockButton.IsEnabled = !viewModel.IsBusy && viewModel.Contact.Status != CollaborationContactStatus.Blocked;
        deleteButton.IsEnabled = !viewModel.IsBusy;
        updating = false;
    }

    private static string ShortDeviceId(string value) => value.Length <= 20
        ? value
        : $"{value[..13]}...{value[^6..]}";
}
