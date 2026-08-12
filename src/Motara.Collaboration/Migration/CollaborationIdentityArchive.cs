namespace Motara.Collaboration.Migration;

public sealed record CollaborationIdentityArchiveInspection(
    string DeviceIdSummary,
    int FriendCount,
    int RelationshipSecretCount,
    int ConsumedInviteCount,
    DateTimeOffset ExportedAtUtc);

public sealed class CollaborationIdentityArchiveException : Exception
{
    public CollaborationIdentityArchiveException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
