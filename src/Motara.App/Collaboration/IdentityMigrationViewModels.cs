using System.ComponentModel;
using System.Runtime.CompilerServices;
using Motara.Collaboration.Migration;

namespace Motara.App.Collaboration;

internal enum IdentityMigrationMode
{
    Export,
    Import,
}

internal sealed class IdentityMigrationViewModel : INotifyPropertyChanged
{
    private readonly CollaborationIdentityArchiveService archiveService;
    private readonly Action imported;
    private readonly Action close;
    private string filePath = string.Empty;
    private string passphrase = string.Empty;
    private string confirmationPassphrase = string.Empty;
    private CollaborationIdentityArchiveInspection? inspection;
    private string? statusResourceKey;
    private bool isReplacementConfirmationVisible;
    private bool isBusy;

    internal IdentityMigrationViewModel(
        IdentityMigrationMode mode,
        CollaborationIdentityArchiveService archiveService,
        Action imported,
        Action close)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        Mode = mode;
        this.archiveService = archiveService ?? throw new ArgumentNullException(nameof(archiveService));
        this.imported = imported ?? throw new ArgumentNullException(nameof(imported));
        this.close = close ?? throw new ArgumentNullException(nameof(close));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal IdentityMigrationMode Mode { get; }

    internal string FilePath
    {
        get => filePath;
        set => Set(ref filePath, value);
    }

    internal string Passphrase
    {
        get => passphrase;
        set => Set(ref passphrase, value);
    }

    internal string ConfirmationPassphrase
    {
        get => confirmationPassphrase;
        set => Set(ref confirmationPassphrase, value);
    }

    internal CollaborationIdentityArchiveInspection? Inspection
    {
        get => inspection;
        private set => Set(ref inspection, value);
    }

    internal string? StatusResourceKey
    {
        get => statusResourceKey;
        private set => Set(ref statusResourceKey, value);
    }

    internal bool IsReplacementConfirmationVisible
    {
        get => isReplacementConfirmationVisible;
        private set => Set(ref isReplacementConfirmationVisible, value);
    }

    internal bool IsBusy
    {
        get => isBusy;
        private set => Set(ref isBusy, value);
    }

    internal async Task<bool> ExportAsync(CancellationToken cancellationToken)
    {
        if (Mode != IdentityMigrationMode.Export)
        {
            throw new InvalidOperationException("The workspace is not in export mode.");
        }

        if (string.IsNullOrWhiteSpace(FilePath) || string.IsNullOrEmpty(Passphrase))
        {
            StatusResourceKey = "Workspace.Collaboration.Identity.RequiredFields";
            return false;
        }

        if (!string.Equals(Passphrase, ConfirmationPassphrase, StringComparison.Ordinal))
        {
            StatusResourceKey = "Workspace.Collaboration.Identity.PassphraseMismatch";
            return false;
        }

        return await ExecuteAsync(async token =>
        {
            await archiveService.ExportAsync(FilePath, Passphrase.AsMemory(), token)
                .ConfigureAwait(false);
            ClearPassphrases();
            StatusResourceKey = "Workspace.Collaboration.Identity.ExportCompleted";
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> PrepareImportAsync(CancellationToken cancellationToken)
    {
        if (Mode != IdentityMigrationMode.Import)
        {
            throw new InvalidOperationException("The workspace is not in import mode.");
        }

        if (string.IsNullOrWhiteSpace(FilePath) || string.IsNullOrEmpty(Passphrase))
        {
            StatusResourceKey = "Workspace.Collaboration.Identity.RequiredFields";
            return false;
        }

        return await ExecuteAsync(async token =>
        {
            Inspection = await archiveService.InspectAsync(FilePath, Passphrase.AsMemory(), token)
                .ConfigureAwait(false);
            IsReplacementConfirmationVisible = true;
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> ConfirmImportAsync(CancellationToken cancellationToken)
    {
        if (!IsReplacementConfirmationVisible || Inspection is null)
        {
            return false;
        }

        return await ExecuteAsync(async token =>
        {
            await archiveService.ImportAsync(FilePath, Passphrase.AsMemory(), token)
                .ConfigureAwait(false);
            IsReplacementConfirmationVisible = false;
            ClearPassphrases();
            imported();
            StatusResourceKey = "Workspace.Collaboration.Identity.ImportCompleted";
        }, cancellationToken).ConfigureAwait(false);
    }

    internal void CancelReplacement() => IsReplacementConfirmationVisible = false;

    internal void Close()
    {
        ClearPassphrases();
        close();
    }

    private async Task<bool> ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        StatusResourceKey = null;
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CollaborationIdentityArchiveException)
        {
            StatusResourceKey = "Workspace.Collaboration.Identity.OperationFailed";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearPassphrases()
    {
        Passphrase = string.Empty;
        ConfirmationPassphrase = string.Empty;
    }

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
