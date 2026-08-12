using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Motara.App.Localization;
using Motara.App.Models;
using Motara.ModelLibrary;

namespace Motara.App.ViewModels;

public enum ModelCatalogPresentationState
{
    Empty = 0,
    Scanning = 1,
    Ready = 2,
    Failed = 3,
    Importing = 4,
}

public sealed class ModelCatalogViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan TransientStatusDuration = TimeSpan.FromSeconds(3);
    private readonly IModelCatalog catalog;
    private readonly IModelsFolderLauncher folderLauncher;
    private readonly string modelsRoot;
    private LocalizationManager localization;
    private readonly IModelImporter? importer;
    private readonly IModelImportSourcePicker? importSourcePicker;
    private readonly Func<TimeSpan, CancellationToken, Task> transientStatusDelay;
    private readonly IModelFileOrganizationService organizationService;
    private readonly object refreshGate = new();
    private Task? activeRefresh;
    private int statusRevision;
    private string? transientStatusRestoreValue;

    internal ModelCatalogViewModel(
        IModelCatalog catalog,
        IModelsFolderLauncher folderLauncher,
        string modelsRoot,
        LocalizationManager localization,
        IModelImporter? importer = null,
        IModelImportSourcePicker? importSourcePicker = null,
        Func<TimeSpan, CancellationToken, Task>? transientStatusDelay = null,
        IModelFileOrganizationService? organizationService = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(folderLauncher);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        ArgumentNullException.ThrowIfNull(localization);
        this.catalog = catalog;
        this.folderLauncher = folderLauncher;
        this.modelsRoot = Path.GetFullPath(modelsRoot);
        this.localization = localization;
        this.importer = importer;
        this.importSourcePicker = importSourcePicker;
        this.transientStatusDelay = transientStatusDelay ?? Task.Delay;
        this.organizationService = organizationService ?? new ModelFileOrganizationService();
        RefreshCommand = new AsyncCommand(RefreshAsync);
        ImportCommand = new AsyncCommand(ImportAsync);
        OrganizeViewedModelCommand = new AsyncCommand(OrganizeViewedModelAsync);
        OpenModelsFolderCommand = new DelegateCommand(_ => folderLauncher.Open(this.modelsRoot));
        SelectModelCommand = new DelegateCommand(SelectModel);
        ApplySnapshot(catalog.Current);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ImmutableArray<ModelCatalogEntryViewModel> Entries { get; private set; } = [];

    public ModelCatalogPresentationState State { get; private set; } = ModelCatalogPresentationState.Empty;

    public string StatusText { get; private set; } = string.Empty;

    public ModelId? SelectedModelId { get; private set; }

    public ModelId? ViewedModelId { get; private set; }

    public ModelCatalogEntryViewModel? ViewedEntry => ViewedModelId is ModelId modelId
        ? Entries.FirstOrDefault(entry => entry.Id == modelId)
        : null;

    public IAsyncCommand RefreshCommand { get; }

    public IAsyncCommand ImportCommand { get; }

    public IAsyncCommand OrganizeViewedModelCommand { get; }

    public ICommand OpenModelsFolderCommand { get; }

    public ICommand SelectModelCommand { get; }

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task refresh;
        lock (refreshGate)
        {
            refresh = activeRefresh ??= RefreshAndClearAsync(cancellationToken);
        }

        return refresh.WaitAsync(cancellationToken);
    }

    internal void UpdateLocalization(LocalizationManager value)
    {
        ArgumentNullException.ThrowIfNull(value);
        localization = value;
        string? status = State switch
        {
            ModelCatalogPresentationState.Empty => localization.GetString("Menu.Model.Empty"),
            ModelCatalogPresentationState.Scanning => localization.GetString("Menu.Model.Scanning"),
            ModelCatalogPresentationState.Importing => localization.GetString("Menu.Model.Importing"),
            ModelCatalogPresentationState.Ready => string.Format(
                localization.Culture,
                localization.GetString("Menu.Model.ReadyFormat"),
                Entries.Length),
            _ => null,
        };
        if (status is not null)
        {
            SetStatusText(status);
        }
    }

    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        if (importer is null || importSourcePicker is null)
        {
            return;
        }

        string? descriptorPath = await importSourcePicker
            .PickDescriptorAsync(cancellationToken)
            .ConfigureAwait(true);
        if (descriptorPath is null)
        {
            return;
        }

        State = ModelCatalogPresentationState.Importing;
        SetStatusText(localization.GetString("Menu.Model.Importing"));
        OnPropertyChanged(nameof(State));
        string extension = Path.GetExtension(descriptorPath);
        ModelImportResult result = extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".rar", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)
            ? await importer.ImportArchiveAsync(
                descriptorPath,
                ModelImportConflictBehavior.Cancel,
                cancellationToken).ConfigureAwait(true)
            : await importer.ImportDescriptorAsync(
                descriptorPath,
                ModelImportConflictBehavior.Cancel,
                cancellationToken).ConfigureAwait(true);
        if (result.Succeeded)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(true);
            if (result.ModelId is ModelId importedModelId)
            {
                SelectModel(importedModelId);
            }
            return;
        }

        State = ModelCatalogPresentationState.Failed;
        SetStatusText(localization.GetString(result.Error?.Code switch
        {
            Motara.ModelRuntime.Abstractions.ModelErrorCode.NameConflict =>
                "Menu.Model.ImportFailed.NameConflict",
            Motara.ModelRuntime.Abstractions.ModelErrorCode.ArchiveModelCount =>
                "Menu.Model.ImportFailed.ModelCount",
            Motara.ModelRuntime.Abstractions.ModelErrorCode.SizeLimitExceeded =>
                "Menu.Model.ImportFailed.SizeLimit",
            _ => "Menu.Model.ImportFailed",
        }));
        OnPropertyChanged(nameof(State));
    }

    private async Task OrganizeViewedModelAsync(CancellationToken cancellationToken)
    {
        ModelCatalogEntryViewModel? entry = ViewedEntry;
        if (entry is null)
        {
            return;
        }

        SetStatusText(localization.GetString("Menu.Model.Organizing"));
        if (entry.Descriptor is null)
        {
            State = ModelCatalogPresentationState.Failed;
            SetStatusText(localization.GetString("Menu.Model.OrganizeConflict"));
            OnPropertyChanged(nameof(State));
            return;
        }

        ModelFileOrganizationResult result = await organizationService
            .OrganizeAsync(CreateOrganizationRequest(entry), cancellationToken)
            .ConfigureAwait(true);
        if (!result.Succeeded)
        {
            State = ModelCatalogPresentationState.Failed;
            SetStatusText(localization.GetString("Menu.Model.OrganizeConflict"));
            OnPropertyChanged(nameof(State));
            return;
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(true);
        string stableStatus = StatusText;
        ShowTransientStatus(localization.GetString("Menu.Model.Organized"), stableStatus);
    }

    private static ModelFileOrganizationRequest CreateOrganizationRequest(
        ModelCatalogEntryViewModel entry) => new(
            entry.Id.Value,
            entry.DisplayName,
            entry.RootPath,
            entry.Descriptor!.DescriptorPath,
            entry.Descriptor);

    private async Task RefreshAndClearAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        State = ModelCatalogPresentationState.Scanning;
        SetStatusText(localization.GetString("Menu.Model.Scanning"));
        OnPropertyChanged(nameof(State));
        try
        {
            ModelCatalogSnapshot snapshot = await catalog.RefreshAsync(cancellationToken)
                .ConfigureAwait(true);
            ApplySnapshot(snapshot);
        }
        finally
        {
            lock (refreshGate)
            {
                activeRefresh = null;
            }
        }
    }

    private void ApplySnapshot(ModelCatalogSnapshot snapshot)
    {
        if (ViewedModelId is ModelId viewedModelId
            && !snapshot.Entries.Any(entry => entry.Id == viewedModelId))
        {
            ViewedModelId = null;
            OnPropertyChanged(nameof(ViewedModelId));
            OnPropertyChanged(nameof(ViewedEntry));
        }

        Entries = snapshot.Entries
            .Select(entry => new ModelCatalogEntryViewModel(
                entry.Id,
                entry.DisplayName,
                entry.Descriptor?.RootPath
                    ?? Path.GetDirectoryName(entry.DescriptorPath)
                    ?? modelsRoot,
                entry.IsSelectable,
                ViewedModelId.HasValue && ViewedModelId.Value == entry.Id,
                SelectedModelId.HasValue && SelectedModelId.Value == entry.Id,
                entry.Descriptor is null ? string.Empty : "Live2D Cubism model3.json",
                entry.Descriptor?.TexturePaths.Length ?? 0,
                entry.Descriptor?.ThumbnailPath,
                entry.Descriptor?.FormatVersion ?? Moc3FormatVersion.Unknown,
                entry.Descriptor))
            .ToImmutableArray();

        if (snapshot.Status == ModelCatalogStatus.Faulted)
        {
            State = ModelCatalogPresentationState.Failed;
            SetStatusText(localization.GetString("Menu.Model.Failed"));
        }
        else if (Entries.IsEmpty)
        {
            State = ModelCatalogPresentationState.Empty;
            SetStatusText(localization.GetString("Menu.Model.Empty"));
        }
        else
        {
            State = ModelCatalogPresentationState.Ready;
            SetStatusText(string.Format(
                localization.Culture,
                localization.GetString("Menu.Model.ReadyFormat"),
                Entries.Length));
        }

        OnPropertyChanged(nameof(Entries));
        OnPropertyChanged(nameof(State));
    }

    private void SetStatusText(string value)
    {
        Interlocked.Increment(ref statusRevision);
        transientStatusRestoreValue = null;
        StatusText = value;
        OnPropertyChanged(nameof(StatusText));
    }

    private void ShowTransientStatus(string value, string restoreValue)
    {
        SetStatusText(value);
        transientStatusRestoreValue = restoreValue;
        int revision = Volatile.Read(ref statusRevision);
        _ = RestoreStatusAsync(revision, restoreValue);
    }

    private async Task RestoreStatusAsync(int revision, string restoreValue)
    {
        await transientStatusDelay(TransientStatusDuration, CancellationToken.None)
            .ConfigureAwait(true);
        if (Interlocked.CompareExchange(ref statusRevision, revision + 1, revision) != revision)
        {
            return;
        }

        transientStatusRestoreValue = null;
        StatusText = restoreValue;
        OnPropertyChanged(nameof(StatusText));
    }

    private void RestoreTransientStatus()
    {
        if (transientStatusRestoreValue is string restoreValue)
        {
            SetStatusText(restoreValue);
        }
    }

    private void SelectModel(object? parameter)
    {
        if (parameter is not ModelId modelId
            || !Entries.Any(entry => entry.Id == modelId && entry.IsSelectable))
        {
            return;
        }

        bool isDeselecting = ViewedModelId == modelId;
        ViewedModelId = isDeselecting ? null : modelId;
        Entries = Entries
            .Select(entry => entry with { IsViewed = !isDeselecting && entry.Id == modelId })
            .ToImmutableArray();
        OnPropertyChanged(nameof(ViewedModelId));
        OnPropertyChanged(nameof(ViewedEntry));
        OnPropertyChanged(nameof(Entries));
        RestoreTransientStatus();
    }

    internal void SetSelectedModel(ModelId? modelId)
    {
        SelectedModelId = modelId;
        Entries = Entries
            .Select(entry => entry with
            {
                IsCurrentMainModel = modelId.HasValue && entry.Id == modelId.Value,
            })
            .ToImmutableArray();
        OnPropertyChanged(nameof(SelectedModelId));
        OnPropertyChanged(nameof(Entries));
    }

    internal void ClearViewedModel()
    {
        if (ViewedModelId is null)
        {
            return;
        }

        ViewedModelId = null;
        Entries = Entries
            .Select(static entry => entry with { IsViewed = false })
            .ToImmutableArray();
        OnPropertyChanged(nameof(ViewedModelId));
        OnPropertyChanged(nameof(ViewedEntry));
        OnPropertyChanged(nameof(Entries));
        RestoreTransientStatus();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public sealed record ModelCatalogEntryViewModel(
        ModelId Id,
        string DisplayName,
        string RootPath,
        bool IsSelectable,
        bool IsViewed,
        bool IsCurrentMainModel,
        string FormatSummary,
        int TextureCount,
        string? ThumbnailPath = null,
        Moc3FormatVersion FormatVersion = Moc3FormatVersion.Unknown,
        ModelDescriptor? Descriptor = null);

    private sealed class DelegateCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute(parameter);
    }

    private sealed class AsyncCommand(Func<CancellationToken, Task> execute) : IAsyncCommand
    {
        private int executing;

        public event EventHandler? CanExecuteChanged;

        public bool IsExecuting => Volatile.Read(ref executing) != 0;

        public bool CanExecute(object? parameter) => !IsExecuting;

        public void Execute(object? parameter) => _ = ExecuteAsync(parameter, CancellationToken.None);

        public async Task ExecuteAsync(object? parameter, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref executing, 1, 0) != 0)
            {
                return;
            }

            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                await execute(cancellationToken);
            }
            finally
            {
                Volatile.Write(ref executing, 0);
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
