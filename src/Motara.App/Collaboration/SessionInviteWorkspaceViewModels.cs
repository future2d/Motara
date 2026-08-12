using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Invites;

namespace Motara.App.Collaboration;

internal sealed class SessionInviteEntryViewModel : INotifyPropertyChanged
{
    private readonly CollaborationWorkspaceViewModel workspace;
    private readonly Action close;
    private readonly Action<InvitationCandidate> continueToAcceptance;
    private readonly ILogger<SessionInviteEntryViewModel> logger;
    private string invitationText = string.Empty;
    private string? validationResourceKey;

    internal SessionInviteEntryViewModel(
        CollaborationWorkspaceViewModel workspace,
        Action close,
        Action<InvitationCandidate> continueToAcceptance,
        ILogger<SessionInviteEntryViewModel>? logger = null)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.close = close ?? throw new ArgumentNullException(nameof(close));
        this.continueToAcceptance = continueToAcceptance
            ?? throw new ArgumentNullException(nameof(continueToAcceptance));
        this.logger = logger ?? NullLogger<SessionInviteEntryViewModel>.Instance;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal string InvitationText
    {
        get => invitationText;
        set => Set(ref invitationText, value);
    }

    internal string? ValidationResourceKey
    {
        get => validationResourceKey;
        private set => Set(ref validationResourceKey, value);
    }

    internal bool ValidateAndContinue()
    {
        ValidationResourceKey = null;
        if (!InvitationLinkParser.TryParse(InvitationText, out InvitationCandidate candidate))
        {
            ValidationResourceKey = "Workspace.Collaboration.InvalidInvite";
            return false;
        }

        if (candidate.Kind != InvitationKind.Session)
        {
            ValidationResourceKey = "Workspace.Collaboration.Session.InviteRequired";
            return false;
        }

        SessionInviteValidationResult validation = workspace.ValidateSessionCandidate(candidate);
        if (!validation.IsValid)
        {
            ValidationResourceKey = $"Workspace.Collaboration.InviteError.{validation.ErrorCode}";
            return false;
        }

        SessionInviteWorkspaceEvents.EntryValidated(logger);
        continueToAcceptance(candidate);
        return true;
    }

    internal void Close() => close();

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal sealed class SessionInviteGenerationViewModel : INotifyPropertyChanged
{
    private readonly CollaborationWorkspaceViewModel workspace;
    private readonly Action close;
    private readonly ILogger<SessionInviteGenerationViewModel> logger;
    private SessionJoinPolicy joinPolicy = SessionJoinPolicy.LinkWithHostApproval;
    private string invitationLink = string.Empty;
    private bool isBusy;
    private bool isGenerationConfirmationVisible;

    internal SessionInviteGenerationViewModel(
        CollaborationWorkspaceViewModel workspace,
        Action close,
        ILogger<SessionInviteGenerationViewModel>? logger = null)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.close = close ?? throw new ArgumentNullException(nameof(close));
        this.logger = logger ?? NullLogger<SessionInviteGenerationViewModel>.Instance;
        string? token = workspace.GetReusableGeneratedSessionInviteToken();
        invitationLink = token is null ? string.Empty : $"https://www.motara.org/invite/session/{token}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal SessionJoinPolicy JoinPolicy
    {
        get => joinPolicy;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            Set(ref joinPolicy, value);
        }
    }

    internal string InvitationLink
    {
        get => invitationLink;
        private set => Set(ref invitationLink, value);
    }

    internal bool IsBusy
    {
        get => isBusy;
        private set => Set(ref isBusy, value);
    }

    internal bool IsGenerationConfirmationVisible
    {
        get => isGenerationConfirmationVisible;
        private set => Set(ref isGenerationConfirmationVisible, value);
    }

    internal bool CanGenerate => !IsBusy
        && string.IsNullOrWhiteSpace(InvitationLink)
        && workspace.SessionSnapshot.Phase
        == Motara.Collaboration.Sessions.CollaborationSessionPhase.Idle;

    internal void RequestGeneration()
    {
        if (!CanGenerate)
        {
            return;
        }

        IsGenerationConfirmationVisible = true;
        SessionInviteWorkspaceEvents.GenerationConfirmationRequested(logger);
    }

    internal void CancelGeneration()
    {
        if (!IsGenerationConfirmationVisible)
        {
            return;
        }

        IsGenerationConfirmationVisible = false;
        SessionInviteWorkspaceEvents.GenerationConfirmationCancelled(logger);
    }

    internal async Task ConfirmGenerationAsync(CancellationToken cancellationToken)
    {
        if (!IsGenerationConfirmationVisible || !CanGenerate)
        {
            SessionInviteWorkspaceEvents.GenerationSkipped(
                logger,
                workspace.SessionSnapshot.Phase.ToString());
            return;
        }

        IsBusy = true;
        try
        {
            CollaborationSessionId sessionId = CollaborationSessionId.New();
            workspace.PrepareHostSession(sessionId, JoinPolicy);
            string token = await workspace.GenerateSessionInviteAsync(
                sessionId,
                JoinPolicy,
                TimeSpan.FromMinutes(10),
                cancellationToken).ConfigureAwait(false);
            workspace.RememberGeneratedSessionInvite(token, sessionId);
            InvitationLink = $"https://www.motara.org/invite/session/{token}";
            workspace.ConfirmModelDistributionConsent();
            SessionInviteWorkspaceEvents.Generated(logger, JoinPolicy);
        }
        finally
        {
            IsBusy = false;
            IsGenerationConfirmationVisible = false;
            Notify(nameof(CanGenerate));
        }
    }

