using System.Net;
using System.Net.Sockets;
using Motara.Tracking.iFacialMocap;

namespace Motara.App.Tracking;

internal sealed record IFacialMocapConfiguration
{
    internal const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string LocalAddress { get; init; } = string.Empty;

    public string DeviceAddress { get; init; } = string.Empty;

    public int Port { get; init; } = 49983;

    internal static IFacialMocapConfiguration Create(
        string localAddress,
        string deviceAddress,
        int port = 49983)
    {
        var configuration = new IFacialMocapConfiguration
        {
            LocalAddress = NormalizeIpv4(localAddress, nameof(localAddress)),
            DeviceAddress = NormalizeIpv4(deviceAddress, nameof(deviceAddress)),
            Port = port,
        };
        Validate(configuration);
        return configuration;
    }

    internal static void Validate(IFacialMocapConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentOutOfRangeException.ThrowIfNotEqual(
            configuration.SchemaVersion,
            CurrentSchemaVersion);
        _ = NormalizeIpv4(configuration.LocalAddress, nameof(LocalAddress));
        _ = NormalizeIpv4(configuration.DeviceAddress, nameof(DeviceAddress));
        ArgumentOutOfRangeException.ThrowIfLessThan(configuration.Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(configuration.Port, ushort.MaxValue);
    }

    internal IFacialMocapOptions ToOptions() => IFacialMocapOptions.Create(
        IPAddress.Parse(LocalAddress),
        Port,
        IPAddress.Parse(DeviceAddress),
        Port);

    private static string NormalizeIpv4(string value, string parameterName)
    {
        if (!IPAddress.TryParse(value, out IPAddress? address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.Broadcast)
            || address.Equals(IPAddress.None))
        {
            throw new ArgumentException("A specific IPv4 address is required.", parameterName);
        }

        return address.ToString();
    }
}
