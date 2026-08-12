using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Friends;
using Motara.Collaboration.Identity;
using Motara.Collaboration.Invites;

namespace Motara.Collaboration.Migration;

public sealed class CollaborationIdentityArchiveService
{
    private const string Magic = "MOTARA-IDENTITY";
    private const int SchemaVersion = 1;
    private const int KdfIterations = 600_000;
    private const int MaximumArchiveBytes = 64 * 1024 * 1024;
    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int KeyLength = 32;
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes(
        $"{Magic}|{SchemaVersion}|PBKDF2-HMAC-SHA256|{KdfIterations}|AES-256-GCM");
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string collaborationRoot;
    private readonly IDeviceSecretProtector protector;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<CollaborationIdentityArchiveService> logger;

    public CollaborationIdentityArchiveService(
        string collaborationRoot,
        IDeviceSecretProtector protector,
        TimeProvider timeProvider,
        ILogger<CollaborationIdentityArchiveService>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collaborationRoot);
        this.collaborationRoot = Path.GetFullPath(collaborationRoot);
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? NullLogger<CollaborationIdentityArchiveService>.Instance;
    }

    public async Task ExportAsync(
        string destinationPath,
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ValidatePassphrase(passphrase);
        long started = Stopwatch.GetTimestamp();
        byte[]? privateSeed = null;
        byte[]? plaintext = null;
        char[]? passwordChars = null;
        byte[]? passwordBytes = null;
        byte[]? key = null;
        ArchivePayload? payload = null;
        try
        {
            using (var identityStore = new DeviceIdentityStore(
                collaborationRoot, protector, timeProvider))
            await using (DeviceIdentityHandle identity = await identityStore.LoadOrCreateAsync(
                cancellationToken).ConfigureAwait(false))
            using (var friendStore = new FriendStore(collaborationRoot))
            using (var secretStore = new RelationshipSecretStore(
                collaborationRoot, protector, timeProvider))
            using (var consumedStore = new ConsumedInviteStore(collaborationRoot, timeProvider))
            {
                privateSeed = identity.CopyPrivateSeed();
                IReadOnlyList<FriendRecord> friends = await friendStore.ListAsync(cancellationToken)
                    .ConfigureAwait(false);
                var secrets = new List<SecretSnapshot>();
                foreach (FriendRecord friend in friends.Where(
                    friend => !string.IsNullOrWhiteSpace(friend.RelationshipSecretReference)))
                {
                    byte[] secret = await secretStore.LoadAsync(
                        friend.FriendDeviceId,
                        cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException("A referenced relationship secret is missing.");
                    try
                    {
                        secrets.Add(new SecretSnapshot(
                            friend.FriendDeviceId.Value,
                            secret.ToArray()));
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(secret);
                    }
                }

                IReadOnlyList<ConsumedInviteSnapshot> consumed = await consumedStore.ExportAsync(
                    cancellationToken).ConfigureAwait(false);
                payload = new ArchivePayload(
                    SchemaVersion,
                    timeProvider.GetUtcNow(),
                    IdentitySnapshot.From(identity.Identity, privateSeed),
                    friends.Select(FriendSnapshot.From).ToArray(),
                    secrets.ToArray(),
                    consumed.Select(entry => new ConsumedSnapshot(
                        entry.Nonce,
                        entry.ExpiresAtUtc)).ToArray());
            }

            plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
            if (plaintext.Length > MaximumArchiveBytes)
            {
                throw new InvalidDataException("The collaboration identity payload is too large.");
            }

            byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
            passwordChars = passphrase.ToArray();
            passwordBytes = Encoding.UTF8.GetBytes(passwordChars);
            key = Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                KdfIterations,
                HashAlgorithmName.SHA256,
                KeyLength);
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[TagLength];
            using (var aes = new AesGcm(key, TagLength))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);
            }

            var envelope = new ArchiveEnvelope(
                Magic,
                SchemaVersion,
                KdfIterations,
                Convert.ToBase64String(salt),
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(ciphertext),
                Convert.ToBase64String(tag));
            await WriteEnvelopeAtomicallyAsync(
                Path.GetFullPath(destinationPath),
                envelope,
                cancellationToken).ConfigureAwait(false);
            CollaborationIdentityArchiveEvents.Exported(
                logger,
                payload.Friends.Length,
                payload.Secrets.Length,
                payload.ConsumedInvites.Length,
                ElapsedMilliseconds(started));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            CollaborationIdentityArchiveEvents.Failed(logger, "export", exception.GetType().Name);
            throw Wrap("The collaboration identity archive could not be exported.", exception);
        }
        finally
        {
            Zero(privateSeed);
            Zero(plaintext);
            Clear(passwordChars);
            Zero(passwordBytes);
            Zero(key);
            payload?.ClearSecrets();
        }
    }

    public async Task<CollaborationIdentityArchiveInspection> InspectAsync(
        string sourcePath,
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken)
    {
        ArchivePayload? payload = null;
        try
        {
            payload = await ReadPayloadAsync(
                sourcePath,
                passphrase,
                cancellationToken).ConfigureAwait(false);
            ValidatedArchive validated = payload.Validate();
            validated.ClearSecrets();
            CollaborationIdentityArchiveEvents.Inspected(
                logger,
                payload.Friends.Length,
                payload.Secrets.Length,
                payload.ConsumedInvites.Length);
            return new CollaborationIdentityArchiveInspection(
                ShortDeviceId(payload.Identity.DeviceId),
                payload.Friends.Length,
                payload.Secrets.Length,
                payload.ConsumedInvites.Length,
                payload.ExportedAtUtc);
        }
        finally
        {
            payload?.ClearSecrets();
        }
    }

    public async Task ImportAsync(
        string sourcePath,
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        string parent = Path.GetDirectoryName(collaborationRoot)
            ?? throw new CollaborationIdentityArchiveException("The collaboration root has no parent directory.");
        string name = Path.GetFileName(collaborationRoot);
        string operationId = Guid.NewGuid().ToString("N");
        string stagingRoot = Path.Combine(parent, $".{name}.import-{operationId}");
        string backupRoot = Path.Combine(parent, $".{name}.backup-{operationId}");
        ArchivePayload? payload = null;
        ValidatedArchive? validated = null;
        bool committed = false;
        try
        {
            payload = await ReadPayloadAsync(sourcePath, passphrase, cancellationToken)
                .ConfigureAwait(false);
            validated = payload.Validate();
            Directory.CreateDirectory(parent);
            await MaterializeAsync(stagingRoot, validated, cancellationToken).ConfigureAwait(false);
            await VerifyStagingAsync(stagingRoot, validated, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceRoot(stagingRoot, backupRoot);
            committed = true;
            CollaborationIdentityArchiveEvents.Imported(
                logger,
                validated.Friends.Count,
                validated.Secrets.Count,
                validated.ConsumedInvites.Count,
                ElapsedMilliseconds(started));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            CollaborationIdentityArchiveEvents.Failed(logger, "import", exception.GetType().Name);
            throw Wrap("The collaboration identity archive could not be imported.", exception);
        }
        finally
        {
            validated?.ClearSecrets();
            payload?.ClearSecrets();
            TryDeleteOwnedDirectory(stagingRoot);
            if (committed)
            {
                TryDeleteOwnedDirectory(backupRoot);
            }
        }
    }

    private static async Task<ArchivePayload> ReadPayloadAsync(
        string sourcePath,
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ValidatePassphrase(passphrase);
        string fullPath = Path.GetFullPath(sourcePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length is <= 0 or > MaximumArchiveBytes)
        {
            throw new CollaborationIdentityArchiveException("The collaboration identity archive size is invalid.");
        }

        char[]? passwordChars = null;
        byte[]? passwordBytes = null;
        byte[]? key = null;
        byte[]? plaintext = null;
        try
        {
            await using FileStream stream = new(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            ArchiveEnvelope envelope = await JsonSerializer.DeserializeAsync<ArchiveEnvelope>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The collaboration identity archive is empty.");
            if (envelope.Magic != Magic
                || envelope.SchemaVersion != SchemaVersion
                || envelope.KdfIterations != KdfIterations)
            {
                throw new InvalidDataException("The collaboration identity archive header is invalid.");
            }

            byte[] salt = Convert.FromBase64String(envelope.Salt);
            byte[] nonce = Convert.FromBase64String(envelope.Nonce);
            byte[] ciphertext = Convert.FromBase64String(envelope.Ciphertext);
            byte[] tag = Convert.FromBase64String(envelope.Tag);
            if (salt.Length != SaltLength
                || nonce.Length != NonceLength
                || tag.Length != TagLength
                || ciphertext.Length == 0
                || ciphertext.Length > MaximumArchiveBytes)
            {
                throw new InvalidDataException("The collaboration identity archive fields are invalid.");
            }

            passwordChars = passphrase.ToArray();
            passwordBytes = Encoding.UTF8.GetBytes(passwordChars);
            key = Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                KdfIterations,
                HashAlgorithmName.SHA256,
                KeyLength);
            plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(key, TagLength))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData);
            }

            return JsonSerializer.Deserialize<ArchivePayload>(plaintext, SerializerOptions)
                ?? throw new InvalidDataException("The collaboration identity archive payload is empty.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Wrap("The collaboration identity archive is invalid.", exception);
        }
        finally
        {
            Clear(passwordChars);
            Zero(passwordBytes);
            Zero(key);
            Zero(plaintext);
        }
    }

    private async Task MaterializeAsync(
        string stagingRoot,
        ValidatedArchive archive,
        CancellationToken cancellationToken)
    {
        using (var identityStore = new DeviceIdentityStore(stagingRoot, protector, timeProvider))
        {
            await identityStore.RestoreAsync(
                archive.Identity,
                archive.PrivateSeed,
                cancellationToken).ConfigureAwait(false);
        }

        using (var friendStore = new FriendStore(stagingRoot))
        {
            await friendStore.RestoreAsync(archive.Friends, cancellationToken).ConfigureAwait(false);
        }

        using (var secretStore = new RelationshipSecretStore(stagingRoot, protector, timeProvider))
        {
            foreach ((DeviceId deviceId, byte[] secret) in archive.Secrets)
            {
                try
                {
                    await secretStore.SaveAsync(deviceId, secret, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(secret);
                }
            }
        }

        using var consumedStore = new ConsumedInviteStore(stagingRoot, timeProvider);
        await consumedStore.RestoreAsync(archive.ConsumedInvites, cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyStagingAsync(
        string stagingRoot,
        ValidatedArchive archive,
        CancellationToken cancellationToken)
    {
        using var identityStore = new DeviceIdentityStore(stagingRoot, protector, timeProvider);
        await using DeviceIdentityHandle identity = await identityStore.LoadOrCreateAsync(cancellationToken)
            .ConfigureAwait(false);
        if (identity.Identity.DeviceId != archive.Identity.DeviceId)
        {
            throw new InvalidDataException("The staged identity could not be verified.");
        }

        using var friendStore = new FriendStore(stagingRoot);
        IReadOnlyList<FriendRecord> records = await friendStore.ListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (records.Count != archive.Friends.Count)
        {
            throw new InvalidDataException("The staged friend records could not be verified.");
        }
    }

    private void ReplaceRoot(string stagingRoot, string backupRoot)
    {
        bool hadOriginal = Directory.Exists(collaborationRoot);
        if (hadOriginal)
        {
            Directory.Move(collaborationRoot, backupRoot);
        }

        try
        {
            Directory.Move(stagingRoot, collaborationRoot);
        }
        catch
        {
            if (hadOriginal && Directory.Exists(backupRoot) && !Directory.Exists(collaborationRoot))
            {
                Directory.Move(backupRoot, collaborationRoot);
            }

            throw;
        }
    }

    private static async Task WriteEnvelopeAtomicallyAsync(
        string path,
        ArchiveEnvelope envelope,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("The archive destination has no directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
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
                    envelope,
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

    private static void ValidatePassphrase(ReadOnlyMemory<char> passphrase)
    {
        if (passphrase.IsEmpty || passphrase.Length > 1024)
        {
            throw new ArgumentException("The migration passphrase length is invalid.", nameof(passphrase));
        }
    }

    private static CollaborationIdentityArchiveException Wrap(string message, Exception exception) =>
        exception as CollaborationIdentityArchiveException
        ?? new CollaborationIdentityArchiveException(message, exception);

    private void TryDeleteOwnedDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CollaborationIdentityArchiveEvents.Failed(
                logger,
                "cleanup",
                exception.GetType().Name);
        }
    }

    private static void Zero(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private static void Clear(char[]? value)
    {
        if (value is not null)
        {
            Array.Clear(value);
        }
    }

    private static long ElapsedMilliseconds(long started) =>
        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static string ShortDeviceId(string value) => value.Length <= 20
        ? value
        : $"{value[..13]}...{value[^6..]}";

    private sealed record ArchiveEnvelope(
        string Magic,
        int SchemaVersion,
        int KdfIterations,
        string Salt,
        string Nonce,
        string Ciphertext,
        string Tag);

    private sealed record ArchivePayload(
        int SchemaVersion,
        DateTimeOffset ExportedAtUtc,
        IdentitySnapshot Identity,
        FriendSnapshot[] Friends,
        SecretSnapshot[] Secrets,
        ConsumedSnapshot[] ConsumedInvites)
    {
        internal ValidatedArchive Validate()
        {
            if (Identity is null
                || Friends is null
                || Secrets is null
                || ConsumedInvites is null
                || SchemaVersion != CollaborationIdentityArchiveService.SchemaVersion
                || Friends.Length > 1024
                || Secrets.Length > 1024
                || ConsumedInvites.Length > 100_000)
            {
                throw new InvalidDataException("The collaboration identity archive payload is invalid.");
            }

            byte[]? privateSeed = null;
            var secrets = new List<(DeviceId DeviceId, byte[] Secret)>();
            try
            {
                DeviceIdentity identity = Identity.ToIdentity();
                privateSeed = Identity.PrivateSeed.ToArray();
                var friends = Friends.Select(friend => friend.ToFriend()).ToArray();
                if (friends.Select(friend => friend.FriendDeviceId).Distinct().Count() != friends.Length)
                {
                    throw new InvalidDataException("The archive contains duplicate friends.");
                }

                secrets.AddRange(Secrets.Select(secret => secret.ToSecret()));
                if (secrets.Select(secret => secret.DeviceId).Distinct().Count() != secrets.Count)
                {
                    throw new InvalidDataException("The archive contains duplicate relationship secrets.");
                }

                var secretIds = secrets.Select(secret => secret.DeviceId).ToHashSet();
                var referencedIds = friends
                    .Where(friend => !string.IsNullOrWhiteSpace(friend.RelationshipSecretReference))
                    .Select(friend => friend.FriendDeviceId)
                    .ToHashSet();
                if (!secretIds.SetEquals(referencedIds))
                {
                    throw new InvalidDataException("The archive relationship secrets are inconsistent.");
                }

                var consumed = ConsumedInvites
                    .Select(entry => new ConsumedInviteSnapshot(entry.Nonce, entry.ExpiresAtUtc))
                    .ToArray();
                ValidatedArchive result = new(identity, privateSeed, friends, secrets.ToArray(), consumed);
                privateSeed = null;
                secrets.Clear();
                return result;
            }
            finally
            {
                Zero(privateSeed);
                foreach ((_, byte[] secret) in secrets)
                {
                    Zero(secret);
                }
            }
        }

        internal void ClearSecrets()
        {
            if (Identity?.PrivateSeed is { } privateSeed)
            {
                CryptographicOperations.ZeroMemory(privateSeed);
            }

            foreach (SecretSnapshot secret in Secrets ?? [])
            {
                if (secret?.Secret is { } value)
                {
                    CryptographicOperations.ZeroMemory(value);
                }
            }
        }
    }

    private sealed record IdentitySnapshot(
        int SchemaVersion,
        string DeviceId,
        byte[] PublicKey,
        byte[] PrivateSeed,
        DateTimeOffset CreatedAtUtc)
    {
        internal static IdentitySnapshot From(DeviceIdentity identity, byte[] privateSeed) => new(
            identity.SchemaVersion,
            identity.DeviceId.Value,
            identity.PublicKey.ToArray(),
            privateSeed.ToArray(),
            identity.CreatedAtUtc);

        internal DeviceIdentity ToIdentity() => new(
            SchemaVersion,
            Identity.DeviceId.Parse(DeviceId),
            PublicKey,
            "device.identity.secret",
            CreatedAtUtc);
    }

    private sealed record FriendSnapshot(
        int SchemaVersion,
        string DeviceId,
        string PublicKey,
        string DisplayName,
        string? Note,
        string? SecretReference,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? LastHandshakeAtUtc,
        FriendTrustState TrustState,
        DateTimeOffset? BlockedAtUtc)
    {
        internal static FriendSnapshot From(FriendRecord friend) => new(
            friend.SchemaVersion,
            friend.FriendDeviceId.Value,
            Convert.ToBase64String(friend.FriendPublicKey.ToArray()),
            friend.LocalDisplayName,
            friend.LocalNote,
            friend.RelationshipSecretReference,
            friend.CreatedAtUtc,
            friend.LastSuccessfulHandshakeAtUtc,
            friend.TrustState,
            friend.BlockedAtUtc);

        internal FriendRecord ToFriend() => new(
            SchemaVersion,
            Identity.DeviceId.Parse(DeviceId),
            Convert.FromBase64String(PublicKey),
            DisplayName,
            Note,
            SecretReference,
            CreatedAtUtc,
            LastHandshakeAtUtc,
            TrustState,
            BlockedAtUtc);
    }

    private sealed record SecretSnapshot(string DeviceId, byte[] Secret)
    {
        internal (DeviceId DeviceId, byte[] Secret) ToSecret()
        {
            byte[] secret = Secret.ToArray();
            if (secret.Length != 32)
            {
                CryptographicOperations.ZeroMemory(secret);
                throw new InvalidDataException("A relationship secret has an invalid length.");
            }

            return (Identity.DeviceId.Parse(DeviceId), secret);
        }
    }

    private sealed record ConsumedSnapshot(string Nonce, DateTimeOffset ExpiresAtUtc);

    private sealed record ValidatedArchive(
        DeviceIdentity Identity,
        byte[] PrivateSeed,
        IReadOnlyList<FriendRecord> Friends,
        IReadOnlyList<(DeviceId DeviceId, byte[] Secret)> Secrets,
        IReadOnlyList<ConsumedInviteSnapshot> ConsumedInvites)
    {
        internal void ClearSecrets()
        {
            CryptographicOperations.ZeroMemory(PrivateSeed);
            foreach ((_, byte[] secret) in Secrets)
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }
    }
}
