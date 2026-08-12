using System.Collections.Immutable;
using Motara.Collaboration.Identity;
using Motara.Collaboration.Invites;

namespace Motara.Collaboration.Transport;

[Flags]
public enum PeerMemberPermissions
{
    None = 0,
    Model = 1,
    Drive = 2,
}

public sealed record PeerMemberCredential(
    int SchemaVersion,
    CollaborationSessionId SessionId,
    DeviceId IssuerDeviceId,
    ImmutableArray<byte> IssuerPublicKey,
    DeviceId MemberDeviceId,
    ImmutableArray<byte> MemberPublicKey,
    PeerMemberPermissions Permissions,
    DateTimeOffset ExpiresAtUtc,
    ImmutableArray<byte> Signature);
