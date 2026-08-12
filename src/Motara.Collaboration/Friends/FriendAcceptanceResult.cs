using Motara.Collaboration.Identity;

namespace Motara.Collaboration.Friends;

public enum FriendAcceptanceResultCode
{
    AcceptedPending,
    AlreadyExists,
    AlreadyProcessed,
    SelfInvite,
    Blocked,
    IdentityConflict,
    InvalidInvite,
    SaveFailed,
}

public sealed record FriendAcceptanceResult(
    FriendAcceptanceResultCode Code,
    DeviceId? FriendDeviceId = null,
    string? InviteNonce = null);
