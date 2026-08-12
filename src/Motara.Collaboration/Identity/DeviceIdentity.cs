using System.Security.Cryptography;
using System.Collections.Immutable;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Motara.Collaboration.Identity;

public sealed record DeviceIdentity
{
    public const int CurrentSchemaVersion = 1;

    public DeviceIdentity(
        int schemaVersion,
        DeviceId deviceId,
        byte[] publicKey,
        string secretReference,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretReference);
        if (schemaVersion != CurrentSchemaVersion || publicKey.Length != 32)
        {
            throw new ArgumentException("The device identity is invalid.");
        }

        SchemaVersion = schemaVersion;
        DeviceId = deviceId;
        PublicKey = ImmutableArray.CreateRange(publicKey);
        SecretReference = secretReference;
        CreatedAtUtc = createdAtUtc;
    }

    public int SchemaVersion { get; }

    public DeviceId DeviceId { get; }

    public ImmutableArray<byte> PublicKey { get; }

    public string SecretReference { get; }

    public DateTimeOffset CreatedAtUtc { get; }
}

public sealed class DeviceIdentityHandle : IDisposable, IAsyncDisposable
{
    private byte[]? privateSeed;

    internal DeviceIdentityHandle(DeviceIdentity identity, byte[] privateSeed)
    {
        Identity = identity;
        this.privateSeed = privateSeed;
    }

    public DeviceIdentity Identity { get; }

    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        ObjectDisposedException.ThrowIf(privateSeed is null, this);
        byte[] input = message.ToArray();
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(privateSeed));
        signer.BlockUpdate(input, 0, input.Length);
        return signer.GenerateSignature();
    }

    internal byte[] CopyPrivateSeed()
    {
        ObjectDisposedException.ThrowIf(privateSeed is null, this);
        return privateSeed.ToArray();
    }

    public void Dispose()
    {
        byte[]? seed = Interlocked.Exchange(ref privateSeed, null);
        if (seed is not null)
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class IdentityLoadException : Exception
{
    public IdentityLoadException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
