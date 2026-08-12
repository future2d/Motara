using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Motara.App.Tracking;

internal interface ILocalIpv4AddressProvider
{
    Task<IReadOnlyList<string>> GetAddressesAsync(CancellationToken cancellationToken);
}

internal sealed class LocalIpv4AddressProvider : ILocalIpv4AddressProvider
{
    private readonly ILogger<LocalIpv4AddressProvider> logger;

    internal LocalIpv4AddressProvider(ILogger<LocalIpv4AddressProvider>? logger = null)
    {
        this.logger = logger ?? NullLogger<LocalIpv4AddressProvider>.Instance;
    }

    public async Task<IReadOnlyList<string>> GetAddressesAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> addresses = await Task.Run(
            EnumerateAddresses,
            cancellationToken).ConfigureAwait(false);
        LocalIpv4AddressLog.Completed(logger, addresses.Count);
        return addresses;
    }

    internal static IReadOnlyList<string> NormalizeCandidates(IEnumerable<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        return addresses
            .Where(static address =>
                address.AddressFamily == AddressFamily.InterNetwork
                && !address.Equals(IPAddress.Any)
                && !address.Equals(IPAddress.Broadcast)
                && !address.Equals(IPAddress.None))
            .Select(static address => address.ToString())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> EnumerateAddresses()
    {
        IEnumerable<IPAddress> candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(static adapter =>
                adapter.OperationalStatus == OperationalStatus.Up
                && adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback
                    and not NetworkInterfaceType.Tunnel)
            .SelectMany(static adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(static address => address.Address);
        return NormalizeCandidates(candidates);
    }
}

internal static partial class LocalIpv4AddressLog
{
    [LoggerMessage(6603, LogLevel.Debug, "Local IPv4 address enumeration completed with {AddressCount} candidates")]
    internal static partial void Completed(ILogger logger, int addressCount);
}
