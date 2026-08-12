using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Motara.App.Collaboration;
using Motara.App.Localization;
using Motara.Collaboration.Invites;

namespace Motara.App.Controls;

internal sealed partial class SessionInviteGenerationControl : UserControl
{
    private readonly TextBlock summaryText;
    private readonly TextBlock policyLabel;
    private readonly TextBlock linkLabel;
    private readonly ComboBox policySelect;
    private readonly Button generateButton;
    private readonly Border generationConfirmationPanel;
    private readonly TextBlock generationConfirmationText;
    private readonly Button cancelGenerationButton;
    private readonly Button confirmGenerationButton;
    private readonly Button closeButton;
    private readonly TextBlock linkText;
    private readonly Button copyButton;
    private SessionInviteGenerationViewModel? viewModel;

    public SessionInviteGenerationControl()
    {
        AvaloniaXamlLoader.Load(this);
        summaryText = this.FindControl<TextBlock>("SummaryText")!;
        policyLabel = this.FindControl<TextBlock>("PolicyLabel")!;
        linkLabel = this.FindControl<TextBlock>("LinkLabel")!;
        policySelect = this.FindControl<ComboBox>("PolicySelect")!;
        generateButton = this.FindControl<Button>("GenerateButton")!;
        generationConfirmationPanel = this.FindControl<Border>("GenerationConfirmationPanel")!;
        generationConfirmationText = this.FindControl<TextBlock>("GenerationConfirmationText")!;
        cancelGenerationButton = this.FindControl<Button>("CancelGenerationButton")!;
        confirmGenerationButton = this.FindControl<Button>("ConfirmGenerationButton")!;
        closeButton = this.FindControl<Button>("CloseButton")!;
        linkText = this.FindControl<TextBlock>("LinkText")!;
        copyButton = this.FindControl<Button>("CopyButton")!;
        policySelect.SelectionChanged += (_, _) =>
        {
            if (viewModel is not null && policySelect.SelectedItem is PolicyOption option)
            {
                viewModel.JoinPolicy = option.Value;
            }
        };
        generateButton.Click += (_, _) =>
        {
            viewModel?.RequestGeneration();
            Refresh();
        };
        cancelGenerationButton.Click += (_, _) =>
        {
            viewModel?.CancelGeneration();
            Refresh();
        };
        confirmGenerationButton.Click += async (_, _) =>
        {
            if (viewModel is not null)
            {
                await viewModel.ConfirmGenerationAsync(CancellationToken.None);
            }
        };
        closeButton.Click += (_, _) => viewModel?.Close();
        copyButton.Click += async (_, _) => await CopyLinkAsync();
    }

    internal Control InitialFocus => policySelect;

    internal void Attach(SessionInviteGenerationViewModel value, LocalizationManager resources)
    {
        viewModel = value ?? throw new ArgumentNullException(nameof(value));
        ArgumentNullException.ThrowIfNull(resources);
        value.PropertyChanged += OnPropertyChanged;
        summaryText.Text = resources.GetString("Workspace.Collaboration.Session.GenerateSummary");
        policyLabel.Text = resources.GetString("Workspace.Collaboration.Session.JoinPolicy");
        linkLabel.Text = resources.GetString("Workspace.Collaboration.InviteLink");
        generationConfirmationText.Text = resources.GetString(
            "Workspace.Collaboration.Session.GenerationConfirmation");
        generateButton.Content = resources.GetString("Command.Generate");
        cancelGenerationButton.Content = resources.GetString("Command.Cancel");
        confirmGenerationButton.Content = resources.GetString("Command.Confirm");
        copyButton.Content = resources.GetString("Command.Copy");
        closeButton.Content = resources.GetString("Command.Close");
        AutomationProperties.SetName(copyButton, resources.GetString("Command.Copy"));
        ToolTip.SetTip(copyButton, resources.GetString("Command.Copy"));
        policySelect.ItemsSource = Enum.GetValues<SessionJoinPolicy>()
            .Select(policy => new PolicyOption(
                policy,
                resources.GetString($"Workspace.Collaboration.Session.Policy.{policy}")))
            .ToArray();
        policySelect.SelectedIndex = (int)value.JoinPolicy;
        AutomationProperties.SetAutomationId(generateButton, "workspace.collaboration.session.generate");
        Refresh();
    }

    internal void Detach()
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnPropertyChanged;
        }

        viewModel = null;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        if (viewModel is null)
        {
            return;
        }

        linkText.Text = InvitationLinkDisplay.Format(viewModel.InvitationLink);
        generateButton.IsEnabled = viewModel.CanGenerate && !viewModel.IsGenerationConfirmationVisible;
        policySelect.IsEnabled = !viewModel.IsBusy && !viewModel.IsGenerationConfirmationVisible;
        generationConfirmationPanel.IsVisible = viewModel.IsGenerationConfirmationVisible;
        cancelGenerationButton.IsEnabled = !viewModel.IsBusy;
        confirmGenerationButton.IsEnabled = !viewModel.IsBusy;
        copyButton.IsEnabled = !viewModel.IsBusy && !string.IsNullOrWhiteSpace(viewModel.InvitationLink);
    }

    private async Task CopyLinkAsync()
    {
        if (string.IsNullOrWhiteSpace(viewModel?.InvitationLink)
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(viewModel.InvitationLink);
            viewModel.RecordCopyResult(true, null);
        }
        catch (Exception exception)
        {
            viewModel.RecordCopyResult(false, exception.GetType().Name);
        }
    }

    private sealed record PolicyOption(SessionJoinPolicy Value, string Label)
    {
        public override string ToString() => Label;
    }
}
