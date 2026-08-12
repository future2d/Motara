using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Motara.App.Collaboration;
using Motara.App.Localization;

namespace Motara.App.Controls;

internal sealed partial class LocalProfileSettingsControl : UserControl
{
    private readonly TextBlock displayNameLabel;
    private readonly TextBlock validationText;
    private readonly TextBox displayNameInput;
    private readonly Button copyIdentityCodeButton;
    private readonly Button cancelButton;
    private readonly Button confirmButton;
    private LocalProfileSettingsViewModel? viewModel;
    private LocalizationManager? localization;
    private bool updating;

    public LocalProfileSettingsControl()
    {
        AvaloniaXamlLoader.Load(this);
        displayNameLabel = this.FindControl<TextBlock>("DisplayNameLabel")!;
        validationText = this.FindControl<TextBlock>("ValidationText")!;
        displayNameInput = this.FindControl<TextBox>("DisplayNameInput")!;
        copyIdentityCodeButton = this.FindControl<Button>("CopyIdentityCodeButton")!;
        cancelButton = this.FindControl<Button>("CancelButton")!;
        confirmButton = this.FindControl<Button>("ConfirmButton")!;
        displayNameInput.TextChanged += (_, _) =>
        {
            if (!updating && viewModel is not null)
            {
                viewModel.DisplayName = displayNameInput.Text ?? string.Empty;
            }
        };
        copyIdentityCodeButton.Click += async (_, _) => await CopyIdentityCodeAsync();
        cancelButton.Click += (_, _) => viewModel?.Cancel();
        confirmButton.Click += async (_, _) => await SaveAsync();
    }

    internal Control InitialFocus => displayNameInput;

    internal void Attach(LocalProfileSettingsViewModel value, LocalizationManager resources)
    {
        viewModel = value ?? throw new ArgumentNullException(nameof(value));
        localization = resources ?? throw new ArgumentNullException(nameof(resources));
        value.PropertyChanged += OnViewModelPropertyChanged;
        displayNameLabel.Text = resources.GetString("Workspace.Collaboration.Profile.DisplayName");
        copyIdentityCodeButton.Content = resources.GetString(
            "Workspace.Collaboration.Profile.CopyDeviceId");
        cancelButton.Content = resources.GetString("Command.Cancel");
        confirmButton.Content = resources.GetString("Command.Confirm");
        AutomationProperties.SetName(
            copyIdentityCodeButton,
            resources.GetString("Workspace.Collaboration.Profile.CopyDeviceId"));
        Update();
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

    private async Task SaveAsync()
    {
        if (viewModel is null)
        {
            return;
        }

        viewModel.DisplayName = displayNameInput.Text ?? string.Empty;
        await viewModel.SaveAsync(CancellationToken.None);
        Update();
    }

    private async Task CopyIdentityCodeAsync()
    {
        if (string.IsNullOrWhiteSpace(viewModel?.DeviceId)
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(viewModel.DeviceId);
            viewModel.RecordIdentityCodeCopyResult(true, null);
        }
        catch (Exception exception)
        {
            viewModel.RecordIdentityCodeCopyResult(false, exception.GetType().Name);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args) => Update();

    private void Update()
    {
        if (viewModel is null || localization is null)
        {
            return;
        }

        updating = true;
        displayNameInput.Text = viewModel.DisplayName;
        updating = false;
        string? resourceKey = viewModel.ValidationResourceKey;
        validationText.Text = resourceKey is null
            ? string.Empty
            : localization.GetString(resourceKey);
        displayNameInput.IsEnabled = !viewModel.IsBusy;
        copyIdentityCodeButton.IsEnabled = !viewModel.IsBusy
            && !string.IsNullOrWhiteSpace(viewModel.DeviceId);
        cancelButton.IsEnabled = !viewModel.IsBusy;
        confirmButton.IsEnabled = !viewModel.IsBusy;
    }
}
