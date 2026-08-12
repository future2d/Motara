using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Motara.Collaboration.Identity;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Motara.Collaboration.Handshake;

internal static class FriendshipHandshakeCryptography
{
    internal const int SchemaVersion = 1;
    internal const int ProtocolVersion = 1;
    internal const int MaximumMessageBytes = 16 * 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    internal static HandshakeOfferHandle CreateOffer(
        DeviceIdentityHandle initiator,
        DeviceId responderDeviceId,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(initiator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (initiator.Identity.DeviceId == responderDeviceId)
        {
            throw new HandshakeProtocolException(
                "A device cannot create a friendship handshake with itself.",
                HandshakeProtocolError.IdentityConflict);
        }

        byte[] nonce = RandomNumberGenerator.GetBytes(32);
        byte[] ephemeralPrivateKey = RandomNumberGenerator.GetBytes(32);
        byte[]? unsigned = null;
        byte[]? signature = null;
        try
        {
            byte[] ephemeralPublicKey = new X25519PrivateKeyParameters(ephemeralPrivateKey)
                .GeneratePublicKey()
                .GetEncoded();
            DateTimeOffset expiresAtUtc = timeProvider.GetUtcNow().Add(Lifetime);
            unsigned = SerializeUnsignedOffer(
                nonce,
                initiator.Identity.DeviceId,
                initiator.Identity.PublicKey,
                responderDeviceId,
                ephemeralPublicKey,
                expiresAtUtc);
            signature = initiator.Sign(unsigned);
            var offer = new HandshakeOffer(
                SchemaVersion,
                ProtocolVersion,
                ImmutableArray.CreateRange(nonce),
                initiator.Identity.DeviceId,
                initiator.Identity.PublicKey,
                responderDeviceId,
                ImmutableArray.CreateRange(ephemeralPublicKey),
                expiresAtUtc,
                ImmutableArray.CreateRange(signature));
            byte[] messageBytes = Serialize(offer);
            return new HandshakeOfferHandle(offer, messageBytes, ephemeralPrivateKey);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(ephemeralPrivateKey);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            ZeroIfPresent(unsigned);
            ZeroIfPresent(signature);
        }
    }

    internal static HandshakeResponseHandle CreateResponse(
        DeviceIdentityHandle responder,
        ReadOnlyMemory<byte> offerMessageBytes,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(responder);
        ArgumentNullException.ThrowIfNull(timeProvider);
        HandshakeOffer offer = DeserializeOffer(offerMessageBytes.Span);
        ValidateOffer(offer, responder.Identity.DeviceId, timeProvider.GetUtcNow());

        byte[] ephemeralPrivateKey = RandomNumberGenerator.GetBytes(32);
        byte[]? unsigned = null;
        byte[]? transcript = null;
        byte[]? signature = null;
        try
        {
            byte[] ephemeralPublicKey = new X25519PrivateKeyParameters(ephemeralPrivateKey)
                .GeneratePublicKey()
                .GetEncoded();
            unsigned = SerializeUnsignedResponse(
                offer.SessionNonce,
                offer.InitiatorDeviceId,
                responder.Identity.DeviceId,
                responder.Identity.PublicKey,
                ephemeralPublicKey,
                offer.ExpiresAtUtc);
            transcript = Combine(offerMessageBytes, unsigned);
            signature = responder.Sign(transcript);
            var response = new HandshakeResponse(
                SchemaVersion,
                ProtocolVersion,
                offer.SessionNonce,
                offer.InitiatorDeviceId,
                responder.Identity.DeviceId,
                responder.Identity.PublicKey,
                ImmutableArray.CreateRange(ephemeralPublicKey),
                offer.ExpiresAtUtc,
                ImmutableArray.CreateRange(signature));
            byte[] messageBytes = Serialize(response);
            return new HandshakeResponseHandle(
                offer,
                offerMessageBytes.ToArray(),
                response,
                messageBytes,
                ephemeralPrivateKey);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(ephemeralPrivateKey);
            throw;
        }
        finally
        {
            ZeroIfPresent(unsigned);
            ZeroIfPresent(transcript);
            ZeroIfPresent(signature);
        }
    }

    internal static byte[] DeriveInitiatorSecret(
        HandshakeOfferHandle offerHandle,
        ReadOnlyMemory<byte> responseMessageBytes,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(offerHandle);
        ArgumentNullException.ThrowIfNull(timeProvider);
        HandshakeResponse response = DeserializeResponse(responseMessageBytes.Span);
        ValidateResponse(
            response,
            offerHandle.Offer,
            offerHandle.MessageBytes.AsSpan().ToArray(),
            timeProvider.GetUtcNow());
        return DeriveInitiatorSecretCore(offerHandle, response);
    }

    internal static byte[] DeriveInitiatorSecret(
        HandshakeOfferHandle offerHandle,
        HandshakeResponse response,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(offerHandle);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ValidateResponse(
            response,
            offerHandle.Offer,
            offerHandle.MessageBytes.AsSpan().ToArray(),
            timeProvider.GetUtcNow());
        return DeriveInitiatorSecretCore(offerHandle, response);
    }

    internal static byte[] DeriveResponderSecret(HandshakeResponseHandle responseHandle)
    {
        ArgumentNullException.ThrowIfNull(responseHandle);
        byte[] privateKey = responseHandle.CopyEphemeralPrivateKey();
        try
        {
            return DeriveSecret(
                privateKey,
                responseHandle.Offer.InitiatorEphemeralPublicKey.AsSpan(),
                responseHandle.Offer,
                responseHandle.Response);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    internal static HandshakeOffer DeserializeOffer(ReadOnlySpan<byte> bytes) =>
        DeserializeCanonical<HandshakeOffer>(bytes, "offer");

    internal static HandshakeResponse DeserializeResponse(ReadOnlySpan<byte> bytes) =>
        DeserializeCanonical<HandshakeResponse>(bytes, "response");

    internal static byte[] Serialize(HandshakeOffer offer) =>
        JsonSerializer.SerializeToUtf8Bytes(offer, SerializerOptions);

    internal static byte[] Serialize(HandshakeResponse response) =>
        JsonSerializer.SerializeToUtf8Bytes(response, SerializerOptions);

    private static byte[] DeriveInitiatorSecretCore(
        HandshakeOfferHandle offerHandle,
        HandshakeResponse response)
    {
        byte[] privateKey = offerHandle.CopyEphemeralPrivateKey();
        try
        {
            return DeriveSecret(
                privateKey,
                response.ResponderEphemeralPublicKey.AsSpan(),
                offerHandle.Offer,
                response);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    private static T DeserializeCanonical<T>(ReadOnlySpan<byte> bytes, string messageKind)
    {
        ValidateMessageSize(bytes);
        try
        {
            T value = JsonSerializer.Deserialize<T>(bytes, SerializerOptions)
                ?? throw new HandshakeProtocolException($"The handshake {messageKind} is empty.");
            ValidateMessage(value);
            byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
            try
            {
                if (!bytes.SequenceEqual(canonical))
                {
                    throw new HandshakeProtocolException($"The handshake {messageKind} is not canonical.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonical);
            }
            return value;
        }
        catch (HandshakeProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            throw new HandshakeProtocolException(
                $"The handshake {messageKind} is malformed.",
                HandshakeProtocolError.InvalidMessage,
                exception);
        }
    }

    private static void ValidateMessage<T>(T value)
    {
        switch (value)
        {
            case HandshakeOffer offer:
                ValidateVersionAndLengths(
                    offer.SchemaVersion,
                    offer.ProtocolVersion,
                    offer.SessionNonce,
                    offer.InitiatorPublicKey,
                    offer.InitiatorEphemeralPublicKey,
                    offer.Signature);
                break;
            case HandshakeResponse response:
                ValidateVersionAndLengths(
                    response.SchemaVersion,
                    response.ProtocolVersion,
                    response.SessionNonce,
                    response.ResponderPublicKey,
                    response.ResponderEphemeralPublicKey,
                    response.Signature);
                break;
            default:
                throw new HandshakeProtocolException("The handshake message type is unsupported.");
        }
    }

    private static void ValidateOffer(
        HandshakeOffer offer,
        DeviceId expectedResponder,
        DateTimeOffset now)
    {
        if (offer.ExpiresAtUtc <= now || offer.ExpiresAtUtc > now.Add(Lifetime))
        {
            throw new HandshakeProtocolException(
                "The handshake offer has expired.",
                HandshakeProtocolError.Expired);
        }

        if (offer.ResponderDeviceId != expectedResponder)
        {
            throw new HandshakeProtocolException(
                "The handshake offer targets another device.",
                HandshakeProtocolError.TranscriptMismatch);
        }

        if (offer.InitiatorDeviceId == offer.ResponderDeviceId
            || DeviceId.FromEd25519PublicKey(offer.InitiatorPublicKey.AsSpan()) != offer.InitiatorDeviceId)
        {
            throw new HandshakeProtocolException(
                "The handshake offer identity is invalid.",
                HandshakeProtocolError.IdentityConflict);
        }

        byte[] unsigned = SerializeUnsignedOffer(
            offer.SessionNonce.AsSpan(),
            offer.InitiatorDeviceId,
            offer.InitiatorPublicKey,
            offer.ResponderDeviceId,
            offer.InitiatorEphemeralPublicKey.AsSpan(),
            offer.ExpiresAtUtc);
        try
        {
            VerifySignature(offer.InitiatorPublicKey, unsigned, offer.Signature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(unsigned);
        }
    }

    private static void ValidateResponse(
        HandshakeResponse response,
        HandshakeOffer offer,
        ReadOnlyMemory<byte> offerMessageBytes,
        DateTimeOffset now)
    {
        if (response.ExpiresAtUtc <= now)
        {
            throw new HandshakeProtocolException(
                "The handshake response has expired.",
                HandshakeProtocolError.Expired);
        }

        if (!response.SessionNonce.AsSpan().SequenceEqual(offer.SessionNonce.AsSpan())
            || response.InitiatorDeviceId != offer.InitiatorDeviceId
            || response.ResponderDeviceId != offer.ResponderDeviceId
            || response.ExpiresAtUtc != offer.ExpiresAtUtc)
        {
            throw new HandshakeProtocolException(
                "The handshake response transcript is invalid.",
                HandshakeProtocolError.TranscriptMismatch);
        }

        if (DeviceId.FromEd25519PublicKey(response.ResponderPublicKey.AsSpan()) != response.ResponderDeviceId)
        {
            throw new HandshakeProtocolException(
                "The handshake response identity is invalid.",
                HandshakeProtocolError.IdentityConflict);
        }

        byte[] unsigned = SerializeUnsignedResponse(
            response.SessionNonce,
            response.InitiatorDeviceId,
            response.ResponderDeviceId,
            response.ResponderPublicKey,
            response.ResponderEphemeralPublicKey.AsSpan(),
            response.ExpiresAtUtc);
        byte[] transcript = Combine(offerMessageBytes, unsigned);
        try
        {
            VerifySignature(response.ResponderPublicKey, transcript, response.Signature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(unsigned);
            CryptographicOperations.ZeroMemory(transcript);
        }
    }

    private static byte[] DeriveSecret(
        ReadOnlySpan<byte> ownPrivate,
        ReadOnlySpan<byte> peerPublic,
        HandshakeOffer offer,
        HandshakeResponse response)
    {
        byte[] sharedSecret = new byte[32];
        byte[] salt = offer.SessionNonce.ToArray();
        byte[] info = BuildInfo(offer, response);
        byte[] output = new byte[32];
        try
        {
            var privateKey = new X25519PrivateKeyParameters(ownPrivate.ToArray());
            privateKey.GenerateSecret(new X25519PublicKeyParameters(peerPublic.ToArray()), sharedSecret, 0);
            HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, output, salt, info);
            return output;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(output);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(info);
        }
    }

    private static byte[] BuildInfo(HandshakeOffer offer, HandshakeResponse response)
    {
        var tuples = new[]
        {
            (offer.InitiatorDeviceId.Value, offer.InitiatorPublicKey, offer.InitiatorEphemeralPublicKey),
            (response.ResponderDeviceId.Value, response.ResponderPublicKey, response.ResponderEphemeralPublicKey),
        };
        Array.Sort(tuples, static (left, right) => StringComparer.Ordinal.Compare(left.Item1, right.Item1));
        using var stream = new MemoryStream();
        stream.Write(Encoding.UTF8.GetBytes("motara-friendship-v1"));
        foreach (var tuple in tuples)
        {
            WriteLengthPrefixed(stream, Encoding.UTF8.GetBytes(tuple.Item1));
            WriteLengthPrefixed(stream, tuple.Item2.AsSpan());
            WriteLengthPrefixed(stream, tuple.Item3.AsSpan());
        }

        return stream.ToArray();
    }

    private static void WriteLengthPrefixed(Stream stream, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
    }

    private static void VerifySignature(
        ImmutableArray<byte> publicKey,
        byte[] data,
        ImmutableArray<byte> signature)
    {
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey.ToArray()));
        verifier.BlockUpdate(data, 0, data.Length);
        if (!verifier.VerifySignature(signature.ToArray()))
        {
            throw new HandshakeProtocolException(
                "The handshake signature is invalid.",
                HandshakeProtocolError.SignatureInvalid);
        }
    }

    private static void ValidateVersionAndLengths(
        int schemaVersion,
        int protocolVersion,
        ImmutableArray<byte> nonce,
        ImmutableArray<byte> publicKey,
        ImmutableArray<byte> ephemeralKey,
        ImmutableArray<byte> signature)
    {
        if (schemaVersion != SchemaVersion || protocolVersion != ProtocolVersion)
        {
            throw new HandshakeProtocolException("The handshake message version is unsupported.");
        }

        if (nonce.Length != 32 || publicKey.Length != 32 || ephemeralKey.Length != 32 || signature.Length != 64)
        {
            throw new HandshakeProtocolException("The handshake message has invalid lengths.");
        }
    }

    private static void ValidateMessageSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumMessageBytes)
        {
            throw new HandshakeProtocolException("The handshake message is outside the size limit.");
        }
    }

    private static byte[] SerializeUnsignedOffer(
        ReadOnlySpan<byte> nonce,
        DeviceId initiatorDeviceId,
        ImmutableArray<byte> initiatorPublicKey,
        DeviceId responderDeviceId,
        ReadOnlySpan<byte> initiatorEphemeralPublicKey,
        DateTimeOffset expiresAtUtc) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion,
            ProtocolVersion,
            SessionNonce = nonce.ToArray(),
            InitiatorDeviceId = initiatorDeviceId,
            InitiatorPublicKey = initiatorPublicKey,
            ResponderDeviceId = responderDeviceId,
            InitiatorEphemeralPublicKey = initiatorEphemeralPublicKey.ToArray(),
            ExpiresAtUtc = expiresAtUtc,
        }, SerializerOptions);

    private static byte[] SerializeUnsignedResponse(
        ImmutableArray<byte> nonce,
        DeviceId initiatorDeviceId,
        DeviceId responderDeviceId,
        ImmutableArray<byte> responderPublicKey,
        ReadOnlySpan<byte> responderEphemeralPublicKey,
        DateTimeOffset expiresAtUtc) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion,
            ProtocolVersion,
            SessionNonce = nonce,
            InitiatorDeviceId = initiatorDeviceId,
            ResponderDeviceId = responderDeviceId,
            ResponderPublicKey = responderPublicKey,
            ResponderEphemeralPublicKey = responderEphemeralPublicKey.ToArray(),
            ExpiresAtUtc = expiresAtUtc,
        }, SerializerOptions);

    private static byte[] Combine(params ReadOnlyMemory<byte>[] values)
    {
        int length = values.Sum(static value => value.Length);
        byte[] result = new byte[length];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> value in values)
        {
            value.Span.CopyTo(result.AsSpan(offset));
            offset += value.Length;
        }

        return result;
    }

    private static void ZeroIfPresent(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new DeviceIdJsonConverter());
        return options;
    }

    private sealed class DeviceIdJsonConverter : JsonConverter<DeviceId>
    {
        public override DeviceId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? value = reader.GetString();
            return DeviceId.Parse(value ?? string.Empty);
        }

        public override void Write(Utf8JsonWriter writer, DeviceId value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
