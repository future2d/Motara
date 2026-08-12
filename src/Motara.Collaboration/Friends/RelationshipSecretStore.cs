using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Identity;

namespace Motara.Collaboration.Friends;

public sealed class RelationshipSecretStore : IDisposable
{
    private const int SchemaVersion = 1;
    private const int SecretLength = 32;
    private const string DeviceIdPrefix = "device-v1:";
    private const string DirectoryName = "relationship-secrets";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string secretsRoot;
    private readonly IDeviceSecretProtector protector;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<RelationshipSecretStore> logger;
    private readonly SemaphoreSlim accessGate = new(1, 1);

    public RelationshipSecretStore(
        string collaborationRoot,
        IDeviceSecretProtector protector,
        TimeProvider timeProvider,
        ILogger<RelationshipSecretStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collaborationRoot);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(timeProvider);
        secretsRoot = Path.Combine(Path.GetFullPath(collaborationRoot), DirectoryName);
        this.protector = protector;
        this.timeProvider = timeProvider;
        this.logger = logger ?? NullLogger<RelationshipSecretStore>.Instance;
    }

    public void Dispose() => accessGate.Dispose();

    public async Task<string> SaveAsync(
        DeviceId friendDeviceId,
        ReadOnlyMemory<byte> relationshipSecret,
        CancellationToken cancellationToken)
    {
        ValidateDeviceId(friendDeviceId);
        if (relationshipSecret.Length != SecretLength)
        {
            throw new ArgumentException(
                $"A relationship secret must contain exactly {SecretLength} bytes.",
                nameof(relationshipSecret));
        }

        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[] plaintext = relationshipSecret.ToArray();
        byte[]? protectedSecret = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            protectedSecret = protector.Protect(plaintext);
            if (protectedSecret.Length == 0)
            {
                throw new CryptographicException("The protected relationship secret is empty.");
            }

            Directory.CreateDirectory(secretsRoot);
            string path = GetPath(friendDeviceId);
            var document = new RelationshipSecretDocument(
                SchemaVersion,
                friendDeviceId.Value,
                Convert.ToBase64String(protectedSecret),
                timeProvider.GetUtcNow());
            await WriteAtomicallyAsync(path, document, cancellationToken).ConfigureAwait(false);
            RelationshipSecretEvents.Saved(logger);
            return GetReference(friendDeviceId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            RelationshipSecretEvents.Failed(logger, "Save", exception.GetType().Name);
            throw new RelationshipSecretStoreException("The relationship secret could not be saved.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedSecret is not null)
            {
                CryptographicOperations.ZeroMemory(protectedSecret);
            }

            accessGate.Release();
        }
    }

    public async Task<byte[]?> LoadAsync(
        DeviceId friendDeviceId,
        CancellationToken cancellationToken)
    {
        ValidateDeviceId(friendDeviceId);
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetPath(friendDeviceId);
            if (!File.Exists(path))
            {
                RelationshipSecretEvents.Loaded(logger, false);
                return null;
            }

            RelationshipSecretDocument document = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (document.SchemaVersion != SchemaVersion
                || !string.Equals(document.FriendDeviceId, friendDeviceId.Value, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The relationship secret document is inconsistent.");
            }

            byte[] protectedSecret = Convert.FromBase64String(document.ProtectedSecret);
            byte[]? plaintext = null;
            try
            {
                plaintext = protector.Unprotect(protectedSecret);
                if (plaintext.Length != SecretLength)
                {
                    throw new CryptographicException("The relationship secret has an invalid length.");
                }

                RelationshipSecretEvents.Loaded(logger, true);
                byte[] result = plaintext;
                plaintext = null;
                return result;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedSecret);
                if (plaintext is not null)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RelationshipSecretStoreException)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            RelationshipSecretEvents.Failed(logger, "Load", exception.GetType().Name);
            throw new RelationshipSecretStoreException("The relationship secret could not be loaded.", exception);
        }
        finally
        {
            accessGate.Release();
        }
    }

    public async Task RemoveAsync(DeviceId friendDeviceId, CancellationToken cancellationToken)
    {
        ValidateDeviceId(friendDeviceId);
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = GetPath(friendDeviceId);
            if (!Directory.Exists(secretsRoot))
            {
                RelationshipSecretEvents.Removed(logger, false);
                return;
            }

            bool existed = File.Exists(path);
            File.Delete(path);
            RelationshipSecretEvents.Removed(logger, existed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            RelationshipSecretEvents.Failed(logger, "Remove", exception.GetType().Name);
            throw new RelationshipSecretStoreException("The relationship secret could not be removed.", exception);
        }
        finally
        {
            accessGate.Release();
        }
    }

    private static string GetReference(DeviceId friendDeviceId) =>
        $"{DirectoryName}/{GetDeviceHash(friendDeviceId)}.secret";

    private string GetPath(DeviceId friendDeviceId) =>
        Path.Combine(secretsRoot, $"{GetDeviceHash(friendDeviceId)}.secret");

    private static string GetDeviceHash(DeviceId deviceId)
    {
        ValidateDeviceId(deviceId);
        return deviceId.Value[DeviceIdPrefix.Length..];
    }

    private static void ValidateDeviceId(DeviceId deviceId)
    {
        if (!DeviceId.TryParse(deviceId.Value, out DeviceId parsed) || parsed != deviceId)
        {
            throw new ArgumentException("The friend device identifier is invalid.", nameof(deviceId));
        }
    }

    private static async Task<RelationshipSecretDocument> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<RelationshipSecretDocument>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The relationship secret document is empty.");
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        RelationshipSecretDocument document,
        CancellationToken cancellationToken)
    {
        string temporaryPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
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

    private static bool IsStorageException(Exception exception) => exception is
        IOException
        or UnauthorizedAccessException
        or JsonException
        or CryptographicException
        or ArgumentException
        or FormatException
        or InvalidDataException;

    private sealed record RelationshipSecretDocument(
        int SchemaVersion,
        string FriendDeviceId,
        string ProtectedSecret,
        DateTimeOffset CreatedAtUtc);
}

public sealed class RelationshipSecretStoreException : Exception
{
    public RelationshipSecretStoreException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
