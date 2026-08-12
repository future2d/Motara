using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Motara.App.Localization;
using Motara.App.ViewModels;

namespace Motara.App.Controls;

internal sealed partial class IFacialMocapConfigurationControl : UserControl
{
    private ComboBox localAddress = null!;
    private TextBlock localAddressLabel = null!;
    private TextBox deviceAddress = null!;
    private TextBlock deviceAddressLabel = null!;
    private TextBox port = null!;
    private TextBlock portLabel = null!;
    private TextBlock error = null!;
    private Button cancel = null!;
    private Button connect = null!;
    private IFacialMocapConfigurationViewModel? viewModel;
    private LocalizationManager? localization;
    private Action? close;

    public IFacialMocapConfigurationControl()
    {
        AvaloniaXamlLoader.Load(this);
        localAddressLabel = this.FindControl<TextBlock>("LocalAddressLabel")!;
        localAddress = this.FindControl<ComboBox>("LocalAddress")!;
        deviceAddressLabel = this.FindControl<TextBlock>("DeviceAddressLabel")!;
        deviceAddress = this.FindControl<TextBox>("DeviceAddress")!;
        portLabel = this.FindControl<TextBlock>("PortLabel")!;
        port = this.FindControl<TextBox>("Port")!;
        error = this.FindControl<TextBlock>("Error")!;
        cancel = this.FindControl<Button>("Cancel")!;
        connect = this.FindControl<Button>("Connect")!;
        localAddress.SelectionChanged += OnLocalAddressChanged;
        deviceAddress.TextChanged += OnDeviceAddressChanged;
        port.TextChanged += OnPortChanged;
        cancel.Click += OnCancel;
        connect.Click += OnConnect;
        AutomationProperties.SetAutomationId(localAddress, "workspace.ifacialmocap.local-address");
        AutomationProperties.SetAutomationId(deviceAddress, "workspace.ifacialmocap.device-address");
        AutomationProperties.SetAutomationId(port, "workspace.ifacialmocap.port");
        AutomationProperties.SetAutomationId(connect, "workspace.ifacialmocap.connect");
    }

    public Control InitialFocus => deviceAddress;

    public void Attach(
        IFacialMocapConfigurationViewModel value,
        LocalizationManager localization,
        Action close)
    {
        viewModel = value;
        this.localization = localization;
        this.close = close;
        value.PropertyChanged += OnPropertyChanged;
        ApplyLocalization();
        Refresh();
    }

    public void Detach()
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnPropertyChanged;
        }

        viewModel = null;
        localization = null;
        close = null;
        localAddress.SelectionChanged -= OnLocalAddressChanged;
        deviceAddress.TextChanged -= OnDeviceAddressChanged;
        port.TextChanged -= OnPortChanged;
        cancel.Click -= OnCancel;
        connect.Click -= OnConnect;
    }

    private void ApplyLocalization()
    {
        localAddressLabel.Text = localization!.GetString("Workspace.Tracking.IFacialMocap.LocalAddress");
        deviceAddressLabel.Text = localization.GetString("Workspace.Tracking.IFacialMocap.DeviceAddress");
        portLabel.Text = localization.GetString("Workspace.Tracking.IFacialMocap.Port");
        cancel.Content = localization.GetString("Command.Cancel");
        connect.Content = localization.GetString("Command.SaveAndConnect");
        AutomationProperties.SetName(localAddress, localAddressLabel.Text);
        AutomationProperties.SetName(deviceAddress, deviceAddressLabel.Text);
        AutomationProperties.SetName(port, portLabel.Text);
        AutomationProperties.SetName(cancel, cancel.Content?.ToString());
        AutomationProperties.SetName(connect, connect.Content?.ToString());
    }

    private void Refresh()
    {
        if (viewModel is null || localization is null)
        {
            return;
        }

        localAddress.ItemsSource = viewModel.LocalAddresses;
        localAddress.SelectedItem = viewModel.SelectedLocalAddress;
        deviceAddress.Text = viewModel.DeviceAddress;
        port.Text = viewModel.PortText;
        error.Text = viewModel.ErrorResourceKey is null
            ? string.Empty
            : localization.GetString(viewModel.ErrorResourceKey);
        connect.IsEnabled = !viewModel.IsLoading
            && !viewModel.IsSubmitting
            && viewModel.SelectedLocalAddress is not null;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        Dispatcher.UIThread.Post(Refresh);

    private void OnLocalAddressChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (viewModel is not null)
        {
            viewModel.SelectedLocalAddress = localAddress.SelectedItem as string;
        }
    }

    private void OnDeviceAddressChanged(object? sender, TextChangedEventArgs args)
    {
        if (viewModel is not null)
        {
            viewModel.DeviceAddress = deviceAddress.Text ?? string.Empty;
        }
    }

    private void OnPortChanged(object? sender, TextChangedEventArgs args)
    {
        if (viewModel is not null)
        {
            viewModel.PortText = port.Text ?? string.Empty;
        }
    }

    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => close?.Invoke();

    private async void OnConnect(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (viewModel is not null
            && await viewModel.SaveAndConnectAsync(CancellationToken.None))
        {
            close?.Invoke();
        }
    }
}
