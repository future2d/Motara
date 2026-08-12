using Motara.Collaboration.Identity;
using Motara.Collaboration.Invites;
using Motara.Collaboration.Models;

namespace Motara.Collaboration.Sessions;

public enum CollaborationSessionPhase
{
    Idle,
    AwaitingHostConsent,
    AwaitingParticipantConsent,
    Active,
}

public enum CollaborationSessionRole
{
    None,
    Host,
    Participant,
}

public sealed record CollaborationSessionSnapshot(
    CollaborationSessionPhase Phase,
    CollaborationSessionRole Role,
    CollaborationSessionId? SessionId,
    DeviceId? HostDeviceId,
    SessionJoinPolicy? JoinPolicy,
    ModelInstanceId? LocalModelInstanceId,
    ModelGeneration? ModelGeneration,
    int MemberCount)
{
    public static CollaborationSessionSnapshot Idle { get; } = new(
        CollaborationSessionPhase.Idle,
        CollaborationSessionRole.None,
        null,
        null,
        null,
        null,
        null,
        0);
}
