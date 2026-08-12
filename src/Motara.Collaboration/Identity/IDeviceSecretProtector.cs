namespace Motara.Collaboration.Identity;

public interface IDeviceSecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(ReadOnlySpan<byte> protectedValue);
}
