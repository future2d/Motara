using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Identity;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Motara.Collaboration.Invites;

public sealed class SessionInviteTokenService
{
    private const int SchemaVersion = 1;
    private const int ProtocolVersion = 1;
    private const int MaximumTokenLength = 8192;
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SessionInviteTokenService> logger;

    public SessionInviteTokenService(
        TimeProvider timeProvider,
        ILogger<SessionInviteTokenService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.timeProvider = timeProvider;
        this.logger = logger ?? NullLogger<SessionInviteTokenService>.Instance;
    }

    public string Create(
        DeviceIdentityHandle host,
        CollaborationSessionId sessionId,
        SessionJoinPolicy joinPolicy,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (sessionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Collaboration session ID cannot be empty.", nameof(sessionId));
        }

        if (!Enum.IsDefined(joinPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(joinPolicy));
        }

        if (lifetime <= TimeSpan.Zero || lifetime > MaximumLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        DateTimeOffset createdAtUtc = timeProvider.GetUtcNow();
        var header = new TokenHeader(SchemaVersion, "session-invite", "Ed25519");
        var body = new TokenBody(
            SchemaVersion,
            ProtocolVersion,
            sessionId.Value.ToString("N"),
            host.Identity.DeviceId.Value,
            Convert.ToBase64String(host.Identity.PublicKey.ToArray()),
            joinPolicy,
            Base64Url.Encode(RandomNumberGenerator.GetBytes(16)),
            createdAtUtc,
            createdAtUtc.Add(lifetime));
        string encodedHeader = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(header, SerializerOptions));
        string encodedBody = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(body, SerializerOptions));
        byte[] signedData = Encoding.ASCII.GetBytes(encodedHeader + "." + encodedBody);
        string signature = Base64Url.Encode(host.Sign(signedData));
        InviteEvents.SessionCreated(logger, joinPolicy);
        return encodedHeader + "." + encodedBody + "." + signature;
    }

    public SessionInviteValidationResult Validate(string? token, DateTimeOffset? nowUtc = null)
    {
        DateTimeOffset validationTime = nowUtc ?? timeProvider.GetUtcNow();
        if (string.IsNullOrEmpty(token))
        {
            return Invalid(SessionInviteErrorCode.Malformed);
        }

        if (token.Length > MaximumTokenLength)
        {
            return Invalid(SessionInviteErrorCode.TooLarge);
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
                return Invalid(SessionInviteErrorCode.Malformed);
            }

            TokenHeader? header = JsonSerializer.Deserialize<TokenHeader>(headerBytes, SerializerOptions);
            TokenBody? body = JsonSerializer.Deserialize<TokenBody>(bodyBytes, SerializerOptions);
            if (header is null || body is null
                || header.SchemaVersion != SchemaVersion
                || body.SchemaVersion != SchemaVersion
                || body.ProtocolVersion != ProtocolVersion
                || header.Type != "session-invite"
                || header.Algorithm != "Ed25519")
            {
                return Invalid(SessionInviteErrorCode.Unsupported);
            }

            byte[] publicKey = Convert.FromBase64String(body.HostPublicKey);
            if (publicKey.Length != 32
                || !Guid.TryParseExact(body.SessionId, "N", out Guid sessionGuid)
                || sessionGuid == Guid.Empty
                || !Enum.IsDefined(body.JoinPolicy)
                || !Base64Url.TryDecode(body.InviteNonce, 16, out byte[] nonce)
                || nonce.Length != 16)
            {
                return Invalid(SessionInviteErrorCode.Malformed);
            }

            DeviceId deviceId = DeviceId.Parse(body.HostDeviceId);
            if (DeviceId.FromEd25519PublicKey(publicKey) != deviceId)
            {
                return Invalid(SessionInviteErrorCode.InconsistentIdentity);
            }

            byte[] signedData = Encoding.ASCII.GetBytes(segments[0] + "." + segments[1]);
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey));
            verifier.BlockUpdate(signedData, 0, signedData.Length);
            if (!verifier.VerifySignature(signature))
            {
                return Invalid(SessionInviteErrorCode.InvalidSignature);
            }

            if (body.CreatedAtUtc > validationTime.Add(ClockSkew))
            {
                return Invalid(SessionInviteErrorCode.NotYetValid);
            }

            if (body.ExpiresAtUtc <= validationTime
                || body.ExpiresAtUtc <= body.CreatedAtUtc
                || body.ExpiresAtUtc - body.CreatedAtUtc > MaximumLifetime)
            {
                return Invalid(SessionInviteErrorCode.Expired);
            }

            var invite = new SessionInvite(
                body.SchemaVersion,
                body.ProtocolVersion,
                new CollaborationSessionId(sessionGuid),
                deviceId,
                ImmutableArray.CreateRange(publicKey),
                body.JoinPolicy,
                body.InviteNonce,
                body.CreatedAtUtc,
                body.ExpiresAtUtc);
            InviteEvents.SessionValidated(logger, body.JoinPolicy);
            return SessionInviteValidationResult.Valid(invite);
        }
        catch (Exception exception) when (exception is JsonException
            or FormatException
            or ArgumentException
            or CryptographicException)
        {
            return Invalid(SessionInviteErrorCode.Malformed);
        }
    }

    private SessionInviteValidationResult Invalid(SessionInviteErrorCode errorCode)
    {
        InviteEvents.SessionRejected(logger, errorCode);
        return SessionInviteValidationResult.Invalid(errorCode);
    }

    private sealed record TokenHeader(int SchemaVersion, string Type, string Algorithm);

    private sealed record TokenBody(
        int SchemaVersion,
        int ProtocolVersion,
        string SessionId,
        string HostDeviceId,
        string HostPublicKey,
        SessionJoinPolicy JoinPolicy,
        string InviteNonce,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset ExpiresAtUtc);
}
