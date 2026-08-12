using System.Net;
using System.Net.Sockets;

namespace Motara.Tracking.iFacialMocap;

/// <summary>Contains one validated explicit IPv4 configuration for iFacialMocap UDP input.</summary>
public sealed record IFacialMocapOptions
{
    private IFacialMocapOptions(
        IPAddress localAddress,
        int localPort,
        IPAddress deviceAddress,
        int devicePort,
        TimeSpan handshakeRetryInterval)
    {
        LocalAddress = localAddress;
        LocalPort = localPort;
        DeviceAddress = deviceAddress;
        DevicePort = devicePort;
        HandshakeRetryInterval = handshakeRetryInterval;
    }

    /// <summary>Gets the explicitly selected local IPv4 address.</summary>
    public IPAddress LocalAddress { get; }

    /// <summary>Gets the local UDP receive port.</summary>
    public int LocalPort { get; }

    /// <summary>Gets the configured iFacialMocap device IPv4 address.</summary>
    public IPAddress DeviceAddress { get; }

    /// <summary>Gets the device UDP control/data port.</summary>
    public int DevicePort { get; }

    /// <summary>Gets the interval used to re-request data while no packet arrives.</summary>
    public TimeSpan HandshakeRetryInterval { get; }

    /// <summary>Creates and validates an explicit iFacialMocap UDP configuration.</summary>
    public static IFacialMocapOptions Create(
        IPAddress localAddress,
        int localPort,
        IPAddress deviceAddress,
        int devicePort = 49983,
        TimeSpan? handshakeRetryInterval = null)
    {
        ValidateAddress(localAddress, nameof(localAddress));
        ValidateAddress(deviceAddress, nameof(deviceAddress));
        ArgumentOutOfRangeException.ThrowIfLessThan(localPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(localPort, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(devicePort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(devicePort, ushort.MaxValue);
        TimeSpan retry = handshakeRetryInterval ?? TimeSpan.FromSeconds(2);
        if (retry < TimeSpan.FromMilliseconds(100) || retry > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(handshakeRetryInterval),
                "Handshake retry interval must be between 100 milliseconds and one minute.");
        }

        return new IFacialMocapOptions(
            localAddress,
            localPort,
            deviceAddress,
            devicePort,
            retry);
    }

    private static void ValidateAddress(IPAddress address, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(address, parameterName);
        if (address.AddressFamily != AddressFamily.InterNetwork
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.Broadcast)
            || address.Equals(IPAddress.None))
        {
            throw new ArgumentException(
                "A specific IPv4 address is required.",
                parameterName);
        }
    }
}
