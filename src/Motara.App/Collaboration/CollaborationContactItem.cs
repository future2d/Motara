using Motara.Collaboration.Friends;
using Motara.Collaboration.Identity;

namespace Motara.App.Collaboration;

internal enum CollaborationContactStatus
{
    Pending,
    Trusted,
    Blocked,
}

internal sealed record CollaborationContactItem(
    DeviceId DeviceId,
    string DisplayName,
    string? Note,
    CollaborationContactStatus Status)
{
    internal static CollaborationContactItem FromRecord(FriendRecord record) => new(
        record.FriendDeviceId,
        record.LocalDisplayName,
        record.LocalNote,
        record.TrustState switch
        {
            FriendTrustState.Pending => CollaborationContactStatus.Pending,
            FriendTrustState.Trusted => CollaborationContactStatus.Trusted,
            FriendTrustState.Blocked => CollaborationContactStatus.Blocked,
            _ => throw new ArgumentOutOfRangeException(nameof(record)),
        });
}
