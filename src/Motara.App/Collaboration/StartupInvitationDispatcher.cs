using Motara.Collaboration.Invites;

namespace Motara.App.Collaboration;

internal enum StartupInvitationStatus
{
    None,
    Valid,
    Invalid,
}

internal readonly record struct StartupInvitationResult(
    StartupInvitationStatus Status,
    InvitationCandidate? Candidate);

internal static class StartupInvitationDispatcher
{
    internal static StartupInvitationResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
        {
            return new StartupInvitationResult(StartupInvitationStatus.None, null);
        }

        if (args.Count != 1
            || string.IsNullOrWhiteSpace(args[0])
            || args[0][0] == '-'
            || !InvitationLinkParser.TryParse(args[0], out InvitationCandidate candidate))
        {
            return new StartupInvitationResult(StartupInvitationStatus.Invalid, null);
        }

        return new StartupInvitationResult(StartupInvitationStatus.Valid, candidate);
    }
}
