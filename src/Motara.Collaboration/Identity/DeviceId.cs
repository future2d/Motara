using System.Security.Cryptography;
using System.Text;

namespace Motara.Collaboration.Identity;

public readonly record struct DeviceId
{
    private const string Prefix = "device-v1:";
    private const int PublicKeyLength = 32;
    private const int HashHexLength = 64;
    private static readonly byte[] DerivationDomain = Encoding.UTF8.GetBytes("motara-device-ed25519-v1");

    private DeviceId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static DeviceId FromEd25519PublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != PublicKeyLength)
        {
            throw new ArgumentException("An Ed25519 public key must contain exactly 32 bytes.", nameof(publicKey));
        }

        byte[] input = new byte[DerivationDomain.Length + publicKey.Length];
        DerivationDomain.CopyTo(input, 0);
        publicKey.CopyTo(input.AsSpan(DerivationDomain.Length));
        byte[] hash = SHA256.HashData(input);
        CryptographicOperations.ZeroMemory(input);
        return new DeviceId(Prefix + Convert.ToHexStringLower(hash));
    }

    public static DeviceId Parse(string value)
    {
        if (!TryParse(value, out DeviceId deviceId))
        {
            throw new FormatException("The device identifier is invalid.");
        }

        return deviceId;
    }

    public static bool TryParse(string? value, out DeviceId deviceId)
    {
        deviceId = default;
        if (value is null
            || value.Length != Prefix.Length + HashHexLength
            || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> hash = value.AsSpan(Prefix.Length);
        foreach (char character in hash)
        {
            if (!char.IsAsciiHexDigitLower(character))
            {
                return false;
            }
        }

        deviceId = new DeviceId(value);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;
}
