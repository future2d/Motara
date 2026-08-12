namespace Motara.Collaboration.Invites;

public enum InvitationKind
{
    Friend,
    Session,
}

public readonly record struct InvitationCandidate(InvitationKind Kind, string Token);