    internal void Close() => close();

    internal void RecordCopyResult(bool succeeded, string? errorType) =>
        SessionInviteWorkspaceEvents.LinkCopyCompleted(
            logger,
            succeeded,
            errorType ?? "none");

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void Notify(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed class SessionInviteAcceptanceViewModel
{
    private readonly CollaborationWorkspaceViewModel workspace;
    private readonly InvitationCandidate candidate;
    private readonly Action close;
    private readonly ILogger<SessionInviteAcceptanceViewModel> logger;

    internal SessionInviteAcceptanceViewModel(
        CollaborationWorkspaceViewModel workspace,
        InvitationCandidate candidate,
        Action close,
        ILogger<SessionInviteAcceptanceViewModel>? logger = null)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        if (candidate.Kind != InvitationKind.Session)
        {
            throw new ArgumentException("The candidate is not a session invitation.", nameof(candidate));
        }

        this.candidate = candidate;
        this.close = close ?? throw new ArgumentNullException(nameof(close));
        this.logger = logger ?? NullLogger<SessionInviteAcceptanceViewModel>.Instance;
    }

    internal SessionInvite? Invite { get; private set; }

    internal string? ValidationResourceKey { get; private set; }

    internal bool CanJoin => Invite is not null;

    internal bool IsAwaitingDistributionConsent => workspace.SessionSnapshot.Phase
        == Motara.Collaboration.Sessions.CollaborationSessionPhase.AwaitingParticipantConsent;

    internal bool IsActive => workspace.SessionSnapshot.Phase
        == Motara.Collaboration.Sessions.CollaborationSessionPhase.Active;

    internal bool Initialize()
    {
        SessionInviteValidationResult result = workspace.ValidateSessionCandidate(candidate);
        if (!result.IsValid)
        {
            ValidationResourceKey = $"Workspace.Collaboration.InviteError.{result.ErrorCode}";
            SessionInviteWorkspaceEvents.Reviewed(logger, result.ErrorCode.ToString());
            return false;
        }

        Invite = result.Invite;
        SessionInviteWorkspaceEvents.Reviewed(logger, "valid");
        return true;
    }

    internal void Acknowledge() => close();

    internal void PrepareJoin()
    {
        SessionInvite invite = Invite
            ?? throw new InvalidOperationException("A valid session invitation is required before joining.");
        workspace.PrepareJoinSession(invite);
        SessionInviteWorkspaceEvents.JoinPrepared(logger);
    }

    internal void ConfirmModelDistributionConsent()
    {
        workspace.ConfirmModelDistributionConsent();
        SessionInviteWorkspaceEvents.JoinConfirmed(logger);
    }

    internal void DeclineModelDistributionConsent()
    {
        workspace.DeclineModelDistributionConsent();
        close();
    }
}

internal static partial class SessionInviteWorkspaceEvents
{
    [LoggerMessage(8068, LogLevel.Information, "Session invitation entry validated")]
    internal static partial void EntryValidated(ILogger logger);

    [LoggerMessage(8062, LogLevel.Information, "Session invitation workspace generated a link with {JoinPolicy}")]
    internal static partial void Generated(ILogger logger, SessionJoinPolicy joinPolicy);

    [LoggerMessage(8067, LogLevel.Information, "Session invitation generation was skipped because the session phase is {SessionPhase}")]
    internal static partial void GenerationSkipped(ILogger logger, string sessionPhase);

    [LoggerMessage(8069, LogLevel.Information, "Session invitation generation confirmation requested")]
    internal static partial void GenerationConfirmationRequested(ILogger logger);

    [LoggerMessage(8070, LogLevel.Information, "Session invitation generation confirmation cancelled")]
    internal static partial void GenerationConfirmationCancelled(ILogger logger);

    [LoggerMessage(8063, LogLevel.Information, "Session invitation workspace reviewed an invitation; result={Result}")]
    internal static partial void Reviewed(ILogger logger, string result);

    [LoggerMessage(8064, LogLevel.Information, "Session invitation link copy completed; succeeded={Succeeded}; error={ErrorType}")]
    internal static partial void LinkCopyCompleted(ILogger logger, bool succeeded, string errorType);

    [LoggerMessage(8065, LogLevel.Information, "Session invitation join prepared pending model-distribution consent")]
    internal static partial void JoinPrepared(ILogger logger);

    [LoggerMessage(8066, LogLevel.Information, "Session invitation join confirmed after model-distribution consent")]
    internal static partial void JoinConfirmed(ILogger logger);
}
