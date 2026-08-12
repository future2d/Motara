using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Identity;

namespace Motara.Collaboration.Friends;

public sealed class FriendStore : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string friendsRoot;
    private readonly ILogger<FriendStore> logger;
    private readonly SemaphoreSlim accessGate = new(1, 1);

    public FriendStore(string collaborationRoot, ILogger<FriendStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collaborationRoot);
        friendsRoot = Path.Combine(Path.GetFullPath(collaborationRoot), "friends");
        this.logger = logger ?? NullLogger<FriendStore>.Instance;
    }

    public async Task<IReadOnlyList<FriendRecord>> ListAsync(CancellationToken cancellationToken)
    {
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(friendsRoot))
            {
                return [];
            }

            var records = new List<FriendRecord>();
            foreach (string path in Directory.EnumerateFiles(
                friendsRoot,
                "*.friend.motara.json",
                SearchOption.TopDirectoryOnly))
            {
                records.Add(await ReadAsync(path, cancellationToken).ConfigureAwait(false));
            }

            records.Sort(static (left, right) =>
                StringComparer.Ordinal.Compare(left.FriendDeviceId.Value, right.FriendDeviceId.Value));
            FriendStoreEvents.Loaded(logger, records.Count);
            return records;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException
            or FormatException)
        {
            FriendStoreEvents.Failed(logger, exception.GetType().Name);
            throw new FriendStoreException("Friend records could not be loaded.", exception);
        }
        finally
        {
            accessGate.Release();
        }
    }

    public async Task<FriendRecord?> GetAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetPath(deviceId);
            return File.Exists(path) ? await ReadAsync(path, cancellationToken).ConfigureAwait(false) : null;
        }
        finally
        {
            accessGate.Release();
        }
    }

    public async Task SaveAsync(FriendRecord friend, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(friend);
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(friendsRoot);
            string path = GetPath(friend.FriendDeviceId);
            if (File.Exists(path))
            {
                FriendRecord existing = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
                if (!existing.FriendPublicKey.AsSpan().SequenceEqual(friend.FriendPublicKey.AsSpan()))
                {
                    throw new FriendStoreException("A friend public key cannot be silently replaced.");
                }

                if (existing.TrustState != friend.TrustState)
                {
                    throw new FriendStoreException(
                        "A friend trust state cannot be changed through public storage.");
                }
            }
            else if (friend.TrustState == FriendTrustState.Trusted)
            {
                throw new FriendStoreException(
                    "A trusted friend can only be created by the handshake coordinator.");
            }

            await WriteAsync(path, FriendDocument.FromRecord(friend), cancellationToken).ConfigureAwait(false);
            FriendStoreEvents.Saved(logger, friend.TrustState);
        }
        finally
        {
            accessGate.Release();
        }
    }

    public async Task<FriendRecord> SetBlockedAsync(
        DeviceId deviceId,
        DateTimeOffset blockedAtUtc,
        CancellationToken cancellationToken)
    {
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetPath(deviceId);
            if (!File.Exists(path))
            {
                throw new KeyNotFoundException("The friend record does not exist.");
            }

            FriendRecord blocked = (await ReadAsync(path, cancellationToken).ConfigureAwait(false))
                .WithBlocked(blockedAtUtc);
            await WriteAsync(path, FriendDocument.FromRecord(blocked), cancellationToken).ConfigureAwait(false);
            FriendStoreEvents.Saved(logger, blocked.TrustState);
            return blocked;
        }
        finally
        {
            accessGate.Release();
        }
    }

    public async Task<FriendRecord> UpdateMetadataAsync(
        DeviceId deviceId,
        string localDisplayName,
        string? localNote,
        CancellationToken cancellationToken)
    {
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetPath(deviceId);
            if (!File.Exists(path))
            {
                throw new KeyNotFoundException("The friend record does not exist.");
            }

            FriendRecord updated = (await ReadAsync(path, cancellationToken).ConfigureAwait(false))
                .WithMetadata(localDisplayName, localNote);
            await WriteAsync(path, FriendDocument.FromRecord(updated), cancellationToken).ConfigureAwait(false);
            FriendStoreEvents.Saved(logger, updated.TrustState);
            return updated;
        }
        finally
        {
            accessGate.Release();
        }
    }

    internal async Task<FriendRecord> SetTrustedAsync(
        DeviceId deviceId,
        ReadOnlyMemory<byte> expectedPublicKey,
        string relationshipSecretReference,
        DateTimeOffset successfulHandshakeAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationshipSecretReference);
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetPath(deviceId);
            if (!File.Exists(path))
            {
                throw new KeyNotFoundException("The friend record does not exist.");
            }

            FriendRecord existing = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (!existing.FriendPublicKey.AsSpan().SequenceEqual(expectedPublicKey.Span))
            {
                throw new FriendStoreException("A friend public key cannot be replaced during handshake.");
            }

            if (existing.TrustState != FriendTrustState.Pending)
            {
                throw new FriendStoreException("Only a pending friend can become trusted.");
            }

            FriendRecord trusted = existing.WithTrusted(
                relationshipSecretReference,
                successfulHandshakeAtUtc);
            await WriteAsync(path, FriendDocument.FromRecord(trusted), cancellationToken).ConfigureAwait(false);
            FriendStoreEvents.Saved(logger, trusted.TrustState);
            return trusted;
        }
        finally
        {
            accessGate.Release();
        }
    }

    internal async Task RestoreAsync(
        IReadOnlyList<FriendRecord> records,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(friendsRoot);
            foreach (FriendRecord record in records)
            {
                ArgumentNullException.ThrowIfNull(record);
                await WriteAsync(
                    GetPath(record.FriendDeviceId),
                    FriendDocument.FromRecord(record),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            accessGate.Release();
        }
    }

    public async Task RemoveAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(GetPath(deviceId));
            FriendStoreEvents.Removed(logger);
        }
        finally
        {
            accessGate.Release();
        }
    }

    public void Dispose() => accessGate.Dispose();

    private string GetPath(DeviceId deviceId) => Path.Combine(
        friendsRoot,
        $"{deviceId.Value["device-v1:".Length..]}.friend.motara.json");

    private static async Task<FriendRecord> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        FriendDocument document = await JsonSerializer.DeserializeAsync<FriendDocument>(
            stream, SerializerOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Friend record is empty.");
        return document.ToRecord();
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        string temporaryPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
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

    private sealed record FriendDocument(
        int SchemaVersion,
        string FriendDeviceId,
        string FriendPublicKey,
        string LocalDisplayName,
        string? LocalNote,
        string? RelationshipSecretReference,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? LastSuccessfulHandshakeAtUtc,
        FriendTrustState TrustState,
        DateTimeOffset? BlockedAtUtc)
    {
        internal static FriendDocument FromRecord(FriendRecord record) => new(
            record.SchemaVersion,
            record.FriendDeviceId.Value,
            Convert.ToBase64String(record.FriendPublicKey.ToArray()),
            record.LocalDisplayName,
            record.LocalNote,
            record.RelationshipSecretReference,
            record.CreatedAtUtc,
            record.LastSuccessfulHandshakeAtUtc,
            record.TrustState,
            record.BlockedAtUtc);

        internal FriendRecord ToRecord() => new(
            SchemaVersion,
            DeviceId.Parse(FriendDeviceId),
            Convert.FromBase64String(FriendPublicKey),
            LocalDisplayName,
            LocalNote,
            RelationshipSecretReference,
            CreatedAtUtc,
            LastSuccessfulHandshakeAtUtc,
            TrustState,
            BlockedAtUtc);
    }
}
