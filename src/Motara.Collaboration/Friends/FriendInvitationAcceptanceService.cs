using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Identity;
using Motara.Collaboration.Invites;

namespace Motara.Collaboration.Friends;

public sealed class FriendInvitationAcceptanceService
{
    private readonly FriendInviteTokenService tokenService;
    private readonly FriendStore friendStore;
    private readonly ConsumedInviteStore consumedInviteStore;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<FriendInvitationAcceptanceService> logger;

    public FriendInvitationAcceptanceService(
        FriendInviteTokenService tokenService,
        FriendStore friendStore,
        ConsumedInviteStore consumedInviteStore,
        TimeProvider timeProvider,
        ILogger<FriendInvitationAcceptanceService>? logger = null)
    {
        this.tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        this.friendStore = friendStore ?? throw new ArgumentNullException(nameof(friendStore));
        this.consumedInviteStore = consumedInviteStore
            ?? throw new ArgumentNullException(nameof(consumedInviteStore));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? NullLogger<FriendInvitationAcceptanceService>.Instance;
    }

    public async Task<FriendAcceptanceResult> AcceptAsync(
        string token,
        DeviceIdentity localIdentity,
        string localDisplayName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localIdentity);
        long started = Stopwatch.GetTimestamp();
        FriendAcceptanceResult result;
        if (string.IsNullOrWhiteSpace(localDisplayName) || localDisplayName.Length > 80)
        {
            result = new FriendAcceptanceResult(FriendAcceptanceResultCode.InvalidInvite);
            return Complete(result, started);
        }

        InviteValidationResult validation = tokenService.Validate(token, timeProvider.GetUtcNow());
        if (!validation.IsValid)
        {
            result = new FriendAcceptanceResult(FriendAcceptanceResultCode.InvalidInvite);
            return Complete(result, started);
        }

        FriendInvite invite = validation.Invite!;
        if (invite.InviterDeviceId == localIdentity.DeviceId)
        {
            result = new FriendAcceptanceResult(FriendAcceptanceResultCode.SelfInvite);
            return Complete(result, started);
        }

        if (await consumedInviteStore.IsConsumedAsync(invite.InviteNonce, cancellationToken)
            .ConfigureAwait(false))
        {
            result = new FriendAcceptanceResult(
                FriendAcceptanceResultCode.AlreadyProcessed, invite.InviterDeviceId, invite.InviteNonce);
            return Complete(result, started);
        }

        FriendRecord? existing = await friendStore.GetAsync(invite.InviterDeviceId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            FriendAcceptanceResultCode code = existing.FriendPublicKey.AsSpan()
                .SequenceEqual(invite.InviterPublicKey.AsSpan())
                ? existing.TrustState == FriendTrustState.Blocked
                    ? FriendAcceptanceResultCode.Blocked
                    : FriendAcceptanceResultCode.AlreadyExists
                : FriendAcceptanceResultCode.IdentityConflict;
            result = new FriendAcceptanceResult(code, invite.InviterDeviceId, invite.InviteNonce);
            return Complete(result, started);
        }

        try
        {
            if (!await consumedInviteStore.TryConsumeAsync(
                invite.InviteNonce, invite.ExpiresAtUtc, cancellationToken).ConfigureAwait(false))
            {
                result = new FriendAcceptanceResult(
                    FriendAcceptanceResultCode.AlreadyProcessed, invite.InviterDeviceId, invite.InviteNonce);
                return Complete(result, started);
            }

            FriendRecord pending = FriendRecord.Pending(
                invite.InviterPublicKey.ToArray(), localDisplayName, timeProvider.GetUtcNow());
            await friendStore.SaveAsync(pending, cancellationToken).ConfigureAwait(false);
            result = new FriendAcceptanceResult(
                FriendAcceptanceResultCode.AcceptedPending, invite.InviterDeviceId, invite.InviteNonce);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FriendStoreException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            result = new FriendAcceptanceResult(
                FriendAcceptanceResultCode.SaveFailed, invite.InviterDeviceId, invite.InviteNonce);
        }

        return Complete(result, started);
    }

    private FriendAcceptanceResult Complete(FriendAcceptanceResult result, long started)
    {
        FriendAcceptanceEvents.Completed(
            logger,
            result.Code,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }
}
