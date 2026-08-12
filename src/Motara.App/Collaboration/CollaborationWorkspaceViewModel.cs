using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Friends;
using Motara.Collaboration.Handshake;
using Motara.Collaboration.Identity;
using Motara.Collaboration.Invites;
using Motara.Collaboration.Profile;
using Motara.Collaboration.Sessions;

namespace Motara.App.Collaboration;

internal sealed class CollaborationWorkspaceViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly CollaborationIdentitySession identitySession;
    private readonly FriendInviteTokenService tokenService;
    private readonly SessionInviteTokenService sessionTokenService;
    private readonly FriendInvitationAcceptanceService acceptanceService;
    private readonly FriendStore friendStore;
    private readonly FriendRelationshipService relationshipService;
    private readonly FriendshipHandshakeService handshakeService;
    private readonly TimeProvider timeProvider;
    private readonly LocalCollaborationProfileStore? profileStore;
    private readonly ILogger<CollaborationWorkspaceViewModel> logger;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private ImmutableArray<CollaborationContactItem> contacts = [];
    private bool isInitialized;
    private bool isBusy;
    private bool requiresRestartAfterIdentityImport;
    private string? generatedInviteToken;
    private GeneratedSessionInvite? generatedSessionInvite;
    private CollaborationSessionCoordinator? sessionCoordinator;
    private bool disposed;

    internal CollaborationWorkspaceViewModel(
        CollaborationIdentitySession identitySession,
        FriendInviteTokenService tokenService,
        SessionInviteTokenService sessionTokenService,
        FriendInvitationAcceptanceService acceptanceService,
        FriendStore friendStore,
        FriendRelationshipService relationshipService,
        FriendshipHandshakeService handshakeService,
        TimeProvider timeProvider,
        ILogger<CollaborationWorkspaceViewModel>? logger = null,
        LocalCollaborationProfileStore? profileStore = null)
    {
        this.identitySession = identitySession ?? throw new ArgumentNullException(nameof(identitySession));
        this.tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        this.sessionTokenService = sessionTokenService ?? throw new ArgumentNullException(nameof(sessionTokenService));
        this.acceptanceService = acceptanceService ?? throw new ArgumentNullException(nameof(acceptanceService));
        this.friendStore = friendStore ?? throw new ArgumentNullException(nameof(friendStore));
        this.relationshipService = relationshipService
            ?? throw new ArgumentNullException(nameof(relationshipService));
        this.handshakeService = handshakeService
            ?? throw new ArgumentNullException(nameof(handshakeService));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.profileStore = profileStore;
        this.logger = logger ?? NullLogger<CollaborationWorkspaceViewModel>.Instance;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal DeviceIdentity? LocalIdentity => identitySession.Identity;

    internal LocalCollaborationProfile? LocalProfile { get; private set; }

    internal CollaborationSessionSnapshot SessionSnapshot => sessionCoordinator?.Snapshot
        ?? CollaborationSessionSnapshot.Idle;

    internal CollaborationSessionCoordinator? SessionCoordinator => sessionCoordinator;

    internal bool CanGenerateFriendInvite => LocalProfile is not null
        && !RequiresRestartAfterIdentityImport;

    internal ImmutableArray<CollaborationContactItem> Contacts
    {
        get => contacts;
        private set => Set(ref contacts, value);
    }

    internal bool IsInitialized
    {
        get => isInitialized;
        private set => Set(ref isInitialized, value);
    }

    internal bool IsBusy
    {
        get => isBusy;
        private set => Set(ref isBusy, value);
    }

    internal bool RequiresRestartAfterIdentityImport
    {
        get => requiresRestartAfterIdentityImport;
        private set => Set(ref requiresRestartAfterIdentityImport, value);
    }

    internal void MarkIdentityImportCompleted()
    {
        RequiresRestartAfterIdentityImport = true;
        OnPropertyChanged(nameof(CanGenerateFriendInvite));
    }

    internal string? GeneratedInviteToken
    {
        get => generatedInviteToken;
        private set => Set(ref generatedInviteToken, value);
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync("initialize", async token =>
        {
            if (IsInitialized)
            {
                return;
            }

            await identitySession.InitializeAsync(token).ConfigureAwait(false);
            sessionCoordinator ??= new CollaborationSessionCoordinator(identitySession.Identity!.DeviceId);
            sessionCoordinator.SnapshotChanged += OnSessionSnapshotChanged;
            LocalProfile = profileStore is null
                ? null
                : await profileStore.LoadAsync(token).ConfigureAwait(false);
            await RefreshContactsAsync(token).ConfigureAwait(false);
            IsInitialized = true;
            OnPropertyChanged(nameof(LocalIdentity));
            OnPropertyChanged(nameof(LocalProfile));
            OnPropertyChanged(nameof(CanGenerateFriendInvite));
            OnPropertyChanged(nameof(SessionSnapshot));
            OnPropertyChanged(nameof(SessionCoordinator));
            CollaborationWorkspaceEvents.Initialized(logger, Contacts.Length);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<string> GenerateInviteAsync(
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        string? generated = null;
        await ExecuteAsync("generate-invite", async token =>
        {
            await identitySession.InitializeAsync(token).ConfigureAwait(false);
            if (LocalProfile is null)
            {
                throw new InvalidOperationException("A local collaboration nickname is required.");
            }

            generated = identitySession.CreateFriendInvite(LocalProfile.DisplayName, lifetime);
            GeneratedInviteToken = generated;
        }, cancellationToken).ConfigureAwait(false);
        return generated!;
    }

    internal async Task SaveLocalDisplayNameAsync(
        string displayName,
        CancellationToken cancellationToken)
    {
        if (profileStore is null)
        {
            throw new InvalidOperationException(
                "Local collaboration profile storage is unavailable.");
        }

        await ExecuteAsync("save-profile", async token =>
        {
            LocalProfile = await profileStore.SaveAsync(displayName, token).ConfigureAwait(false);
            OnPropertyChanged(nameof(LocalProfile));
            OnPropertyChanged(nameof(CanGenerateFriendInvite));
            CollaborationWorkspaceEvents.OperationCompleted(
                logger,
                "save-profile",
                $"saved:{LocalProfile.DisplayName.Length}");
        }, cancellationToken).ConfigureAwait(false);
    }

    internal InviteValidationResult ValidateCandidate(InvitationCandidate candidate) =>
        candidate.Kind == InvitationKind.Friend
            ? tokenService.Validate(candidate.Token, timeProvider.GetUtcNow())
            : throw new ArgumentOutOfRangeException(nameof(candidate));

    internal SessionInviteValidationResult ValidateSessionCandidate(InvitationCandidate candidate) =>
        candidate.Kind == InvitationKind.Session
            ? sessionTokenService.Validate(candidate.Token, timeProvider.GetUtcNow())
            : throw new ArgumentOutOfRangeException(nameof(candidate));

    internal async Task<string> GenerateSessionInviteAsync(
        CollaborationSessionId sessionId,
        SessionJoinPolicy joinPolicy,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        string? generated = null;
        await ExecuteAsync("generate-session-invite", async token =>
        {
            await identitySession.InitializeAsync(token).ConfigureAwait(false);
            generated = sessionTokenService.Create(
                identitySession.Handle,
                sessionId,
                joinPolicy,
                lifetime);
            CollaborationWorkspaceEvents.OperationCompleted(
                logger,
                "generate-session-invite",
                joinPolicy.ToString());
        }, cancellationToken).ConfigureAwait(false);
        return generated!;
    }

    internal void RememberGeneratedSessionInvite(string token, CollaborationSessionId sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        generatedSessionInvite = new GeneratedSessionInvite(token, sessionId);
    }

    internal string? GetReusableGeneratedSessionInviteToken()
    {
        GeneratedSessionInvite? cached = generatedSessionInvite;
        if (cached is null)
        {
            return null;
        }

        SessionInviteValidationResult validation = sessionTokenService.Validate(
            cached.Token,
            timeProvider.GetUtcNow());
        CollaborationSessionSnapshot snapshot = SessionSnapshot;
        bool matchesCurrentHostSession = validation.IsValid
            && validation.Invite!.SessionId == cached.SessionId
            && snapshot.Role == CollaborationSessionRole.Host
            && snapshot.SessionId == cached.SessionId
            && snapshot.Phase is (CollaborationSessionPhase.AwaitingHostConsent
                or CollaborationSessionPhase.Active);
        if (matchesCurrentHostSession)
        {
            return cached.Token;
        }

        generatedSessionInvite = null;
        if (!validation.IsValid
            && snapshot.Role == CollaborationSessionRole.Host
            && snapshot.SessionId == cached.SessionId
            && snapshot.MemberCount == 1
            && snapshot.Phase is (CollaborationSessionPhase.AwaitingHostConsent
                or CollaborationSessionPhase.Active))
        {
            RequireSessionCoordinator().Leave();
            CollaborationWorkspaceEvents.SessionInvitationExpired(logger, validation.ErrorCode.ToString());
        }

        return null;
    }

    internal void PrepareHostSession(CollaborationSessionId sessionId, SessionJoinPolicy joinPolicy)
    {
        generatedSessionInvite = null;
        RequireSessionCoordinator().PrepareHost(sessionId, joinPolicy);
        CollaborationWorkspaceEvents.OperationCompleted(logger, "session-host-prepared", joinPolicy.ToString());
    }

    internal void PrepareJoinSession(SessionInvite invite)
    {
        ArgumentNullException.ThrowIfNull(invite);
        generatedSessionInvite = null;
        RequireSessionCoordinator().PrepareJoin(invite);
        CollaborationWorkspaceEvents.OperationCompleted(logger, "session-participant-prepared", invite.JoinPolicy.ToString());
    }

    internal void ConfirmModelDistributionConsent()
    {
        RequireSessionCoordinator().ConfirmModelDistributionConsent();
        CollaborationWorkspaceEvents.OperationCompleted(logger, "session-consent-confirmed", "active");
    }

    internal void DeclineModelDistributionConsent()
    {
        RequireSessionCoordinator().DeclineModelDistributionConsent();
        CollaborationWorkspaceEvents.OperationCompleted(logger, "session-consent-declined", "idle");
    }

    internal void LeaveSession()
    {
        generatedSessionInvite = null;
        RequireSessionCoordinator().Leave();
        CollaborationWorkspaceEvents.OperationCompleted(logger, "session-left", "idle");
    }

    internal async Task<FriendAcceptanceResult> AcceptAsync(
        InvitationCandidate candidate,
        string localDisplayName,
        CancellationToken cancellationToken)
    {
        FriendAcceptanceResult? result = null;
        await ExecuteAsync("accept-invite", async token =>
        {
            await identitySession.InitializeAsync(token).ConfigureAwait(false);
            result = await acceptanceService.AcceptAsync(
                candidate.Token,
                identitySession.Identity!,
                localDisplayName,
                token).ConfigureAwait(false);
            if (result.Code == FriendAcceptanceResultCode.AcceptedPending)
            {
                await RefreshContactsAsync(token).ConfigureAwait(false);
            }

            CollaborationWorkspaceEvents.OperationCompleted(
                logger, "accept-invite", result.Code.ToString());
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }

    internal Task UpdateMetadataAsync(
        DeviceId deviceId,
        string displayName,
        string? note,
        CancellationToken cancellationToken) => ExecuteAsync("update-metadata", async token =>
        {
            await friendStore.UpdateMetadataAsync(deviceId, displayName, note, token).ConfigureAwait(false);
            await RefreshContactsAsync(token).ConfigureAwait(false);
            CollaborationWorkspaceEvents.OperationCompleted(logger, "update-metadata", "updated");
        }, cancellationToken);

    internal Task BlockAsync(DeviceId deviceId, CancellationToken cancellationToken) =>
        ExecuteAsync("block", async token =>
        {
            await friendStore.SetBlockedAsync(deviceId, timeProvider.GetUtcNow(), token).ConfigureAwait(false);
            await RefreshContactsAsync(token).ConfigureAwait(false);
            CollaborationWorkspaceEvents.OperationCompleted(logger, "block", "blocked");
        }, cancellationToken);

    internal Task DeleteAsync(DeviceId deviceId, CancellationToken cancellationToken) =>
        ExecuteAsync("delete", async token =>
        {
            FriendRelationshipRemovalResult result = await relationshipService.RemoveAsync(
                deviceId,
                token).ConfigureAwait(false);
            if (result.Code != FriendRelationshipRemovalResultCode.Removed)
            {
                throw new InvalidOperationException(
                    $"Friend relationship removal failed: {result.Code}.");
            }
            await RefreshContactsAsync(token).ConfigureAwait(false);
            CollaborationWorkspaceEvents.OperationCompleted(logger, "delete", "removed");
        }, cancellationToken);

    internal async Task<HandshakeOfferHandle> CreateHandshakeOfferAsync(
        DeviceId deviceId,
        CancellationToken cancellationToken)
    {
        HandshakeOfferHandle? offer = null;
        await ExecuteAsync("handshake-create-offer", async token =>
        {
            await identitySession.InitializeAsync(token).ConfigureAwait(false);
            offer = handshakeService.CreateOffer(identitySession.Handle, deviceId);
            CollaborationWorkspaceEvents.OperationCompleted(
                logger, "handshake-create-offer", "created");
        }, cancellationToken).ConfigureAwait(false);
        return offer!;
    }

    internal async Task<HandshakeAcceptResult> AcceptHandshakeOfferAsync(
        string encodedOffer,
        CancellationToken cancellationToken)
    {
        if (!FriendshipHandshakeTextCodec.TryDecode(encodedOffer, out byte[] offerBytes))
        {
            return new HandshakeAcceptResult(FriendshipHandshakeResultCode.InvalidMessage, []);
        }

        try
        {
            HandshakeAcceptResult? result = null;
            await ExecuteAsync("handshake-accept", async token =>
            {
                await identitySession.InitializeAsync(token).ConfigureAwait(false);
                result = await handshakeService.AcceptOfferAsync(
                    identitySession.Handle,
                    offerBytes,
                    token).ConfigureAwait(false);
                if (result.Code == FriendshipHandshakeResultCode.Completed)
                {
                    await RefreshContactsAsync(token).ConfigureAwait(false);
                }

                CollaborationWorkspaceEvents.OperationCompleted(
                    logger, "handshake-accept", result.Code.ToString());
            }, cancellationToken).ConfigureAwait(false);
            return result!;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(offerBytes);
        }
    }

    internal async Task<HandshakeCompleteResult> CompleteHandshakeOfferAsync(
        HandshakeOfferHandle offer,
        string encodedResponse,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (!FriendshipHandshakeTextCodec.TryDecode(encodedResponse, out byte[] responseBytes))
        {
            return new HandshakeCompleteResult(FriendshipHandshakeResultCode.InvalidMessage);
        }

        try
        {
            HandshakeCompleteResult? result = null;
            await ExecuteAsync("handshake-complete", async token =>
            {
                await identitySession.InitializeAsync(token).ConfigureAwait(false);
                result = await handshakeService.CompleteOfferAsync(
                    identitySession.Handle,
                    offer,
                    responseBytes,
                    token).ConfigureAwait(false);
                if (result.Code == FriendshipHandshakeResultCode.Completed)
                {
                    await RefreshContactsAsync(token).ConfigureAwait(false);
                }

                CollaborationWorkspaceEvents.OperationCompleted(
                    logger, "handshake-complete", result.Code.ToString());
            }, cancellationToken).ConfigureAwait(false);
            return result!;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(responseBytes);
        }
    }

    internal CollaborationContactItem GetRequiredContact(DeviceId deviceId) =>
        Contacts.Single(contact => contact.DeviceId == deviceId);

    private async Task RefreshContactsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FriendRecord> records = await friendStore.ListAsync(cancellationToken)
            .ConfigureAwait(false);
        Contacts = records.Select(CollaborationContactItem.FromRecord).ToImmutableArray();
    }

    private async Task ExecuteAsync(
        string operation,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (RequiresRestartAfterIdentityImport)
        {
            throw new InvalidOperationException("Collaboration identity was replaced and requires restart.");
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            IsBusy = true;
            await action(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            CollaborationWorkspaceEvents.OperationFailed(logger, operation, exception.GetType().Name);
            throw;
        }
        finally
        {
            IsBusy = false;
            operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            disposed = true;
        }
        finally
        {
            operationGate.Release();
        }

        operationGate.Dispose();
        await identitySession.DisposeAsync().ConfigureAwait(false);
    }

    private CollaborationSessionCoordinator RequireSessionCoordinator() => sessionCoordinator
        ?? throw new InvalidOperationException("The collaboration identity must be initialized first.");

    private void OnSessionSnapshotChanged(object? sender, CollaborationSessionSnapshot snapshot) =>
        OnPropertyChanged(nameof(SessionSnapshot));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed record GeneratedSessionInvite(string Token, CollaborationSessionId SessionId);
}
