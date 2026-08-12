using System.Collections.Immutable;
using Motara.Collaboration.Identity;

namespace Motara.Collaboration.Invites;

public sealed record SessionInvite(
    int SchemaVersion,
    int ProtocolVersion,
    CollaborationSessionId SessionId,
    DeviceId HostDeviceId,
    ImmutableArray<byte> HostPublicKey,
    SessionJoinPolicy JoinPolicy,
    string InviteNonce,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public enum SessionInviteErrorCode
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

public sealed record SessionInviteValidationResult(
    SessionInvite? Invite,
    SessionInviteErrorCode ErrorCode)
{
    public bool IsValid => Invite is not null && ErrorCode == SessionInviteErrorCode.None;

    internal static SessionInviteValidationResult Valid(SessionInvite invite) =>
        new(invite, SessionInviteErrorCode.None);

    internal static SessionInviteValidationResult Invalid(SessionInviteErrorCode errorCode) =>
        new(null, errorCode);
}
