using System.ComponentModel;
using System.Runtime.CompilerServices;
using Motara.App.Shell;
using Motara.Collaboration.Handshake;

namespace Motara.App.Collaboration;

internal sealed class FriendDetailsViewModel : INotifyPropertyChanged, IWorkspaceCloseGuard, IDisposable
{
    private readonly CollaborationWorkspaceViewModel workspace;
    private readonly Action close;
    private HandshakeOfferHandle? pendingOffer;
    private CollaborationContactItem contact;
    private string displayName;
    private string note;
    private string outgoingOfferText = string.Empty;
    private string incomingHandshakeText = string.Empty;
    private string outgoingResponseText = string.Empty;
    private string? statusResourceKey;
    private bool isBusy;
    private bool isBlockConfirmationVisible;
    private bool isDeleteConfirmationVisible;
    private bool disposed;

    internal FriendDetailsViewModel(
        CollaborationWorkspaceViewModel workspace,
        CollaborationContactItem contact,
        Action close)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.contact = contact ?? throw new ArgumentNullException(nameof(contact));
        this.close = close ?? throw new ArgumentNullException(nameof(close));
        displayName = contact.DisplayName;
        note = contact.Note ?? string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal CollaborationContactItem Contact
    {
        get => contact;
        private set
        {
            if (Set(ref contact, value))
            {
                OnPropertyChanged(nameof(CanHandshake));
            }
        }
    }

    internal string DisplayName
    {
        get => displayName;
        set => Set(ref displayName, value);
    }

    internal string Note
    {
        get => note;
        set => Set(ref note, value);
    }

    internal string OutgoingOfferText
    {
        get => outgoingOfferText;
        private set => Set(ref outgoingOfferText, value);
    }

    internal string IncomingHandshakeText
    {
        get => incomingHandshakeText;
        set => Set(ref incomingHandshakeText, value);
    }

    internal string OutgoingResponseText
    {
        get => outgoingResponseText;
        private set => Set(ref outgoingResponseText, value);
    }

    internal string? StatusResourceKey
    {
        get => statusResourceKey;
        private set => Set(ref statusResourceKey, value);
    }

    internal bool IsBusy
    {
        get => isBusy;
        private set => Set(ref isBusy, value);
    }

    internal bool IsBlockConfirmationVisible
    {
        get => isBlockConfirmationVisible;
        private set => Set(ref isBlockConfirmationVisible, value);
    }

    internal bool IsDeleteConfirmationVisible
    {
        get => isDeleteConfirmationVisible;
        private set => Set(ref isDeleteConfirmationVisible, value);
    }

    internal bool CanHandshake => Contact.Status == CollaborationContactStatus.Pending;

    internal bool HasPendingOffer => pendingOffer is not null;

    internal Task SaveMetadataAsync(CancellationToken cancellationToken) => ExecuteAsync(async token =>
    {
        await workspace.UpdateMetadataAsync(
            Contact.DeviceId,
            DisplayName,
            string.IsNullOrWhiteSpace(Note) ? null : Note,
            token).ConfigureAwait(false);
        RefreshContact();
        StatusResourceKey = "Workspace.Collaboration.Friend.Saved";
    }, cancellationToken);

    internal Task BeginOfferAsync(CancellationToken cancellationToken) => ExecuteAsync(async token =>
    {
        EnsureHandshakeAllowed();
        DisposePendingOffer();
        pendingOffer = await workspace.CreateHandshakeOfferAsync(
            Contact.DeviceId,
            token).ConfigureAwait(false);
        OutgoingOfferText = FriendshipHandshakeTextCodec.Encode(
            pendingOffer.MessageBytes.AsSpan());
        IncomingHandshakeText = string.Empty;
        OutgoingResponseText = string.Empty;
        OnPropertyChanged(nameof(HasPendingOffer));
        StatusResourceKey = "Workspace.Collaboration.Handshake.OfferCreated";
    }, cancellationToken);

    internal Task AcceptOfferAsync(CancellationToken cancellationToken) => ExecuteAsync(async token =>
    {
        EnsureHandshakeAllowed();
        HandshakeAcceptResult result = await workspace.AcceptHandshakeOfferAsync(
            IncomingHandshakeText,
            token).ConfigureAwait(false);
        StatusResourceKey = ResultResourceKey(result.Code);
        if (result.Code == FriendshipHandshakeResultCode.Completed)
        {
            OutgoingResponseText = FriendshipHandshakeTextCodec.Encode(
                result.ResponseBytes.AsSpan());
            RefreshContact();
        }
    }, cancellationToken);

    internal Task CompleteOfferAsync(CancellationToken cancellationToken) => ExecuteAsync(async token =>
    {
        EnsureHandshakeAllowed();
        HandshakeOfferHandle offer = pendingOffer
            ?? throw new InvalidOperationException("A pending handshake offer is required.");
        HandshakeCompleteResult result = await workspace.CompleteHandshakeOfferAsync(
            offer,
            IncomingHandshakeText,
            token).ConfigureAwait(false);
        StatusResourceKey = ResultResourceKey(result.Code);
        if (result.Code == FriendshipHandshakeResultCode.Completed)
        {
            RefreshContact();
            DisposePendingOffer();
            OutgoingOfferText = string.Empty;
            IncomingHandshakeText = string.Empty;
        }
    }, cancellationToken);

    internal void RequestBlock() => IsBlockConfirmationVisible = true;

    internal void CancelBlock() => IsBlockConfirmationVisible = false;

    internal Task ConfirmBlockAsync(CancellationToken cancellationToken) => ExecuteAsync(async token =>
    {
        await workspace.BlockAsync(Contact.DeviceId, token).ConfigureAwait(false);
        IsBlockConfirmationVisible = false;
        DisposePendingOffer();
        RefreshContact();
        StatusResourceKey = "Workspace.Collaboration.Friend.Blocked";
    }, cancellationToken);

    internal void RequestDelete() => IsDeleteConfirmationVisible = true;

    internal void CancelDelete() => IsDeleteConfirmationVisible = false;

    internal Task ConfirmDeleteAsync(CancellationToken cancellationToken) => ExecuteAsync(async token =>
    {
        await workspace.DeleteAsync(Contact.DeviceId, token).ConfigureAwait(false);
        IsDeleteConfirmationVisible = false;
        Dispose();
        close();
    }, cancellationToken);

    internal void Close()
    {
        Dispose();
        close();
    }

    public Task<bool> RequestCloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dispose();
        return Task.FromResult(true);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        DisposePendingOffer();
        OutgoingOfferText = string.Empty;
        IncomingHandshakeText = string.Empty;
        OutgoingResponseText = string.Empty;
    }

    private async Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusResourceKey = null;
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            StatusResourceKey = "Workspace.Collaboration.Friend.OperationFailed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void EnsureHandshakeAllowed()
    {
        if (!CanHandshake)
        {
            throw new InvalidOperationException("Only pending contacts can complete a handshake.");
        }
    }

    private void RefreshContact() => Contact = workspace.GetRequiredContact(Contact.DeviceId);

    private void DisposePendingOffer()
    {
        if (pendingOffer is null)
        {
            return;
        }

        pendingOffer.Dispose();
        pendingOffer = null;
        OnPropertyChanged(nameof(HasPendingOffer));
    }

    private static string ResultResourceKey(FriendshipHandshakeResultCode result) =>
        $"Workspace.Collaboration.Handshake.{result}";

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
