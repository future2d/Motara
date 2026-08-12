using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Models;
using Motara.ModelLibrary;
using Motara.ModelRuntime.Abstractions;
using Motara.Persistence;

namespace Motara.App.ViewModels;

internal enum ModelConfigurationReviewState
{
    Loading = 0,
    Ready = 1,
    Missing = 2,
    NonCanonical = 3,
    Conflict = 4,
    Invalid = 5,
    Failed = 6,
}

internal sealed class ModelConfigurationReviewViewModel : INotifyPropertyChanged
{
    private readonly ModelCatalogViewModel.ModelCatalogEntryViewModel model;
    private readonly Func<ModelId, CancellationToken, Task<ModelCapabilities?>> capabilitiesProvider;
    private readonly Action cancelSelection;
    private readonly ScopedMotaraStorage storage;
    private readonly ModelParameterMappingService mappingService;
    private readonly ILogger logger;
    private readonly IModelFileOrganizationService organizationService;
    private ScopedMotaraScanResult? reviewedScan;
    private ModelConfigurationReviewState state = ModelConfigurationReviewState.Loading;
    private string? errorText;
    private bool isBusy;
    private bool repairFailed;

    internal ModelConfigurationReviewViewModel(
        ModelCatalogViewModel.ModelCatalogEntryViewModel model,
        Func<ModelId, CancellationToken, Task<ModelCapabilities?>> capabilitiesProvider,
        Action cancelSelection,
        ILogger? logger = null,
        IModelFileOrganizationService? organizationService = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(capabilitiesProvider);
        ArgumentNullException.ThrowIfNull(cancelSelection);
        this.model = model;
        this.capabilitiesProvider = capabilitiesProvider;
        this.cancelSelection = cancelSelection;
        storage = new ScopedMotaraStorage(model.RootPath, "model.motara.json", model.DisplayName);
        mappingService = new ModelParameterMappingService();
        this.logger = logger ?? NullLogger.Instance;
        this.organizationService = organizationService ?? new ModelFileOrganizationService();
        ConfirmCommand = new AsyncCommand(ConfirmAsync);
        RepairCommand = new AsyncCommand(RepairAsync);
        CancelCommand = new DelegateCommand(_ => Cancel());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal ModelConfigurationReviewState State
    {
        get => state;
        private set
        {
            if (state == value)
            {
                return;
            }

            state = value;
            Raise();
            Raise(nameof(IsVisible));
            Raise(nameof(CanConfirm));
            Raise(nameof(CanRepair));
        }
    }

    internal string? ErrorText
    {
        get => errorText;
        private set
        {
            if (StringComparer.Ordinal.Equals(errorText, value))
            {
                return;
            }

            errorText = value;
            Raise();
        }
    }

    internal bool IsVisible => State is not ModelConfigurationReviewState.Loading
        and not ModelConfigurationReviewState.Ready;

    internal bool CanConfirm => !IsBusy
        && State is (ModelConfigurationReviewState.Missing
            or ModelConfigurationReviewState.NonCanonical);

    internal bool CanRepair => !IsBusy && State == ModelConfigurationReviewState.Invalid;

    internal bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (isBusy == value)
            {
                return;
            }

            isBusy = value;
            Raise();
            Raise(nameof(CanConfirm));
            Raise(nameof(CanRepair));
        }
    }

    internal bool RepairFailed
    {
        get => repairFailed;
        private set => Set(ref repairFailed, value);
    }

    internal ICommand CancelCommand { get; }

    internal IAsyncCommand ConfirmCommand { get; }

    internal IAsyncCommand RepairCommand { get; }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            if (File.Exists(storage.ManifestPath))
            {
                await LoadCanonicalConfigurationAsync(cancellationToken).ConfigureAwait(false);
                ModelConfigurationReviewLog.Discovered(
                    logger,
                    model.Id.Value,
                    State.ToString(),
                    stopwatch.ElapsedMilliseconds);
                return;
            }

            ScopedMotaraScanResult scan = await storage.ScanAsync(cancellationToken)
                .ConfigureAwait(false);
            reviewedScan = scan;
            switch (scan.ManifestStatus)
            {
                case ScopedMotaraManifestStatus.Missing:
                    State = ModelConfigurationReviewState.Missing;
                    break;
                case ScopedMotaraManifestStatus.NonCanonical:
                    State = ModelConfigurationReviewState.NonCanonical;
                    break;
                case ScopedMotaraManifestStatus.Conflict:
                    State = ModelConfigurationReviewState.Conflict;
                    break;
                case ScopedMotaraManifestStatus.Canonical:
                    await LoadCanonicalConfigurationAsync(cancellationToken).ConfigureAwait(false);
                    break;
            }

            ModelConfigurationReviewLog.Discovered(
                logger,
                model.Id.Value,
                State.ToString(),
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
            State = ModelConfigurationReviewState.Failed;
            ModelConfigurationReviewLog.Failed(
                logger,
                model.Id.Value,
                exception.GetType().Name,
                stopwatch.ElapsedMilliseconds);
        }
    }

    private async Task LoadCanonicalConfigurationAsync(CancellationToken cancellationToken)
    {
        try
        {
            MotaraModelConfiguration configuration = await new MotaraModelConfigurationStore(
                model.RootPath,
                model.DisplayName).LoadAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("Model configuration is empty.");
            if ((configuration.FileLayout is not null
                    && model.Descriptor?.FileLayoutStatus != ModelFileLayoutStatus.Stale)
                || model.Descriptor is null)
            {
                State = ModelConfigurationReviewState.Ready;
                return;
            }

            ModelFileOrganizationAnalysis analysis = await organizationService.AnalyzeAsync(
                CreateOrganizationRequest(),
                cancellationToken).ConfigureAwait(false);
            State = !analysis.CanOrganize
                ? ModelConfigurationReviewState.Invalid
                : analysis.NeedsOrganization
                    ? ModelConfigurationReviewState.NonCanonical
                    : ModelConfigurationReviewState.Ready;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
        {
            ErrorText = exception.Message;
            RepairFailed = false;
            State = ModelConfigurationReviewState.Invalid;
        }
    }

    private async Task ConfirmAsync(CancellationToken cancellationToken)
    {
        if (!CanConfirm)
        {
            return;
        }

        IsBusy = true;
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            if (State == ModelConfigurationReviewState.NonCanonical && model.Descriptor is null)
            {
                ScopedMotaraScanResult scan = reviewedScan
                    ?? throw new InvalidOperationException("Model configuration scan is unavailable.");
                ScopedMotaraOrganizationResult result = await storage
                    .OrganizeAsync(scan, cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException("Model configuration organization is blocked by conflicts.");
                }
            }
            else if (State == ModelConfigurationReviewState.Missing)
            {
                ModelCapabilities? capabilities = await capabilitiesProvider(
                    model.Id,
                    cancellationToken).ConfigureAwait(false);
                if (capabilities is null)
                {
                    throw new InvalidOperationException("Model capabilities are unavailable.");
                }

                ModelParameterMappingDocument document = await mappingService.LoadAsync(
                    model,
                    capabilities,
                    cancellationToken).ConfigureAwait(false);
                await mappingService.SaveAsync(document, cancellationToken).ConfigureAwait(false);
            }

            if (model.Descriptor is not null)
            {
                ModelFileOrganizationResult organization = await organizationService.OrganizeAsync(
                    CreateOrganizationRequest(),
                    cancellationToken).ConfigureAwait(false);
                if (!organization.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Model file organization failed: {organization.ErrorCode ?? "Unknown"}.");
                }
            }

            State = ModelConfigurationReviewState.Ready;
            reviewedScan = null;
            ModelConfigurationReviewLog.Confirmed(
                logger,
                model.Id.Value,
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
            State = ModelConfigurationReviewState.Failed;
            ModelConfigurationReviewLog.Failed(
                logger,
                model.Id.Value,
                exception.GetType().Name,
                stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RepairAsync(CancellationToken cancellationToken)
    {
        if (!CanRepair)
        {
            return;
        }

        IsBusy = true;
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            ModelCapabilities? capabilities = await capabilitiesProvider(model.Id, cancellationToken)
                .ConfigureAwait(false);
            if (capabilities is null)
            {
                throw new InvalidOperationException("Model capabilities are unavailable.");
            }

            await mappingService.RepairAsync(model, capabilities, cancellationToken).ConfigureAwait(false);
            if (model.Descriptor is not null)
            {
                ModelFileOrganizationResult organization = await organizationService.OrganizeAsync(
                    CreateOrganizationRequest(),
                    cancellationToken).ConfigureAwait(false);
                if (!organization.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Model file organization failed: {organization.ErrorCode ?? "Unknown"}.");
                }
            }
            State = ModelConfigurationReviewState.Ready;
            ErrorText = null;
            RepairFailed = false;
            ModelConfigurationReviewLog.Repaired(logger, model.Id.Value, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
            RepairFailed = true;
            State = ModelConfigurationReviewState.Invalid;
            ModelConfigurationReviewLog.Failed(
                logger,
                model.Id.Value,
                exception.GetType().Name,
                stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Cancel()
    {
        ModelConfigurationReviewLog.Cancelled(logger, model.Id.Value);
        cancelSelection();
    }

    private ModelFileOrganizationRequest CreateOrganizationRequest() => new(
        model.Id.Value,
        model.DisplayName,
        model.RootPath,
        model.Descriptor!.DescriptorPath,
        model.Descriptor);

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

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

internal static partial class ModelConfigurationReviewLog
{
    [LoggerMessage(6650, LogLevel.Information,
        "Model configuration discovered for {ModelId}: {State}; elapsed={ElapsedMilliseconds}ms")]
    internal static partial void Discovered(
        ILogger logger,
        string modelId,
        string state,
        long elapsedMilliseconds);

    [LoggerMessage(6651, LogLevel.Information,
        "Model configuration creation or organization confirmed for {ModelId}; elapsed={ElapsedMilliseconds}ms")]
    internal static partial void Confirmed(
        ILogger logger,
        string modelId,
        long elapsedMilliseconds);

    [LoggerMessage(6652, LogLevel.Information,
        "Model configuration confirmation cancelled for {ModelId}")]
    internal static partial void Cancelled(ILogger logger, string modelId);

    [LoggerMessage(6653, LogLevel.Error,
        "Model configuration confirmation failed for {ModelId}: {ErrorType}; elapsed={ElapsedMilliseconds}ms")]
    internal static partial void Failed(
        ILogger logger,
        string modelId,
        string errorType,
        long elapsedMilliseconds);

    [LoggerMessage(6654, LogLevel.Information,
        "Invalid model configuration repaired for {ModelId}; elapsed={ElapsedMilliseconds}ms")]
    internal static partial void Repaired(
        ILogger logger,
        string modelId,
        long elapsedMilliseconds);
}
