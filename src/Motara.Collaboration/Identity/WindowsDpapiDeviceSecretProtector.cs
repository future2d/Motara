using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;

namespace Motara.Collaboration.Identity;

[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiDeviceSecretProtector : IDeviceSecretProtector
{
    private static readonly byte[] OptionalEntropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("motara-collaboration-device-secret-v1"));

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        EnsureWindows();
        return ProtectedData.Protect(
            plaintext.ToArray(),
            OptionalEntropy,
            DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedValue)
    {
        EnsureWindows();
        return ProtectedData.Unprotect(
            protectedValue.ToArray(),
            OptionalEntropy,
            DataProtectionScope.CurrentUser);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Collaboration identity secure storage is not available on this platform yet.");
        }
    }
}
