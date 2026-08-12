using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Tracking;
using Motara.Tracking.iFacialMocap;

namespace Motara.App.ViewModels;

internal sealed class IFacialMocapConfigurationViewModel : INotifyPropertyChanged
{
    private readonly IIFacialMocapConfigurationStore store;
    private readonly ILocalIpv4AddressProvider addressProvider;
    private readonly Action<IFacialMocapOptions> configureSource;
    private readonly Func<string, CancellationToken, Task<bool>> selectSourceAsync;
    private readonly ILogger<IFacialMocapConfigurationViewModel> logger;
    private IReadOnlyList<string> localAddresses = Array.Empty<string>();
    private string? selectedLocalAddress;
    private string deviceAddress = string.Empty;
    private string portText = "49983";
    private string? errorResourceKey;
    private bool isLoading;
    private bool isSubmitting;

    internal IFacialMocapConfigurationViewModel(
        IIFacialMocapConfigurationStore store,
        ILocalIpv4AddressProvider addressProvider,
        Action<IFacialMocapOptions> configureSource,
        Func<string, CancellationToken, Task<bool>> selectSourceAsync,
        ILogger<IFacialMocapConfigurationViewModel>? logger = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.addressProvider = addressProvider
            ?? throw new ArgumentNullException(nameof(addressProvider));
        this.configureSource = configureSource
            ?? throw new ArgumentNullException(nameof(configureSource));
        this.selectSourceAsync = selectSourceAsync
            ?? throw new ArgumentNullException(nameof(selectSourceAsync));
        this.logger = logger ?? NullLogger<IFacialMocapConfigurationViewModel>.Instance;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal IReadOnlyList<string> LocalAddresses
    {
        get => localAddresses;
        private set => SetField(ref localAddresses, value);
    }

    internal string? SelectedLocalAddress
    {
        get => selectedLocalAddress;
        set => SetField(ref selectedLocalAddress, value);
    }

    internal string DeviceAddress
    {
        get => deviceAddress;
        set => SetField(ref deviceAddress, value ?? string.Empty);
    }

    internal string PortText
    {
        get => portText;
        set => SetField(ref portText, value ?? string.Empty);
    }

    internal string? ErrorResourceKey
    {
        get => errorResourceKey;
        private set => SetField(ref errorResourceKey, value);
    }

    internal bool IsLoading
    {
        get => isLoading;
        private set => SetField(ref isLoading, value);
    }

    internal bool IsSubmitting
    {
        get => isSubmitting;
        private set => SetField(ref isSubmitting, value);
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        ErrorResourceKey = null;
        try
        {
            Task<IFacialMocapConfiguration?> configurationTask = store.LoadAsync(cancellationToken);
            Task<IReadOnlyList<string>> addressesTask = addressProvider.GetAddressesAsync(
                cancellationToken);
            await Task.WhenAll(configurationTask, addressesTask).ConfigureAwait(false);

            IFacialMocapConfiguration? configuration = await configurationTask.ConfigureAwait(false);
            IReadOnlyList<string> discovered = await addressesTask.ConfigureAwait(false);
            LocalAddresses = configuration is null
                ? discovered
                : discovered
                    .Append(configuration.LocalAddress)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
            SelectedLocalAddress = configuration?.LocalAddress
                ?? (LocalAddresses.Count > 0 ? LocalAddresses[0] : null);
            DeviceAddress = configuration?.DeviceAddress ?? string.Empty;
            PortText = (configuration?.Port ?? 49983).ToString(CultureInfo.InvariantCulture);
            if (SelectedLocalAddress is null)
            {
                ErrorResourceKey = "Workspace.Tracking.IFacialMocap.Error.NoLocalAddress";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ErrorResourceKey = "Workspace.Tracking.IFacialMocap.Error.Load";
            IFacialMocapConfigurationWorkspaceLog.Failed(
                logger,
                "Load",
                exception.GetType().Name);
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal async Task<bool> SaveAndConnectAsync(CancellationToken cancellationToken)
    {
        if (IsSubmitting)
        {
            return false;
        }

        if (!TryCreateConfiguration(out IFacialMocapConfiguration? configuration))
        {
            return false;
        }

        IsSubmitting = true;
        ErrorResourceKey = null;
        try
        {
            await store.SaveAsync(configuration, cancellationToken).ConfigureAwait(false);
            IFacialMocapOptions options = IFacialMocapOptions.Create(
                IPAddress.Parse(configuration.LocalAddress),
                configuration.Port,
                IPAddress.Parse(configuration.DeviceAddress),
                configuration.Port);
            configureSource(options);
            bool connected = await selectSourceAsync(
                IFacialMocapTrackingSource.SourceId,
                cancellationToken).ConfigureAwait(false);
            if (!connected)
            {
                ErrorResourceKey = "Workspace.Tracking.IFacialMocap.Error.Connect";
                IFacialMocapConfigurationWorkspaceLog.Failed(logger, "Connect", "Unavailable");
                return false;
            }

            IFacialMocapConfigurationWorkspaceLog.Connected(logger);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ErrorResourceKey = "Workspace.Tracking.IFacialMocap.Error.SaveOrConnect";
            IFacialMocapConfigurationWorkspaceLog.Failed(
                logger,
                "SaveOrConnect",
                exception.GetType().Name);
            return false;
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private bool TryCreateConfiguration(
        out IFacialMocapConfiguration configuration)
    {
        configuration = null!;
        if (!IsSpecificIpv4(SelectedLocalAddress))
        {
            ErrorResourceKey = "Workspace.Tracking.IFacialMocap.Error.LocalAddress";
            return false;
        }

        if (!IsSpecificIpv4(DeviceAddress))
        {
            ErrorResourceKey = "Workspace.Tracking.IFacialMocap.Error.DeviceAddress";
            return false;
        }

        if (!int.TryParse(
                PortText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int port)
            || port is < 1 or > ushort.MaxValue)
        {
            ErrorResourceKey = "Workspace.Tracking.IFacialMocap.Error.Port";
            return false;
        }

        configuration = IFacialMocapConfiguration.Create(
            SelectedLocalAddress!,
            DeviceAddress,
            port);
        return true;
    }

    private static bool IsSpecificIpv4(string? value) =>
        IPAddress.TryParse(value, out IPAddress? address)
        && address.AddressFamily == AddressFamily.InterNetwork
        && !address.Equals(IPAddress.Any)
        && !address.Equals(IPAddress.Broadcast)
        && !address.Equals(IPAddress.None);

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal static partial class IFacialMocapConfigurationWorkspaceLog
{
    [LoggerMessage(6604, LogLevel.Information, "iFacialMocap configuration saved and source selected")]
    internal static partial void Connected(ILogger logger);

    [LoggerMessage(6605, LogLevel.Warning, "iFacialMocap configuration operation {Operation} failed with {ErrorType}")]
    internal static partial void Failed(ILogger logger, string operation, string errorType);
}
