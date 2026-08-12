using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Friends;
using Motara.Collaboration.Invites;

namespace Motara.App.Collaboration;

internal sealed class FriendInviteGenerationViewModel : INotifyPropertyChanged
{
    private readonly CollaborationWorkspaceViewModel workspace;
    private readonly Action close;
    private readonly ILogger<FriendInviteGenerationViewModel> logger;
    private string invitationLink = string.Empty;

    internal FriendInviteGenerationViewModel(
        CollaborationWorkspaceViewModel workspace,
        Action close,
        ILogger<FriendInviteGenerationViewModel>? logger = null)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.close = close ?? throw new ArgumentNullException(nameof(close));
        this.logger = logger ?? NullLogger<FriendInviteGenerationViewModel>.Instance;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal string InvitationLink
    {
        get => invitationLink;
        private set
        {
            if (StringComparer.Ordinal.Equals(invitationLink, value))
            {
                return;
            }

            invitationLink = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InvitationLink)));
        }
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await workspace.InitializeAsync(cancellationToken).ConfigureAwait(false);
        string token = await workspace.GenerateInviteAsync(
            TimeSpan.FromHours(24), cancellationToken).ConfigureAwait(false);
        InvitationLink = $"https://www.motara.org/invite/friend/{token}";
        FriendInviteWorkspaceEvents.Generated(logger);
    }

    internal void Close() => close();
}

internal sealed class FriendInviteAcceptanceViewModel : INotifyPropertyChanged
{
    private readonly CollaborationWorkspaceViewModel workspace;
    private readonly Action close;
    private readonly ILogger<FriendInviteAcceptanceViewModel> logger;
    private string invitationText = string.Empty;
    private string localDisplayName = string.Empty;
    private string? automaticDisplayName;
    private InvitationCandidate? candidate;
    private FriendAcceptanceResult? result;
    private string? validationResourceKey;

    internal FriendInviteAcceptanceViewModel(
        CollaborationWorkspaceViewModel workspace,
        Action close,
        ILogger<FriendInviteAcceptanceViewModel>? logger = null)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.close = close ?? throw new ArgumentNullException(nameof(close));
        this.logger = logger ?? NullLogger<FriendInviteAcceptanceViewModel>.Instance;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal string InvitationText
    {
        get => invitationText;
        set
        {
            if (StringComparer.Ordinal.Equals(invitationText, value))
            {
                return;
            }

            invitationText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InvitationText)));
            PreviewCandidate();
        }
    }

    internal string LocalDisplayName
    {
        get => localDisplayName;
        set => Set(ref localDisplayName, value);
    }

    internal InvitationCandidate? Candidate => candidate;

    internal FriendAcceptanceResult? Result
    {
        get => result;
        private set => Set(ref result, value);
    }

    internal string? ValidationResourceKey
    {
        get => validationResourceKey;
        private set => Set(ref validationResourceKey, value);
    }

    internal bool ValidateCandidate()
    {
        ValidationResourceKey = null;
        if (!InvitationLinkParser.TryParse(InvitationText, out InvitationCandidate parsed))
        {
            candidate = null;
            ValidationResourceKey = "Workspace.Collaboration.InvalidInvite";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Candidate)));
            return false;
        }

        if (parsed.Kind != InvitationKind.Friend)
        {
            candidate = null;
            ValidationResourceKey = "Workspace.Collaboration.Friend.InviteRequired";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Candidate)));
            return false;
        }

        InviteValidationResult validation = workspace.ValidateCandidate(parsed);
        if (!validation.IsValid)
        {
            candidate = null;
            ValidationResourceKey = $"Workspace.Collaboration.InviteError.{validation.ErrorCode}";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Candidate)));
            return false;
        }

        candidate = parsed;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Candidate)));
        return true;
    }

    private void PreviewCandidate()
    {
        if (!InvitationLinkParser.TryParse(InvitationText, out InvitationCandidate parsed)
            || parsed.Kind != InvitationKind.Friend)
        {
            return;
        }

        InviteValidationResult validation = workspace.ValidateCandidate(parsed);
        if (!validation.IsValid || validation.Invite is null)
        {
            return;
        }

        candidate = parsed;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Candidate)));
        if (string.IsNullOrWhiteSpace(LocalDisplayName)
            || StringComparer.Ordinal.Equals(LocalDisplayName, automaticDisplayName))
        {
            automaticDisplayName = validation.Invite.InviterDisplayName;
            LocalDisplayName = automaticDisplayName;
        }
    }

    internal async Task<FriendAcceptanceResult> AcceptAsync(CancellationToken cancellationToken)
    {
        if (candidate is not { } acceptedCandidate && !ValidateCandidate())
        {
            throw new InvalidOperationException("The invitation is not valid.");
        }

        if (candidate!.Value.Kind != InvitationKind.Friend)
        {
            throw new InvalidOperationException("A session invitation cannot be accepted as a friend invitation.");
        }

        Result = await workspace.AcceptAsync(
            candidate!.Value,
            LocalDisplayName,
            cancellationToken).ConfigureAwait(false);
        FriendInviteWorkspaceEvents.Accepted(logger, Result.Code);
        return Result;
    }

    internal void Close() => close();

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal static partial class FriendInviteWorkspaceEvents
{
    [LoggerMessage(8060, LogLevel.Information, "Friend invitation generation workspace created a link")]
    internal static partial void Generated(ILogger logger);

    [LoggerMessage(8061, LogLevel.Information,
        "Friend invitation acceptance workspace completed; result={ResultCode}")]
    internal static partial void Accepted(ILogger logger, FriendAcceptanceResultCode resultCode);
}
