using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Identity;
using Motara.Collaboration.Invites;
using Motara.Collaboration.Models;

namespace Motara.Collaboration.Sessions;

/// <summary>
/// Owns local collaboration-session intent. It does not start a network host
/// or move model bytes; those adapters react to its immutable snapshots.
/// </summary>
public sealed class CollaborationSessionCoordinator
{
    private readonly object gate = new();
    private readonly DeviceId localDeviceId;
    private readonly CollaborationMemberRegistry members;
    private readonly ILogger<CollaborationSessionCoordinator> logger;
    private CollaborationSessionSnapshot snapshot = CollaborationSessionSnapshot.Idle;

    public CollaborationSessionCoordinator(
        DeviceId localDeviceId,
        int maximumMembers = 4,
        ILogger<CollaborationSessionCoordinator>? logger = null)
    {
        this.localDeviceId = localDeviceId;
        this.logger = logger ?? NullLogger<CollaborationSessionCoordinator>.Instance;
        members = new CollaborationMemberRegistry(maximumMembers);
    }

    public event EventHandler<CollaborationSessionSnapshot>? SnapshotChanged;

    public CollaborationSessionSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return snapshot;
            }
        }
    }

    public void PrepareHost(CollaborationSessionId sessionId, SessionJoinPolicy joinPolicy)
    {
        if (!Enum.IsDefined(joinPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(joinPolicy));
        }

        CollaborationSessionSnapshot next;
        lock (gate)
        {
            EnsureIdle();
            if (!members.TryAdd(localDeviceId))
            {
                throw new InvalidOperationException("The local member cannot be admitted to the session.");
            }

            next = PublishLocked(new CollaborationSessionSnapshot(
                CollaborationSessionPhase.AwaitingHostConsent,
                CollaborationSessionRole.Host,
                sessionId,
                localDeviceId,
                joinPolicy,
                null,
                null,
                members.Count));
        }

        Notify(next);
        CollaborationSessionEvents.HostPrepared(logger);
    }

    public void PrepareJoin(SessionInvite invite)
    {
        ArgumentNullException.ThrowIfNull(invite);
        CollaborationSessionSnapshot next;
        lock (gate)
        {
            EnsureIdle();
            if (!members.TryAdd(localDeviceId))
            {
                throw new InvalidOperationException("The local member cannot be admitted to the session.");
            }

            next = PublishLocked(new CollaborationSessionSnapshot(
                CollaborationSessionPhase.AwaitingParticipantConsent,
                CollaborationSessionRole.Participant,
                invite.SessionId,
                invite.HostDeviceId,
                invite.JoinPolicy,
                null,
                null,
                members.Count));
        }

        Notify(next);
        CollaborationSessionEvents.ParticipantPrepared(logger);
    }

    public void ConfirmModelDistributionConsent()
    {
        CollaborationSessionSnapshot next;
        lock (gate)
        {
            if (snapshot.Phase is not (CollaborationSessionPhase.AwaitingHostConsent
                or CollaborationSessionPhase.AwaitingParticipantConsent))
            {
                throw new InvalidOperationException("The session is not awaiting model-distribution consent.");
            }

            next = PublishLocked(snapshot with { Phase = CollaborationSessionPhase.Active });
        }

        Notify(next);
        CollaborationSessionEvents.ConsentConfirmed(logger);
    }

    public void DeclineModelDistributionConsent() => Leave();

    public void SetLocalModel(ModelInstanceId? modelInstanceId)
    {
        CollaborationSessionSnapshot? next = null;
        lock (gate)
        {
            if (snapshot.Phase != CollaborationSessionPhase.Active)
            {
                throw new InvalidOperationException("The session must be active before publishing a model.");
            }

            if (snapshot.LocalModelInstanceId == modelInstanceId)
            {
                return;
            }

            ModelGeneration nextGeneration = snapshot.ModelGeneration?.Next() ?? new ModelGeneration(1);
            next = PublishLocked(snapshot with
            {
                LocalModelInstanceId = modelInstanceId,
                ModelGeneration = nextGeneration,
            });
        }

        if (next is not null)
        {
            Notify(next);
        }
        CollaborationSessionEvents.LocalModelChanged(logger, modelInstanceId.HasValue);
    }

    public void Leave()
    {
        CollaborationSessionSnapshot? next = null;
        lock (gate)
        {
            if (snapshot.Phase == CollaborationSessionPhase.Idle)
            {
                return;
            }

            members.Remove(localDeviceId);
            next = PublishLocked(CollaborationSessionSnapshot.Idle);
        }

        if (next is not null)
        {
            Notify(next);
            CollaborationSessionEvents.Left(logger);
        }
    }

    private void EnsureIdle()
    {
        if (snapshot.Phase != CollaborationSessionPhase.Idle)
        {
            throw new InvalidOperationException("The current collaboration session must be ended first.");
        }
    }

    private CollaborationSessionSnapshot PublishLocked(CollaborationSessionSnapshot next)
    {
        snapshot = next;
        return next;
    }

    private void Notify(CollaborationSessionSnapshot value) =>
        SnapshotChanged?.Invoke(this, value);
}
