using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Identity;
using Motara.Collaboration.Profile;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Motara.Collaboration.Invites;

public sealed class FriendInviteTokenService
{
    private const int SchemaVersion = 1;
    private const int ProtocolVersion = 1;
    private const int MaximumTokenLength = 8192;
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider timeProvider;
    private readonly ILogger<FriendInviteTokenService> logger;

    public FriendInviteTokenService(
        TimeProvider timeProvider,
        ILogger<FriendInviteTokenService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.timeProvider = timeProvider;
        this.logger = logger ?? NullLogger<FriendInviteTokenService>.Instance;
    }

    public string Create(
        DeviceIdentityHandle identity,
        string inviterDisplayName,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string normalizedDisplayName = LocalCollaborationProfile.NormalizeDisplayName(
            inviterDisplayName);
        if (lifetime <= TimeSpan.Zero || lifetime > MaximumLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        DateTimeOffset createdAtUtc = timeProvider.GetUtcNow();
        var header = new TokenHeader(SchemaVersion, "friend-invite", "Ed25519");
        var body = new TokenBody(
            SchemaVersion,
            ProtocolVersion,
            identity.Identity.DeviceId.Value,
            Convert.ToBase64String(identity.Identity.PublicKey.ToArray()),
            normalizedDisplayName,
            Base64Url.Encode(RandomNumberGenerator.GetBytes(16)),
            createdAtUtc,
            createdAtUtc.Add(lifetime));
        string encodedHeader = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(header, SerializerOptions));
        string encodedBody = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(body, SerializerOptions));
        byte[] signedData = Encoding.ASCII.GetBytes(encodedHeader + "." + encodedBody);
        string signature = Base64Url.Encode(identity.Sign(signedData));
        InviteEvents.Created(logger);
        return encodedHeader + "." + encodedBody + "." + signature;
    }

    public InviteValidationResult Validate(string? token, DateTimeOffset? nowUtc = null)
    {
        DateTimeOffset validationTime = nowUtc ?? timeProvider.GetUtcNow();
        if (token is null || token.Length == 0)
        {
            return Invalid(InviteErrorCode.Malformed);
        }

        if (token.Length > MaximumTokenLength)
        {
            return Invalid(InviteErrorCode.TooLarge);
        }

        try
        {
            string[] segments = token.Split('.');
            if (segments.Length != 3
                || !Base64Url.TryDecode(segments[0], 256, out byte[] headerBytes)
                || !Base64Url.TryDecode(segments[1], 4096, out byte[] bodyBytes)
                || !Base64Url.TryDecode(segments[2], 64, out byte[] signature)
                || signature.Length != 64)
            {
                return Invalid(InviteErrorCode.Malformed);
            }

            TokenHeader? header = JsonSerializer.Deserialize<TokenHeader>(headerBytes, SerializerOptions);
            TokenBody? body = JsonSerializer.Deserialize<TokenBody>(bodyBytes, SerializerOptions);
            if (header is null || body is null
                || header.SchemaVersion != SchemaVersion
                || body.SchemaVersion != SchemaVersion
                || body.ProtocolVersion != ProtocolVersion
                || header.Type != "friend-invite"
                || header.Algorithm != "Ed25519")
            {
                return Invalid(InviteErrorCode.Unsupported);
            }

            byte[] publicKey = Convert.FromBase64String(body.InviterPublicKey);
            if (publicKey.Length != 32
                || !Base64Url.TryDecode(body.InviteNonce, 16, out byte[] nonce)
                || nonce.Length != 16)
            {
                return Invalid(InviteErrorCode.Malformed);
            }

            DeviceId deviceId = DeviceId.Parse(body.InviterDeviceId);
            string normalizedDisplayName = LocalCollaborationProfile.NormalizeDisplayName(
                body.InviterDisplayName);
            if (DeviceId.FromEd25519PublicKey(publicKey) != deviceId
                || !StringComparer.Ordinal.Equals(body.InviterDisplayName, normalizedDisplayName))
            {
                return Invalid(InviteErrorCode.InconsistentIdentity);
            }

            byte[] signedData = Encoding.ASCII.GetBytes(segments[0] + "." + segments[1]);
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey));
            verifier.BlockUpdate(signedData, 0, signedData.Length);
            if (!verifier.VerifySignature(signature))
            {
                return Invalid(InviteErrorCode.InvalidSignature);
            }

            if (body.CreatedAtUtc > validationTime.Add(ClockSkew))
            {
                return Invalid(InviteErrorCode.NotYetValid);
            }

            if (body.ExpiresAtUtc <= validationTime
                || body.ExpiresAtUtc <= body.CreatedAtUtc
                || body.ExpiresAtUtc - body.CreatedAtUtc > MaximumLifetime)
            {
                return Invalid(InviteErrorCode.Expired);
            }

            InviteValidationResult result = InviteValidationResult.Valid(new FriendInvite(
                body.SchemaVersion,
                body.ProtocolVersion,
                deviceId,
                System.Collections.Immutable.ImmutableArray.CreateRange(publicKey),
                normalizedDisplayName,
                body.InviteNonce,
                body.CreatedAtUtc,
                body.ExpiresAtUtc));
            InviteEvents.Validated(logger);
            return result;
        }
        catch (Exception exception) when (exception is JsonException
            or FormatException
            or ArgumentException
            or CryptographicException)
        {
            return Invalid(InviteErrorCode.Malformed);
        }
    }

    private InviteValidationResult Invalid(InviteErrorCode errorCode)
    {
        InviteEvents.Rejected(logger, errorCode);
        return InviteValidationResult.Invalid(errorCode);
    }

    private sealed record TokenHeader(int SchemaVersion, string Type, string Algorithm);

    private sealed record TokenBody(
        int SchemaVersion,
        int ProtocolVersion,
        string InviterDeviceId,
        string InviterPublicKey,
        string InviterDisplayName,
        string InviteNonce,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset ExpiresAtUtc);
}
