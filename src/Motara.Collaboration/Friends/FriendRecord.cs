using Motara.Collaboration.Identity;
using System.Collections.Immutable;

namespace Motara.Collaboration.Friends;

public sealed record FriendRecord
{
    public const int CurrentSchemaVersion = 1;

    public FriendRecord(
        int schemaVersion,
        DeviceId friendDeviceId,
        byte[] friendPublicKey,
        string localDisplayName,
        string? localNote,
        string? relationshipSecretReference,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? lastSuccessfulHandshakeAtUtc,
        FriendTrustState trustState,
        DateTimeOffset? blockedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(friendPublicKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(localDisplayName);
        if (schemaVersion != CurrentSchemaVersion
            || friendPublicKey.Length != 32
            || DeviceId.FromEd25519PublicKey(friendPublicKey) != friendDeviceId
            || localDisplayName.Length > 80
            || localNote?.Length > 500
            || !Enum.IsDefined(trustState)
            || (trustState == FriendTrustState.Trusted
                && (string.IsNullOrWhiteSpace(relationshipSecretReference)
                    || !lastSuccessfulHandshakeAtUtc.HasValue))
            || (trustState == FriendTrustState.Blocked) != blockedAtUtc.HasValue)
        {
            throw new ArgumentException("The friend record is invalid.");
        }

        SchemaVersion = schemaVersion;
        FriendDeviceId = friendDeviceId;
        FriendPublicKey = ImmutableArray.CreateRange(friendPublicKey);
        LocalDisplayName = localDisplayName;
        LocalNote = localNote;
        RelationshipSecretReference = relationshipSecretReference;
        CreatedAtUtc = createdAtUtc;
        LastSuccessfulHandshakeAtUtc = lastSuccessfulHandshakeAtUtc;
        TrustState = trustState;
        BlockedAtUtc = blockedAtUtc;
    }

    public int SchemaVersion { get; }
    public DeviceId FriendDeviceId { get; }
    public ImmutableArray<byte> FriendPublicKey { get; }
    public string LocalDisplayName { get; }
    public string? LocalNote { get; }
    public string? RelationshipSecretReference { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? LastSuccessfulHandshakeAtUtc { get; }
    public FriendTrustState TrustState { get; }
    public DateTimeOffset? BlockedAtUtc { get; }

    public static FriendRecord Pending(
        byte[] publicKey,
        string localDisplayName,
        DateTimeOffset createdAtUtc) => new(
            CurrentSchemaVersion,
            DeviceId.FromEd25519PublicKey(publicKey),
            publicKey,
            localDisplayName,
            null,
            null,
            createdAtUtc,
            null,
            FriendTrustState.Pending,
            null);

    internal FriendRecord WithBlocked(DateTimeOffset blockedAtUtc) => new(
        SchemaVersion,
        FriendDeviceId,
        FriendPublicKey.ToArray(),
        LocalDisplayName,
        LocalNote,
        RelationshipSecretReference,
        CreatedAtUtc,
        LastSuccessfulHandshakeAtUtc,
        FriendTrustState.Blocked,
        blockedAtUtc);

    internal FriendRecord WithMetadata(string localDisplayName, string? localNote) => new(
        SchemaVersion,
        FriendDeviceId,
        FriendPublicKey.ToArray(),
        localDisplayName,
        localNote,
        RelationshipSecretReference,
        CreatedAtUtc,
        LastSuccessfulHandshakeAtUtc,
        TrustState,
        BlockedAtUtc);

    internal FriendRecord WithTrusted(
        string relationshipSecretReference,
        DateTimeOffset successfulHandshakeAtUtc) => new(
        SchemaVersion,
        FriendDeviceId,
        FriendPublicKey.ToArray(),
        LocalDisplayName,
        LocalNote,
        relationshipSecretReference,
        CreatedAtUtc,
        successfulHandshakeAtUtc,
        FriendTrustState.Trusted,
        null);
}

public sealed class FriendStoreException : Exception
{
    public FriendStoreException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
