namespace Motara.Collaboration.Invites;

public enum InviteErrorCode
{
    None,
    Malformed,
    TooLarge,
    Unsupported,
    InvalidSignature,
    Expired,
    NotYetValid,
    InconsistentIdentity,
}

public sealed record InviteValidationResult(
    FriendInvite? Invite,
    InviteErrorCode ErrorCode)
{
    public bool IsValid => Invite is not null && ErrorCode == InviteErrorCode.None;

    internal static InviteValidationResult Valid(FriendInvite invite) => new(invite, InviteErrorCode.None);

    internal static InviteValidationResult Invalid(InviteErrorCode errorCode) => new(null, errorCode);
}
