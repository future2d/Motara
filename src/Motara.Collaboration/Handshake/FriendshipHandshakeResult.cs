using System.Collections.Immutable;

namespace Motara.Collaboration.Handshake;

public enum FriendshipHandshakeResultCode
{
    Completed,
    AlreadyTrusted,
    Blocked,
    UnknownFriend,
    InvalidMessage,
    IdentityConflict,
    SignatureInvalid,
    Expired,
    TranscriptMismatch,
    SecretSaveFailed,
    SaveFailed,
}

public sealed record HandshakeAcceptResult(
    FriendshipHandshakeResultCode Code,
    ImmutableArray<byte> ResponseBytes)
{
    internal static HandshakeAcceptResult WithoutResponse(FriendshipHandshakeResultCode code) =>
        new(code, []);
}

public sealed record HandshakeCompleteResult(FriendshipHandshakeResultCode Code);
