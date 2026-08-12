using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Motara.App.Localization;
using Motara.App.ViewModels;

namespace Motara.App.Controls;

public sealed partial class CubismEditorOutputSettingsControl : UserControl
{
    private readonly TextBox endpoint;
    private readonly ToggleSwitch startOnLaunch;
    private readonly TextBlock validation;
    private readonly Button apply;
    private CubismEditorOutputSettingsWorkspaceViewModel? viewModel;
    private LocalizationManager? localization;
    private bool updating;

    public CubismEditorOutputSettingsControl()
    {
        AvaloniaXamlLoader.Load(this);
        endpoint = this.FindControl<TextBox>("EndpointInput")!;
        startOnLaunch = this.FindControl<ToggleSwitch>("StartOnLaunchToggle")!;
        validation = this.FindControl<TextBlock>("ValidationText")!;
        apply = this.FindControl<Button>("ApplyButton")!;

        endpoint.TextChanged += (_, _) =>
        {
            if (!updating && viewModel is not null)
            {
                viewModel.EndpointText = endpoint.Text ?? string.Empty;
            }
        };
        startOnLaunch.IsCheckedChanged += (_, _) =>
        {
            if (!updating && viewModel is not null)
            {
                viewModel.StartOnLaunch = startOnLaunch.IsChecked == true;
            }
        };
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => viewModel?.Cancel();
        apply.Click += async (_, _) =>
            await (viewModel?.ApplyAsync(CancellationToken.None) ?? Task.CompletedTask);
    }

    internal Control InitialFocus => endpoint;

    internal void Attach(
        CubismEditorOutputSettingsWorkspaceViewModel value,
        LocalizationManager manager)
    {
        viewModel = value ?? throw new ArgumentNullException(nameof(value));
        localization = manager ?? throw new ArgumentNullException(nameof(manager));
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = value;
        this.FindControl<TextBlock>("EndpointLabel")!.Text = manager.GetString("Workspace.CubismEditor.Endpoint");
        this.FindControl<TextBlock>("StartOnLaunchLabel")!.Text = manager.GetString("Workspace.CubismEditor.StartOnLaunch");
        this.FindControl<Button>("CancelButton")!.Content = manager.GetString("Command.Cancel");
        apply.Content = manager.GetString("Command.Apply");
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
        DataContext = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(CubismEditorOutputSettingsWorkspaceViewModel.ValidationResourceKey)
            or nameof(CubismEditorOutputSettingsWorkspaceViewModel.IsApplying))
        {
            RefreshState();
        }
    }

    private void Refresh()
    {
        updating = true;
        endpoint.Text = viewModel!.EndpointText;
        startOnLaunch.IsChecked = viewModel.StartOnLaunch;
        updating = false;
        RefreshState();
    }

    private void RefreshState()
    {
        validation.Text = viewModel?.ValidationResourceKey is { } key
            ? localization!.GetString(key)
            : string.Empty;
        bool isApplying = viewModel?.IsApplying == true;
        endpoint.IsEnabled = !isApplying;
        startOnLaunch.IsEnabled = !isApplying;
        apply.IsEnabled = !isApplying;
    }
}
