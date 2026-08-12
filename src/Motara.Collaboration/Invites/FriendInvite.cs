using Motara.Collaboration.Identity;
using System.Collections.Immutable;

namespace Motara.Collaboration.Invites;

public sealed record FriendInvite(
    int SchemaVersion,
    int ProtocolVersion,
    DeviceId InviterDeviceId,
    ImmutableArray<byte> InviterPublicKey,
    string InviterDisplayName,
    string InviteNonce,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);
