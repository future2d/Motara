using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Identity;

namespace Motara.Collaboration.Friends;

public enum FriendRelationshipRemovalResultCode
{
    Removed,
    NotFound,
    SecretRemoveFailed,
    FriendRemoveFailed,
}

public sealed record FriendRelationshipRemovalResult(FriendRelationshipRemovalResultCode Code);

public sealed class FriendRelationshipService
{
    private readonly FriendStore friendStore;
    private readonly RelationshipSecretStore relationshipSecretStore;
    private readonly ILogger<FriendRelationshipService> logger;

    public FriendRelationshipService(
        FriendStore friendStore,
        RelationshipSecretStore relationshipSecretStore,
        ILogger<FriendRelationshipService>? logger = null)
    {
        this.friendStore = friendStore ?? throw new ArgumentNullException(nameof(friendStore));
        this.relationshipSecretStore = relationshipSecretStore
            ?? throw new ArgumentNullException(nameof(relationshipSecretStore));
        this.logger = logger ?? NullLogger<FriendRelationshipService>.Instance;
    }

    public async Task<FriendRelationshipRemovalResult> RemoveAsync(
        DeviceId friendDeviceId,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        FriendRecord? friend = await friendStore.GetAsync(friendDeviceId, cancellationToken)
            .ConfigureAwait(false);
        if (friend is null)
        {
            return Complete(FriendRelationshipRemovalResultCode.NotFound, started);
        }

        try
        {
            await relationshipSecretStore.RemoveAsync(friendDeviceId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is RelationshipSecretStoreException
            or IOException
            or UnauthorizedAccessException)
        {
            return Complete(FriendRelationshipRemovalResultCode.SecretRemoveFailed, started);
        }

        try
        {
            await friendStore.RemoveAsync(friendDeviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FriendStoreException
            or IOException
            or UnauthorizedAccessException)
        {
            return Complete(FriendRelationshipRemovalResultCode.FriendRemoveFailed, started);
        }

        return Complete(FriendRelationshipRemovalResultCode.Removed, started);
    }

    private FriendRelationshipRemovalResult Complete(
        FriendRelationshipRemovalResultCode code,
        long started)
    {
        FriendRelationshipEvents.Completed(
            logger,
            code,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new FriendRelationshipRemovalResult(code);
    }
}
