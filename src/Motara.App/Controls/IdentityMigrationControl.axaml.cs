using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Motara.App.Collaboration;
using Motara.App.Localization;

namespace Motara.App.Controls;

internal sealed partial class IdentityMigrationControl : UserControl
{
    private readonly TextBlock summaryText;
    private readonly TextBlock fileLabel;
    private readonly TextBlock passphraseLabel;
    private readonly TextBlock confirmationLabel;
    private readonly TextBlock inspectionText;
    private readonly TextBlock statusText;
    private readonly TextBlock replacementText;
    private readonly TextBox filePathText;
    private readonly TextBox passphraseInput;
    private readonly TextBox confirmationInput;
    private readonly StackPanel confirmationPanel;
    private readonly Border inspectionPanel;
    private readonly Border replacementOverlay;
    private readonly Button chooseFileButton;
    private readonly Button cancelButton;
    private readonly Button primaryButton;
    private readonly Button cancelReplacementButton;
    private readonly Button confirmReplacementButton;
    private IdentityMigrationViewModel? viewModel;
    private LocalizationManager? localization;
    private bool updating;

    public IdentityMigrationControl()
    {
        AvaloniaXamlLoader.Load(this);
        summaryText = this.FindControl<TextBlock>("SummaryText")!;
        fileLabel = this.FindControl<TextBlock>("FileLabel")!;
        passphraseLabel = this.FindControl<TextBlock>("PassphraseLabel")!;
        confirmationLabel = this.FindControl<TextBlock>("ConfirmationLabel")!;
        inspectionText = this.FindControl<TextBlock>("InspectionText")!;
        statusText = this.FindControl<TextBlock>("StatusText")!;
        replacementText = this.FindControl<TextBlock>("ReplacementText")!;
        filePathText = this.FindControl<TextBox>("FilePathText")!;
        passphraseInput = this.FindControl<TextBox>("PassphraseInput")!;
        confirmationInput = this.FindControl<TextBox>("ConfirmationInput")!;
        confirmationPanel = this.FindControl<StackPanel>("ConfirmationPanel")!;
        inspectionPanel = this.FindControl<Border>("InspectionPanel")!;
        replacementOverlay = this.FindControl<Border>("ReplacementOverlay")!;
        chooseFileButton = this.FindControl<Button>("ChooseFileButton")!;
        cancelButton = this.FindControl<Button>("CancelButton")!;
        primaryButton = this.FindControl<Button>("PrimaryButton")!;
        cancelReplacementButton = this.FindControl<Button>("CancelReplacementButton")!;
        confirmReplacementButton = this.FindControl<Button>("ConfirmReplacementButton")!;
        passphraseInput.TextChanged += (_, _) => { if (!updating && viewModel is not null) viewModel.Passphrase = passphraseInput.Text ?? string.Empty; };
        confirmationInput.TextChanged += (_, _) => { if (!updating && viewModel is not null) viewModel.ConfirmationPassphrase = confirmationInput.Text ?? string.Empty; };
        chooseFileButton.Click += async (_, _) => await ChooseFileAsync();
        cancelButton.Click += (_, _) => viewModel?.Close();
        primaryButton.Click += async (_, _) => await ExecutePrimaryAsync();
        cancelReplacementButton.Click += (_, _) => viewModel?.CancelReplacement();
        confirmReplacementButton.Click += async (_, _) =>
        {
            if (viewModel is not null)
            {
                await viewModel.ConfirmImportAsync(CancellationToken.None);
            }
        };
    }

    internal Control InitialFocus => chooseFileButton;

    internal void Attach(IdentityMigrationViewModel value, LocalizationManager resources)
    {
        viewModel = value ?? throw new ArgumentNullException(nameof(value));
        localization = resources ?? throw new ArgumentNullException(nameof(resources));
        value.PropertyChanged += OnViewModelPropertyChanged;
        summaryText.Text = resources.GetString(value.Mode == IdentityMigrationMode.Export
            ? "Workspace.Collaboration.Identity.ExportSummary"
            : "Workspace.Collaboration.Identity.ImportSummary");
        fileLabel.Text = resources.GetString("Workspace.Collaboration.Identity.File");
        passphraseLabel.Text = resources.GetString("Workspace.Collaboration.Identity.Passphrase");
        confirmationLabel.Text = resources.GetString("Workspace.Collaboration.Identity.ConfirmPassphrase");
        chooseFileButton.Content = resources.GetString("Command.ChooseFile");
        cancelButton.Content = resources.GetString("Command.Cancel");
        primaryButton.Content = resources.GetString(value.Mode == IdentityMigrationMode.Export
            ? "Command.Export"
            : "Command.Inspect");
        cancelReplacementButton.Content = resources.GetString("Command.Cancel");
        confirmReplacementButton.Content = resources.GetString("Command.Confirm");
        replacementText.Text = resources.GetString("Workspace.Collaboration.Identity.ReplaceConfirmation");
        AutomationProperties.SetAutomationId(primaryButton, "workspace.collaboration.identity.primary");
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

    private async Task ChooseFileAsync()
    {
        if (viewModel is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        string? path;
        var fileType = new FilePickerFileType("Motara identity")
        {
            Patterns = ["*.motara.identity"],
        };
        if (viewModel.Mode == IdentityMigrationMode.Export)
        {
            IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = "motara-identity.motara.identity",
                DefaultExtension = "motara.identity",
                FileTypeChoices = [fileType],
            });
            path = file?.TryGetLocalPath();
        }
        else
        {
            IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = [fileType],
            });
            path = files.Count == 0 ? null : files[0].TryGetLocalPath();
        }

        if (path is not null)
        {
            viewModel.FilePath = path;
        }
    }

    private async Task ExecutePrimaryAsync()
    {
        if (viewModel is null)
        {
            return;
        }

        if (viewModel.Mode == IdentityMigrationMode.Export)
        {
            await viewModel.ExportAsync(CancellationToken.None);
        }
        else
        {
            await viewModel.PrepareImportAsync(CancellationToken.None);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        if (viewModel is null || localization is null)
        {
            return;
        }

        updating = true;
        filePathText.Text = viewModel.FilePath;
        passphraseInput.Text = viewModel.Passphrase;
        confirmationInput.Text = viewModel.ConfirmationPassphrase;
        confirmationPanel.IsVisible = viewModel.Mode == IdentityMigrationMode.Export;
        inspectionPanel.IsVisible = viewModel.Inspection is not null;
        inspectionText.Text = viewModel.Inspection is { } inspection
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                localization.GetString("Workspace.Collaboration.Identity.Inspection"),
                inspection.DeviceIdSummary,
                inspection.FriendCount,
                inspection.RelationshipSecretCount,
                inspection.ConsumedInviteCount)
            : string.Empty;
        statusText.Text = viewModel.StatusResourceKey is { } key
            ? localization.GetString(key)
            : string.Empty;
        replacementOverlay.IsVisible = viewModel.IsReplacementConfirmationVisible;
        primaryButton.IsEnabled = !viewModel.IsBusy;
        chooseFileButton.IsEnabled = !viewModel.IsBusy;
        confirmReplacementButton.IsEnabled = !viewModel.IsBusy;
        updating = false;
    }
}
