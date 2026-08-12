using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Crypto.Parameters;

namespace Motara.Collaboration.Identity;

public sealed class DeviceIdentityStore : IDisposable
{
    private const string DocumentFileName = "device.identity.motara.json";
    private const string SecretFileName = "device.identity.secret";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IDeviceSecretProtector protector;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<DeviceIdentityStore> logger;
    private readonly SemaphoreSlim accessGate = new(1, 1);

    public DeviceIdentityStore(
        string collaborationRoot,
        IDeviceSecretProtector protector,
        TimeProvider timeProvider,
        ILogger<DeviceIdentityStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collaborationRoot);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(timeProvider);
        string identityDirectory = Path.Combine(Path.GetFullPath(collaborationRoot), "identity");
        DocumentPath = Path.Combine(identityDirectory, DocumentFileName);
        SecretPath = Path.Combine(identityDirectory, SecretFileName);
        this.protector = protector;
        this.timeProvider = timeProvider;
        this.logger = logger ?? NullLogger<DeviceIdentityStore>.Instance;
    }

    internal string DocumentPath { get; }

    internal string SecretPath { get; }

    public void Dispose() => accessGate.Dispose();

    public async Task<DeviceIdentityHandle> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool documentExists = File.Exists(DocumentPath);
            bool secretExists = File.Exists(SecretPath);
            if (!documentExists && !secretExists)
            {
                return await CreateAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!documentExists || !secretExists)
            {
                throw FailClosed("Device identity storage is incomplete.");
            }

            return await LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IdentityLoadException exception)
        {
            DeviceIdentityEvents.LoadFailed(logger, exception.InnerException?.GetType().Name ?? exception.GetType().Name);
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or CryptographicException
            or ArgumentException)
        {
            DeviceIdentityEvents.LoadFailed(logger, exception.GetType().Name);
            throw FailClosed("Device identity could not be loaded.", exception);
        }
        finally
        {
            accessGate.Release();
        }
    }

    internal async Task RestoreAsync(
        DeviceIdentity identity,
        ReadOnlyMemory<byte> privateSeed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (privateSeed.Length != Ed25519PrivateKeyParameters.KeySize)
        {
            throw new ArgumentException("The device identity seed has an invalid length.", nameof(privateSeed));
        }

        byte[] seed = privateSeed.ToArray();
        byte[]? protectedSecret = null;
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] actualPublicKey = new Ed25519PrivateKeyParameters(seed).GeneratePublicKey().GetEncoded();
            if (!CryptographicOperations.FixedTimeEquals(actualPublicKey, identity.PublicKey.AsSpan())
                || DeviceId.FromEd25519PublicKey(actualPublicKey) != identity.DeviceId)
            {
                throw new IdentityLoadException("The imported identity key does not match its public identity.");
            }

            protectedSecret = protector.Protect(seed);
            Directory.CreateDirectory(Path.GetDirectoryName(DocumentPath)!);
            await WriteBytesAtomicallyAsync(SecretPath, protectedSecret, cancellationToken)
                .ConfigureAwait(false);
            await WriteJsonAtomicallyAsync(
                DocumentPath,
                DeviceIdentityDocument.FromIdentity(identity),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
            if (protectedSecret is not null)
            {
                CryptographicOperations.ZeroMemory(protectedSecret);
            }

            accessGate.Release();
        }
    }

    private async Task<DeviceIdentityHandle> CreateAsync(CancellationToken cancellationToken)
    {
        byte[] privateSeed = RandomNumberGenerator.GetBytes(Ed25519PrivateKeyParameters.KeySize);
        try
        {
            var privateKey = new Ed25519PrivateKeyParameters(privateSeed);
            byte[] publicKey = privateKey.GeneratePublicKey().GetEncoded();
            var identity = new DeviceIdentity(
                DeviceIdentity.CurrentSchemaVersion,
                DeviceId.FromEd25519PublicKey(publicKey),
                publicKey,
                SecretFileName,
                timeProvider.GetUtcNow());
            byte[] protectedSecret = protector.Protect(privateSeed);
            Directory.CreateDirectory(Path.GetDirectoryName(DocumentPath)!);
            await WriteBytesAtomicallyAsync(SecretPath, protectedSecret, cancellationToken).ConfigureAwait(false);
            await WriteJsonAtomicallyAsync(
                DocumentPath,
                DeviceIdentityDocument.FromIdentity(identity),
                cancellationToken).ConfigureAwait(false);
            DeviceIdentityEvents.Created(logger);
            return new DeviceIdentityHandle(identity, privateSeed);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(privateSeed);
            throw;
        }
    }

    private async Task<DeviceIdentityHandle> LoadAsync(CancellationToken cancellationToken)
    {
        DeviceIdentityDocument document = await ReadJsonAsync(cancellationToken).ConfigureAwait(false);
        DeviceIdentity identity = document.ToIdentity();
        byte[] protectedSecret = await File.ReadAllBytesAsync(SecretPath, cancellationToken).ConfigureAwait(false);
        byte[] privateSeed = protector.Unprotect(protectedSecret);
        try
        {
            if (privateSeed.Length != Ed25519PrivateKeyParameters.KeySize)
            {
                throw FailClosed("Device identity secret has an invalid length.");
            }

            byte[] actualPublicKey = new Ed25519PrivateKeyParameters(privateSeed).GeneratePublicKey().GetEncoded();
            if (!CryptographicOperations.FixedTimeEquals(actualPublicKey, identity.PublicKey.AsSpan())
                || DeviceId.FromEd25519PublicKey(actualPublicKey) != identity.DeviceId)
            {
                throw FailClosed("Device identity public and private data do not match.");
            }

            DeviceIdentityEvents.Loaded(logger);
            return new DeviceIdentityHandle(identity, privateSeed);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(privateSeed);
            throw;
        }
    }

    private async Task<DeviceIdentityDocument> ReadJsonAsync(CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            DocumentPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<DeviceIdentityDocument>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw FailClosed("Device identity document is empty.");
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        string temporaryPath = CreateTemporaryPath(path);
        try
        {
            await using (FileStream stream = CreateWriteStream(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static async Task WriteBytesAtomicallyAsync(
        string path,
        byte[] value,
        CancellationToken cancellationToken)
    {
        string temporaryPath = CreateTemporaryPath(path);
        try
        {
            await using (FileStream stream = CreateWriteStream(temporaryPath))
            {
                await stream.WriteAsync(value, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static FileStream CreateWriteStream(string path) => new(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        4096,
        FileOptions.Asynchronous | FileOptions.WriteThrough);

    private static string CreateTemporaryPath(string path) => Path.Combine(
        Path.GetDirectoryName(path)!,
        $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

    private static IdentityLoadException FailClosed(string message, Exception? exception = null) =>
        new(message, exception);

    private sealed record DeviceIdentityDocument(
        int SchemaVersion,
        string DeviceId,
        string PublicKey,
        string SecretReference,
        DateTimeOffset CreatedAtUtc)
    {
        internal static DeviceIdentityDocument FromIdentity(DeviceIdentity identity) => new(
            identity.SchemaVersion,
            identity.DeviceId.Value,
            Convert.ToBase64String(identity.PublicKey.ToArray()),
            identity.SecretReference,
            identity.CreatedAtUtc);

        internal DeviceIdentity ToIdentity()
        {
            if (SchemaVersion != DeviceIdentity.CurrentSchemaVersion
                || !string.Equals(SecretReference, SecretFileName, StringComparison.Ordinal))
            {
                throw FailClosed("Device identity document has unsupported values.");
            }

            byte[] publicKey = Convert.FromBase64String(PublicKey);
            DeviceId deviceId = Identity.DeviceId.Parse(DeviceId);
            if (publicKey.Length != Ed25519PublicKeyParameters.KeySize
                || Identity.DeviceId.FromEd25519PublicKey(publicKey) != deviceId)
            {
                throw FailClosed("Device identity document is inconsistent.");
            }

            return new DeviceIdentity(SchemaVersion, deviceId, publicKey, SecretReference, CreatedAtUtc);
        }
    }
}
