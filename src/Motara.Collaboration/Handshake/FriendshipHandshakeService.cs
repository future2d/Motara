using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Friends;
using Motara.Collaboration.Identity;

namespace Motara.Collaboration.Handshake;

public sealed class FriendshipHandshakeService
{
    private readonly FriendStore friendStore;
    private readonly RelationshipSecretStore relationshipSecretStore;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<FriendshipHandshakeService> logger;

    public FriendshipHandshakeService(
        FriendStore friendStore,
        RelationshipSecretStore relationshipSecretStore,
        TimeProvider timeProvider,
        ILogger<FriendshipHandshakeService>? logger = null)
    {
        this.friendStore = friendStore ?? throw new ArgumentNullException(nameof(friendStore));
        this.relationshipSecretStore = relationshipSecretStore
            ?? throw new ArgumentNullException(nameof(relationshipSecretStore));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? NullLogger<FriendshipHandshakeService>.Instance;
    }

    public HandshakeOfferHandle CreateOffer(
        DeviceIdentityHandle localIdentity,
        DeviceId friendDeviceId)
    {
        long started = Stopwatch.GetTimestamp();
        HandshakeOfferHandle offer = FriendshipHandshakeCryptography.CreateOffer(
            localIdentity,
            friendDeviceId,
            timeProvider);
        FriendshipHandshakeEvents.OfferCreated(
            logger,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return offer;
    }

    public async Task<HandshakeAcceptResult> AcceptOfferAsync(
        DeviceIdentityHandle localIdentity,
        ReadOnlyMemory<byte> offerBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localIdentity);
        long started = Stopwatch.GetTimestamp();
        HandshakeResponseHandle? responseHandle = null;
        byte[]? relationshipSecret = null;
        try
        {
            try
            {
                responseHandle = FriendshipHandshakeCryptography.CreateResponse(
                    localIdentity,
                    offerBytes,
                    timeProvider);
            }
            catch (HandshakeProtocolException exception)
            {
                return CompleteAccept(Map(exception.Error), started);
            }

            FriendshipHandshakeResultCode? friendResult = await ValidateFriendAsync(
                responseHandle.Offer.InitiatorDeviceId,
                responseHandle.Offer.InitiatorPublicKey,
                cancellationToken).ConfigureAwait(false);
            if (friendResult.HasValue)
            {
                return CompleteAccept(friendResult.Value, started);
            }

            relationshipSecret = FriendshipHandshakeCryptography.DeriveResponderSecret(responseHandle);
            string secretReference;
            try
            {
                secretReference = await relationshipSecretStore.SaveAsync(
                    responseHandle.Offer.InitiatorDeviceId,
                    relationshipSecret,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (RelationshipSecretStoreException)
            {
                return CompleteAccept(FriendshipHandshakeResultCode.SecretSaveFailed, started);
            }

            try
            {
                await friendStore.SetTrustedAsync(
                    responseHandle.Offer.InitiatorDeviceId,
                    responseHandle.Offer.InitiatorPublicKey.AsSpan().ToArray(),
                    secretReference,
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsFriendStorageException(exception))
            {
                return CompleteAccept(FriendshipHandshakeResultCode.SaveFailed, started);
            }

            return CompleteAccept(
                FriendshipHandshakeResultCode.Completed,
                started,
                responseHandle.MessageBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsFriendStorageException(exception))
        {
            return CompleteAccept(FriendshipHandshakeResultCode.SaveFailed, started);
        }
        finally
        {
            responseHandle?.Dispose();
            if (relationshipSecret is not null)
            {
                CryptographicOperations.ZeroMemory(relationshipSecret);
            }
        }
    }

    public async Task<HandshakeCompleteResult> CompleteOfferAsync(
        DeviceIdentityHandle localIdentity,
        HandshakeOfferHandle offer,
        ReadOnlyMemory<byte> responseBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localIdentity);
        ArgumentNullException.ThrowIfNull(offer);
        long started = Stopwatch.GetTimestamp();
        byte[]? relationshipSecret = null;
        try
        {
            if (localIdentity.Identity.DeviceId != offer.Offer.InitiatorDeviceId
                || !localIdentity.Identity.PublicKey.AsSpan().SequenceEqual(
                    offer.Offer.InitiatorPublicKey.AsSpan()))
            {
                return Complete(FriendshipHandshakeResultCode.IdentityConflict, started);
            }

            HandshakeResponse response;
            try
            {
                response = FriendshipHandshakeCryptography.DeserializeResponse(responseBytes.Span);
                relationshipSecret = FriendshipHandshakeCryptography.DeriveInitiatorSecret(
                    offer,
                    response,
                    timeProvider);
            }
            catch (HandshakeProtocolException exception)
            {
                return Complete(Map(exception.Error), started);
            }

            FriendshipHandshakeResultCode? friendResult = await ValidateFriendAsync(
                response.ResponderDeviceId,
                response.ResponderPublicKey,
                cancellationToken).ConfigureAwait(false);
            if (friendResult.HasValue)
            {
                return Complete(friendResult.Value, started);
            }

            string secretReference;
            try
            {
                secretReference = await relationshipSecretStore.SaveAsync(
                    response.ResponderDeviceId,
                    relationshipSecret,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (RelationshipSecretStoreException)
            {
                return Complete(FriendshipHandshakeResultCode.SecretSaveFailed, started);
            }

            try
            {
                await friendStore.SetTrustedAsync(
                    response.ResponderDeviceId,
                    response.ResponderPublicKey.AsSpan().ToArray(),
                    secretReference,
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsFriendStorageException(exception))
            {
                return Complete(FriendshipHandshakeResultCode.SaveFailed, started);
            }

            return Complete(FriendshipHandshakeResultCode.Completed, started);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsFriendStorageException(exception))
        {
            return Complete(FriendshipHandshakeResultCode.SaveFailed, started);
        }
        finally
        {
            if (relationshipSecret is not null)
            {
                CryptographicOperations.ZeroMemory(relationshipSecret);
            }
        }
    }

    private async Task<FriendshipHandshakeResultCode?> ValidateFriendAsync(
        DeviceId friendDeviceId,
        ImmutableArray<byte> publicKey,
        CancellationToken cancellationToken)
    {
        FriendRecord? friend = await friendStore.GetAsync(friendDeviceId, cancellationToken)
            .ConfigureAwait(false);
        if (friend is null)
        {
            return FriendshipHandshakeResultCode.UnknownFriend;
        }

        if (!friend.FriendPublicKey.AsSpan().SequenceEqual(publicKey.AsSpan()))
        {
            return FriendshipHandshakeResultCode.IdentityConflict;
        }

        return friend.TrustState switch
        {
            FriendTrustState.Trusted => FriendshipHandshakeResultCode.AlreadyTrusted,
            FriendTrustState.Blocked => FriendshipHandshakeResultCode.Blocked,
            _ => null,
        };
    }

    private HandshakeAcceptResult CompleteAccept(
        FriendshipHandshakeResultCode code,
        long started,
        ImmutableArray<byte> responseBytes = default)
    {
        FriendshipHandshakeEvents.Completed(
            logger,
            "Accept",
            code,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return responseBytes.IsDefault
            ? HandshakeAcceptResult.WithoutResponse(code)
            : new HandshakeAcceptResult(code, responseBytes);
    }

    private HandshakeCompleteResult Complete(FriendshipHandshakeResultCode code, long started)
    {
        FriendshipHandshakeEvents.Completed(
            logger,
            "Complete",
            code,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new HandshakeCompleteResult(code);
    }

    private static FriendshipHandshakeResultCode Map(HandshakeProtocolError error) => error switch
    {
        HandshakeProtocolError.IdentityConflict => FriendshipHandshakeResultCode.IdentityConflict,
        HandshakeProtocolError.SignatureInvalid => FriendshipHandshakeResultCode.SignatureInvalid,
        HandshakeProtocolError.Expired => FriendshipHandshakeResultCode.Expired,
        HandshakeProtocolError.TranscriptMismatch => FriendshipHandshakeResultCode.TranscriptMismatch,
        _ => FriendshipHandshakeResultCode.InvalidMessage,
    };

    private static bool IsFriendStorageException(Exception exception) => exception is
        FriendStoreException
        or KeyNotFoundException
        or IOException
        or UnauthorizedAccessException
        or ArgumentException
        or JsonException
        or FormatException
        or InvalidDataException;
}
