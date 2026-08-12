using System.Collections.Immutable;
using Motara.Collaboration.Identity;

namespace Motara.Collaboration.Handshake;

internal sealed record HandshakeOffer(
    int SchemaVersion,
    int ProtocolVersion,
    ImmutableArray<byte> SessionNonce,
    DeviceId InitiatorDeviceId,
    ImmutableArray<byte> InitiatorPublicKey,
    DeviceId ResponderDeviceId,
    ImmutableArray<byte> InitiatorEphemeralPublicKey,
    DateTimeOffset ExpiresAtUtc,
    ImmutableArray<byte> Signature);

internal sealed record HandshakeResponse(
    int SchemaVersion,
    int ProtocolVersion,
    ImmutableArray<byte> SessionNonce,
    DeviceId InitiatorDeviceId,
    DeviceId ResponderDeviceId,
    ImmutableArray<byte> ResponderPublicKey,
    ImmutableArray<byte> ResponderEphemeralPublicKey,
    DateTimeOffset ExpiresAtUtc,
    ImmutableArray<byte> Signature);

internal sealed class HandshakeProtocolException : Exception
{
    internal HandshakeProtocolException(
        string message,
        HandshakeProtocolError error = HandshakeProtocolError.InvalidMessage,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    internal HandshakeProtocolError Error { get; }
}

internal enum HandshakeProtocolError
{
    InvalidMessage,
    IdentityConflict,
    SignatureInvalid,
    Expired,
    TranscriptMismatch,
}
