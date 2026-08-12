using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Parameters;
using Motara.App.Shell;
using Motara.Core.Formulas;
using Motara.Core.Parameters;
using Motara.Tracking.Abstractions;

namespace Motara.App.ViewModels;

public enum SourceMappingApplyResult
{
    Success,
    ValidationFailed,
    Unavailable,
    PermissionDenied,
    StorageFailure,
    UnexpectedFailure,
}

public sealed record SourceMappingOutputItem(
    SourceMappingOutputDocument Output,
    string ParameterId,
    string? Subtitle,
    bool IsBuiltIn);

public sealed class SourceMappingEditorViewModel : INotifyPropertyChanged, IDisposable, IWorkspaceCloseGuard
{
    private delegate Task<SourceMappingValidationReport> ValidateDocumentDelegate(
        SourceMappingProfileDocument document,
        CancellationToken cancellationToken);

    private readonly ImmutableArray<TrackingInputDefinition> inputs;
    private ImmutableArray<SourceMappingInputItem> inputItems;
    private SourceMappingProfileDocument appliedBaseline;
    private SourceMappingOutputDocument selectedOutput;
    private ImmutableArray<SourceMappingOutputItem> outputItems;
    private Func<string, string> parameterLocalizer = static key => key;
    private string searchText = string.Empty;
    private string formula;
    private string? validationError;
    private string newParameterId = string.Empty;
    private readonly Func<SourceMappingProfileDocument, CancellationToken, Task>? applyAsync;
    private readonly Func<ImmutableArray<(string OldId, string NewId)>, CancellationToken, Task>? synchronizeReferencesAsync;
    private readonly Func<string, CancellationToken, Task<SourceMappingProfileDocument>>? importAsync;
    private readonly Func<SourceMappingProfileDocument, string, CancellationToken, Task<string>>? saveAsAsync;
    private readonly Func<CancellationToken, Task<SourceMappingProfileDocument>>? restoreDefaultAsync;
    private readonly Func<CancellationToken, Task>? openConfigurationFolderAsync;
    private bool isDeleteConfirmationVisible;
    private readonly ValidateDocumentDelegate validateAsync;
    private readonly TimeSpan validationDebounce;
    private readonly Func<Action, Task> postStateAsync;
    private CancellationTokenSource? validationCancellation;
    private long validationVersion;
    private SourceMappingValidationReport validationReport;
    private ImmutableArray<SourceMappingApplyError> applyValidationErrors = [];
    private readonly ILogger logger;
    private bool isDirty;
    private bool isCloseConfirmationVisible;
    private SourceMappingApplyResult? applyResult;
    private bool isRestoreDefaultConfirmationVisible;
    private bool isReferenceSyncConfirmationVisible;
    private ImmutableArray<(string OldId, string NewId)> pendingReferenceRenames;

    public SourceMappingEditorViewModel(
        SourceMappingProfileDocument document,
        IEnumerable<TrackingInputDefinition> inputs,
        Func<SourceMappingProfileDocument, CancellationToken, Task>? applyAsync = null,
        ILogger? logger = null,
        Func<string, CancellationToken, Task<SourceMappingProfileDocument>>? importAsync = null,
        Func<SourceMappingProfileDocument, string, CancellationToken, Task<string>>? saveAsAsync = null,
        Func<CancellationToken, Task<SourceMappingProfileDocument>>? restoreDefaultAsync = null,
        Func<CancellationToken, Task>? openConfigurationFolderAsync = null,
        Func<ImmutableArray<(string OldId, string NewId)>, CancellationToken, Task>? synchronizeReferencesAsync = null)
        : this(
            document,
            inputs,
            applyAsync,
            ValidateDocumentAsync,
            TimeSpan.FromMilliseconds(200),
            PostToUiAsync,
            logger,
            importAsync,
            saveAsAsync,
            restoreDefaultAsync,
            openConfigurationFolderAsync,
            synchronizeReferencesAsync)
    {
    }

    internal SourceMappingEditorViewModel(
        SourceMappingProfileDocument document,
        IEnumerable<TrackingInputDefinition> inputs,
        Func<SourceMappingProfileDocument, CancellationToken, Task>? applyAsync,
        Func<SourceMappingProfileDocument, CancellationToken, Task<FormulaEditorState>> validateAsync,
        TimeSpan validationDebounce,
        Func<Action, Task> postStateAsync,
        ILogger? logger = null,
        Func<string, CancellationToken, Task<SourceMappingProfileDocument>>? importAsync = null,
        Func<SourceMappingProfileDocument, string, CancellationToken, Task<string>>? saveAsAsync = null,
        Func<CancellationToken, Task<SourceMappingProfileDocument>>? restoreDefaultAsync = null,
        Func<CancellationToken, Task>? openConfigurationFolderAsync = null,
        Func<ImmutableArray<(string OldId, string NewId)>, CancellationToken, Task>? synchronizeReferencesAsync = null)
        : this(
            document,
            inputs,
            applyAsync,
            async (snapshot, cancellationToken) => SourceMappingValidationReport.FromSingle(
                snapshot.Outputs.Length,
                await validateAsync(snapshot, cancellationToken).ConfigureAwait(false)),
            validationDebounce,
            postStateAsync,
            logger,
            importAsync,
            saveAsAsync,
            restoreDefaultAsync,
            openConfigurationFolderAsync,
            synchronizeReferencesAsync)
    {
    }

    private SourceMappingEditorViewModel(
        SourceMappingProfileDocument document,
        IEnumerable<TrackingInputDefinition> inputs,
        Func<SourceMappingProfileDocument, CancellationToken, Task>? applyAsync,
        ValidateDocumentDelegate validateAsync,
        TimeSpan validationDebounce,
        Func<Action, Task> postStateAsync,
        ILogger? logger,
        Func<string, CancellationToken, Task<SourceMappingProfileDocument>>? importAsync,
        Func<SourceMappingProfileDocument, string, CancellationToken, Task<string>>? saveAsAsync,
        Func<CancellationToken, Task<SourceMappingProfileDocument>>? restoreDefaultAsync,
        Func<CancellationToken, Task>? openConfigurationFolderAsync,
        Func<ImmutableArray<(string OldId, string NewId)>, CancellationToken, Task>? synchronizeReferencesAsync)
    {
        document = SourceMappingParameterCatalog.NormalizeBuiltIns(
            document ?? throw new ArgumentNullException(nameof(document)));
        Document = document;
        appliedBaseline = document;
        this.inputs = inputs?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(inputs));
        inputItems = this.inputs.Select(static input => new SourceMappingInputItem(
            input,
            input.DisplayNameResourceKey)).ToImmutableArray();
        selectedOutput = document.Outputs[0];
        outputItems = CreateOutputItems(document.Outputs);
        formula = selectedOutput.Formula;
        this.applyAsync = applyAsync;
        this.synchronizeReferencesAsync = synchronizeReferencesAsync;
        this.importAsync = importAsync;
        this.saveAsAsync = saveAsAsync;
        this.restoreDefaultAsync = restoreDefaultAsync;
        this.openConfigurationFolderAsync = openConfigurationFolderAsync;
        this.validateAsync = validateAsync ?? throw new ArgumentNullException(nameof(validateAsync));
        this.validationDebounce = validationDebounce;
        this.postStateAsync = postStateAsync ?? throw new ArgumentNullException(nameof(postStateAsync));
        this.logger = logger ?? NullLogger.Instance;
        validationReport = SourceMappingValidationReport.Empty(document.Outputs.Length);
        Completions = CreateCompletions(inputItems, document.Outputs);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SourceMappingProfileDocument Document { get; private set; }

    public ImmutableArray<SourceMappingOutputDocument> Outputs => Document.Outputs;

    public bool IsReferenceSyncConfirmationVisible
    {
        get => isReferenceSyncConfirmationVisible;
        private set { if (isReferenceSyncConfirmationVisible == value) return; isReferenceSyncConfirmationVisible = value; Raise(nameof(IsReferenceSyncConfirmationVisible)); }
    }

    public ImmutableArray<SourceMappingOutputItem> OutputItems => outputItems;

    public ImmutableArray<FormulaCompletionItem> Completions { get; private set; }

    public SourceMappingValidationReport ValidationReport
    {
        get => validationReport;
        private set
        {
            if (validationReport == value) return;
            validationReport = value;
            ValidationError = value.Diagnostics.FirstOrDefault()?.Diagnostic.Code.ToString();
            Raise(nameof(ValidationReport));
            Raise(nameof(EditorState));
        }
    }

    public FormulaEditorState EditorState
    {
        get
        {
            int index = Document.Outputs.IndexOf(selectedOutput);
            return index >= 0 && index < ValidationReport.OutputStates.Length
                ? ValidationReport.OutputStates[index]
                : FormulaEditorState.Empty;
        }
    }

    public ImmutableArray<SourceMappingApplyError> ApplyValidationErrors
    {
        get => applyValidationErrors;
        private set
        {
            if (applyValidationErrors == value) return;
            applyValidationErrors = value;
            Raise(nameof(ApplyValidationErrors));
        }
    }

    public Task ValidationTask { get; private set; } = Task.CompletedTask;

    public IEnumerable<SourceMappingInputItem> FilteredInputs => string.IsNullOrWhiteSpace(searchText)
        ? inputItems
        : inputItems.Where(input =>
            input.Id.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            || input.Subtitle.Contains(searchText, StringComparison.OrdinalIgnoreCase));

    public string SelectedParameterId
    {
        get => selectedOutput.ParameterId;
        set => _ = TryRenameSelectedParameter(value);
    }

    public string? SelectedSubtitle
    {
        get => GetOutputSubtitle(selectedOutput);
        set
        {
            if (IsSelectedBuiltInParameter)
            {
                Raise(nameof(SelectedSubtitle));
                return;
            }

            UpdateSelected(selectedOutput with { Subtitle = value });
        }
    }

    public bool IsSelectedBuiltInParameter =>
        SourceMappingParameterCatalog.IsBuiltIn(selectedOutput.ParameterId);

    public bool CanDeleteSelectedParameter => !IsSelectedBuiltInParameter;

    public bool CanEditSelectedParameterMetadata => !IsSelectedBuiltInParameter;

    public bool IsDirty
    {
        get => isDirty;
        private set
        {
            if (isDirty == value) return;
            isDirty = value;
            Raise(nameof(IsDirty));
        }
    }

    public bool IsCloseConfirmationVisible
    {
        get => isCloseConfirmationVisible;
        private set
        {
            if (isCloseConfirmationVisible == value) return;
            isCloseConfirmationVisible = value;
            Raise(nameof(IsCloseConfirmationVisible));
        }
    }

    public bool CanOpenConfigurationFolder => openConfigurationFolderAsync is not null;

    public SourceMappingApplyResult? ApplyResult
    {
        get => applyResult;
        private set
        {
            if (applyResult == value) return;
            applyResult = value;
            Raise(nameof(ApplyResult));
            Raise(nameof(IsApplyResultVisible));
        }
    }

    public bool IsApplyResultVisible => ApplyResult is not null;

    public void AcknowledgeApplyResult() => ApplyResult = null;

    public bool IsRestoreDefaultConfirmationVisible
    {
        get => isRestoreDefaultConfirmationVisible;
        private set
        {
            if (isRestoreDefaultConfirmationVisible == value) return;
            isRestoreDefaultConfirmationVisible = value;
            Raise(nameof(IsRestoreDefaultConfirmationVisible));
        }
    }

    public async Task<bool> OpenConfigurationFolderAsync(CancellationToken cancellationToken)
    {
        if (openConfigurationFolderAsync is null)
        {
            return false;
        }

        try
        {
            await openConfigurationFolderAsync(cancellationToken).ConfigureAwait(false);
            SourceMappingEditorLog.ConfigurationFolderOpened(logger, Document.AdapterId);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await postStateAsync(() => ValidationError = exception.GetType().Name).ConfigureAwait(false);
            SourceMappingEditorLog.ConfigurationFolderOpenFailed(
                logger,
                Document.AdapterId,
                exception.GetType().Name);
            return false;
        }
    }

    public SourceMappingOutputDocument SelectedOutput
    {
        get => selectedOutput;
        set
        {
            if (value is null || value == selectedOutput) return;
            SaveCurrentFormula();
            selectedOutput = value;
            formula = value.Formula;
            IsDeleteConfirmationVisible = false;
            Raise(nameof(SelectedOutput));
            Raise(nameof(SelectedOutputItem));
            Raise(nameof(Formula));
            Raise(nameof(EditorState));
            RaiseSelectedMetadata();
        }
    }

    public SourceMappingOutputItem? SelectedOutputItem
    {
        get => outputItems.FirstOrDefault(item =>
            StringComparer.Ordinal.Equals(item.ParameterId, selectedOutput.ParameterId));
        set
        {
            if (value is not null)
            {
                SelectedOutput = value.Output;
            }
        }
    }

    public string SearchText
    {
        get => searchText;
        set
        {
            searchText = value ?? string.Empty;
            Raise(nameof(SearchText));
            Raise(nameof(FilteredInputs));
        }
    }

    public string Formula
    {
        get => formula;
        set
        {
            string replacement = value ?? string.Empty;
            if (formula == replacement) return;
            formula = replacement;
            ValidationError = null;
            Raise(nameof(Formula));
            QueueValidation();
            UpdateDirtyState();
        }
    }

    public double NeutralValue
    {
        get => selectedOutput.NeutralValue;
        set
        {
            if (!IsSelectedBuiltInParameter)
            {
                UpdateSelected(selectedOutput with { NeutralValue = value });
            }
        }
    }

    public double SuggestedMinimum
    {
        get => selectedOutput.SuggestedMinimum;
        set
        {
            if (!IsSelectedBuiltInParameter)
            {
                UpdateSelected(selectedOutput with { SuggestedMinimum = value });
            }
        }
    }

    public double SuggestedMaximum
    {
        get => selectedOutput.SuggestedMaximum;
        set
        {
            if (!IsSelectedBuiltInParameter)
            {
                UpdateSelected(selectedOutput with { SuggestedMaximum = value });
            }
        }
    }

    public double Smoothing
    {
        get => selectedOutput.Smoothing;
        set => UpdateSelected(selectedOutput with { Smoothing = value });
    }

    public bool IsDeleteConfirmationVisible
    {
        get => isDeleteConfirmationVisible;
        private set
        {
            if (isDeleteConfirmationVisible == value) return;
            isDeleteConfirmationVisible = value;
            Raise(nameof(IsDeleteConfirmationVisible));
        }
    }

    public string NewParameterId
    {
        get => newParameterId;
        set { newParameterId = value ?? string.Empty; Raise(nameof(NewParameterId)); }
    }

    public string? ValidationError
    {
        get => validationError;
        private set { validationError = value; Raise(nameof(ValidationError)); }
    }

    public bool Validate()
    {
        SaveCurrentFormula();
        try
        {
            CompiledSourceFormulaProgram program = SourceFormulaCompiler.Compile(Document.ToFormulaProfile());
            _ = SourceFormulaRegistryBuilder.Create(StandardParameterCatalog.Definitions, [program]);
            ValidationError = null;
            return true;
        }
        catch (SourceFormulaCompilationException exception)
        {
            ValidationError = exception.Code.ToString();
            return false;
        }
        catch (ArgumentException exception)
        {
            ValidationError = exception.GetType().Name;
            return false;
        }
    }

    public bool FormatSelectedFormula()
    {
        try
        {
            Formula = SourceFormulaFormatter.Format(Formula);
            SourceMappingEditorLog.FormulaFormatted(
                logger,
                Document.AdapterId,
                selectedOutput.ParameterId);
            return true;
        }
        catch (SourceFormulaCompilationException exception)
        {
            ValidationError = exception.Code.ToString();
            SourceMappingEditorLog.FormulaFormatFailed(
                logger,
                Document.AdapterId,
                selectedOutput.ParameterId,
                exception.Code.ToString());
            return false;
        }
    }

    public async Task<bool> ApplyAsync(CancellationToken cancellationToken)
    {
        if (applyAsync is null)
        {
            await postStateAsync(() =>
            {
                ApplyValidationErrors = [];
                ApplyResult = SourceMappingApplyResult.Unavailable;
            })
                .ConfigureAwait(false);
            return false;
        }

        if (!await ValidateAsync(cancellationToken).ConfigureAwait(false))
        {
            await postStateAsync(() =>
            {
                ApplyValidationErrors = CreateApplyValidationErrors(ValidationReport);
                ApplyResult = SourceMappingApplyResult.ValidationFailed;
            })
                .ConfigureAwait(false);
            return false;
        }

        long started = Stopwatch.GetTimestamp();
        try
        {
            await applyAsync(Document, cancellationToken).ConfigureAwait(false);
            pendingReferenceRenames = appliedBaseline.Outputs.Length == Document.Outputs.Length
                ? appliedBaseline.Outputs.Zip(Document.Outputs)
                    .Where(static pair => !StringComparer.Ordinal.Equals(pair.First.ParameterId, pair.Second.ParameterId))
                    .Select(static pair => (pair.First.ParameterId, pair.Second.ParameterId)).ToImmutableArray()
                : [];
            SourceMappingEditorLog.Applied(
                logger,
                Document.ProfileId,
                Document.AdapterId,
                Document.Outputs.Length,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            appliedBaseline = Document;
            IsReferenceSyncConfirmationVisible = !pendingReferenceRenames.IsEmpty && synchronizeReferencesAsync is not null;
            UpdateDirtyState();
            await postStateAsync(() =>
            {
                ApplyValidationErrors = [];
                ApplyResult = SourceMappingApplyResult.Success;
            })
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SourceMappingApplyResult result = exception switch
            {
                UnauthorizedAccessException => SourceMappingApplyResult.PermissionDenied,
                IOException => SourceMappingApplyResult.StorageFailure,
                _ => SourceMappingApplyResult.UnexpectedFailure,
            };
            await postStateAsync(() =>
            {
                ApplyValidationErrors = [];
                ValidationError = exception.GetType().Name;
                ApplyResult = result;
            }).ConfigureAwait(false);
            SourceMappingEditorLog.ApplyFailed(
                logger,
                exception,
                Document.ProfileId,
                Document.AdapterId,
                result.ToString(),
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return false;
        }
    }

    public async Task ConfirmReferenceSyncAsync(CancellationToken cancellationToken)
    {
        if (!IsReferenceSyncConfirmationVisible || synchronizeReferencesAsync is null) return;
        await synchronizeReferencesAsync(pendingReferenceRenames, cancellationToken).ConfigureAwait(false);
        pendingReferenceRenames = [];
        IsReferenceSyncConfirmationVisible = false;
    }

    public void SkipReferenceSync()
    {
        pendingReferenceRenames = [];
        IsReferenceSyncConfirmationVisible = false;
    }

    public Task<bool> RequestCloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsDirty)
        {
            return Task.FromResult(true);
        }

        IsCloseConfirmationVisible = true;
        return Task.FromResult(false);
    }

    public bool DiscardAndConfirmClose()
    {
        if (!IsCloseConfirmationVisible)
        {
            return false;
        }

        IsCloseConfirmationVisible = false;
        SourceMappingEditorLog.Discarded(
            logger,
            Document.ProfileId,
            Document.AdapterId,
            Document.Outputs.Length);
        return true;
    }

    public void CancelClose() => IsCloseConfirmationVisible = false;

    public async Task<bool> ValidateAsync(CancellationToken cancellationToken)
    {
        SaveCurrentFormula();
        long started = Stopwatch.GetTimestamp();
        SourceMappingValidationReport report = await validateAsync(Document, cancellationToken)
            .ConfigureAwait(false);
        await postStateAsync(() => ValidationReport = report).ConfigureAwait(false);
        LogValidation(report, started);
        return report.Diagnostics.IsEmpty;
    }

    public async Task<bool> ImportAsDraftAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (importAsync is null)
        {
            return false;
        }

        try
        {
            SourceMappingProfileDocument imported = await importAsync(path, cancellationToken)
                .ConfigureAwait(false);
            await postStateAsync(() => ReplaceDraft(imported)).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await postStateAsync(() => ValidationError = exception.GetType().Name).ConfigureAwait(false);
            return false;
        }
    }

    public async Task<string?> SaveAsAsync(
        string name,
        CancellationToken cancellationToken)
    {
        if (saveAsAsync is null)
        {
            return null;
        }

        SaveCurrentFormula();
        try
        {
            return await saveAsAsync(Document, name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await postStateAsync(() => ValidationError = exception.GetType().Name).ConfigureAwait(false);
            return null;
        }
    }

    public async Task<bool> RestoreDefaultAsDraftAsync(CancellationToken cancellationToken)
    {
        if (restoreDefaultAsync is null)
        {
            return false;
        }

        try
        {
            SourceMappingProfileDocument restored = await restoreDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            await postStateAsync(() => ReplaceDraft(restored)).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await postStateAsync(() => ValidationError = exception.GetType().Name).ConfigureAwait(false);
            return false;
        }
    }

    public void RequestRestoreDefault()
    {
        if (restoreDefaultAsync is not null)
        {
            IsRestoreDefaultConfirmationVisible = true;
        }
    }

    public void CancelRestoreDefault() => IsRestoreDefaultConfirmationVisible = false;

    public async Task<bool> ConfirmRestoreDefaultAsync(CancellationToken cancellationToken)
    {
        if (!IsRestoreDefaultConfirmationVisible)
        {
            return false;
        }

        IsRestoreDefaultConfirmationVisible = false;
        return await RestoreDefaultAsDraftAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool TryAddGlobalParameter(
        string parameterId,
        double neutralValue,
        double minimum,
        double maximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterId);
        if (Document.Outputs.Any(output => StringComparer.Ordinal.Equals(output.ParameterId, parameterId)))
        {
            return false;
        }

        var output = new SourceMappingOutputDocument(
            parameterId,
            null,
            "0",
            neutralValue,
            minimum,
            maximum,
            0);
        Document = Document with { Outputs = Document.Outputs.Add(output) };
        selectedOutput = output;
        formula = output.Formula;
        Raise(nameof(Document));
        Raise(nameof(Outputs));
        RefreshOutputItems();
        Raise(nameof(SelectedOutput));
        Raise(nameof(Formula));
        RefreshCompletions();
        RaiseSelectedMetadata();
        UpdateDirtyState();
        return true;
    }

    public bool TryAddGlobalParameter()
    {
        if (string.IsNullOrWhiteSpace(NewParameterId)) return false;
        bool added = TryAddGlobalParameter(
            NewParameterId,
            0,
            -1,
            1);
        if (added) NewParameterId = string.Empty;
        return added;
    }

    public void RequestDeleteSelected()
    {
        if (!CanDeleteSelectedParameter)
        {
            return;
        }

        if (Outputs.Length <= 1)
        {
            ValidationError = "LastOutputRequired";
            return;
        }

        IsDeleteConfirmationVisible = true;
    }

    public void CancelDeleteSelected() => IsDeleteConfirmationVisible = false;

    public bool ConfirmDeleteSelected()
    {
        if (!IsDeleteConfirmationVisible
            || !CanDeleteSelectedParameter
            || Outputs.Length <= 1)
        {
            return false;
        }
        int removedIndex = Document.Outputs.IndexOf(selectedOutput);
        if (removedIndex < 0) return false;
        ImmutableArray<SourceMappingOutputDocument> remaining = Document.Outputs.RemoveAt(removedIndex);
        Document = Document with { Outputs = remaining };
        selectedOutput = remaining[Math.Min(removedIndex, remaining.Length - 1)];
        formula = selectedOutput.Formula;
        IsDeleteConfirmationVisible = false;
        ValidationError = null;
        Raise(nameof(Document));
        Raise(nameof(Outputs));
        RefreshOutputItems();
        Raise(nameof(SelectedOutput));
        Raise(nameof(Formula));
        RefreshCompletions();
        RaiseSelectedMetadata();
        UpdateDirtyState();
        return true;
    }

    public void SetInputLocalizer(Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(localize);
        inputItems = inputs.Select(input => new SourceMappingInputItem(
            input,
            localize(input.DisplayNameResourceKey))).ToImmutableArray();
        Raise(nameof(FilteredInputs));
        RefreshCompletions();
    }

    public void SetParameterLocalizer(Func<string, string> localize)
    {
        parameterLocalizer = localize ?? throw new ArgumentNullException(nameof(localize));
        RefreshOutputItems();
        RefreshCompletions();
        Raise(nameof(SelectedSubtitle));
    }

    public bool TryRenameSelectedParameter(string newId)
    {
        SaveCurrentFormula();
        if (IsSelectedBuiltInParameter
            || !GlobalParameterId.IsValid(newId)
            || Document.Outputs.Any(output =>
                !ReferenceEquals(output, selectedOutput)
                && StringComparer.Ordinal.Equals(output.ParameterId, newId)))
        {
            Raise(nameof(SelectedParameterId));
            return false;
        }

        string oldId = selectedOutput.ParameterId;
        if (StringComparer.Ordinal.Equals(oldId, newId))
        {
            return true;
        }

        int selectedIndex = Document.Outputs.IndexOf(selectedOutput);
        ImmutableArray<SourceMappingOutputDocument> renamed = Document.Outputs
            .Select((output, index) => output with
            {
                ParameterId = index == selectedIndex ? newId : output.ParameterId,
                Formula = SourceFormulaIdentifierRewriter.Rename(output.Formula, oldId, newId),
            })
            .ToImmutableArray();
        Document = Document with { Outputs = renamed };
        selectedOutput = renamed[selectedIndex];
        formula = selectedOutput.Formula;
        Raise(nameof(Document));
        Raise(nameof(Outputs));
        RefreshOutputItems();
        Raise(nameof(SelectedOutput));
        Raise(nameof(SelectedParameterId));
        Raise(nameof(Formula));
        RefreshCompletions();
        UpdateDirtyState();
        return true;
    }

    private void SaveCurrentFormula()
    {
        int index = Document.Outputs.IndexOf(selectedOutput);
        if (index < 0 || selectedOutput.Formula == formula) return;
        selectedOutput = selectedOutput with { Formula = formula };
        Document = Document with { Outputs = Document.Outputs.SetItem(index, selectedOutput) };
        Raise(nameof(Document));
        Raise(nameof(Outputs));
        RefreshOutputItems();
        UpdateDirtyState();
    }

    private void ReplaceDraft(SourceMappingProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document = SourceMappingParameterCatalog.NormalizeBuiltIns(document);
        Document = document;
        selectedOutput = document.Outputs[0];
        formula = selectedOutput.Formula;
        IsDeleteConfirmationVisible = false;
        ValidationError = null;
        Raise(nameof(Document));
        Raise(nameof(Outputs));
        RefreshOutputItems();
        Raise(nameof(SelectedOutput));
        Raise(nameof(Formula));
        RaiseSelectedMetadata();
        RefreshCompletions();
        UpdateDirtyState();
    }

    private void UpdateSelected(SourceMappingOutputDocument replacement)
    {
        int index = Document.Outputs.IndexOf(selectedOutput);
        if (index < 0) return;
        replacement = replacement with { Formula = formula };
        if (replacement == selectedOutput) return;
        selectedOutput = replacement;
        Document = Document with { Outputs = Document.Outputs.SetItem(index, replacement) };
        Raise(nameof(Document));
        Raise(nameof(Outputs));
        RefreshOutputItems();
        Raise(nameof(SelectedOutput));
        RaiseSelectedMetadata();
        UpdateDirtyState();
    }

    private void RaiseSelectedMetadata()
    {
        Raise(nameof(NeutralValue));
        Raise(nameof(SuggestedMinimum));
        Raise(nameof(SuggestedMaximum));
        Raise(nameof(Smoothing));
        Raise(nameof(SelectedParameterId));
        Raise(nameof(SelectedSubtitle));
        Raise(nameof(IsSelectedBuiltInParameter));
        Raise(nameof(CanDeleteSelectedParameter));
        Raise(nameof(CanEditSelectedParameterMetadata));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        validationCancellation?.Cancel();
        validationCancellation?.Dispose();
        validationCancellation = null;
    }

    private void QueueValidation()
    {
        long version = Interlocked.Increment(ref validationVersion);
        validationCancellation?.Cancel();
        validationCancellation?.Dispose();
        validationCancellation = new CancellationTokenSource();
        SourceMappingProfileDocument snapshot = CreateFormulaSnapshot();
        ValidationTask = ValidateAfterDebounceAsync(
            snapshot,
            version,
            validationCancellation.Token);
    }

    private async Task ValidateAfterDebounceAsync(
        SourceMappingProfileDocument snapshot,
        long version,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(validationDebounce, cancellationToken).ConfigureAwait(false);
            long started = Stopwatch.GetTimestamp();
            SourceMappingValidationReport report = await validateAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
            if (version != Volatile.Read(ref validationVersion))
            {
                return;
            }

            await postStateAsync(() =>
            {
                if (version == Volatile.Read(ref validationVersion))
                {
                    ValidationReport = report;
                }
            }).ConfigureAwait(false);
            LogValidation(report, started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private SourceMappingProfileDocument CreateFormulaSnapshot()
    {
        int index = Document.Outputs.IndexOf(selectedOutput);
        if (index < 0 || selectedOutput.Formula == formula)
        {
            return Document;
        }

        return Document with
        {
            Outputs = Document.Outputs.SetItem(index, selectedOutput with { Formula = formula }),
        };
    }

    private void RefreshCompletions()
    {
        Completions = CreateCompletions(inputItems, Document.Outputs);
        Raise(nameof(Completions));
    }

    private ImmutableArray<FormulaCompletionItem> CreateCompletions(
        ImmutableArray<SourceMappingInputItem> inputs,
        ImmutableArray<SourceMappingOutputDocument> outputs)
    {
        var items = ImmutableArray.CreateBuilder<FormulaCompletionItem>(
            inputs.Length + outputs.Length + SourceFormulaLanguage.Functions.Length);
        items.AddRange(inputs.Select(static input => new FormulaCompletionItem(
            input.Id,
            input.Id,
            input.Category,
            input.Subtitle,
            null,
            FormulaCompletionKind.Input)));
        items.AddRange(outputs.Select(output => new FormulaCompletionItem(
            output.ParameterId,
            output.ParameterId,
            "Output",
            GetOutputSubtitle(output) ?? output.ParameterId,
            null,
            FormulaCompletionKind.Output)));
        items.AddRange(SourceFormulaLanguage.Functions.Select(static function =>
            new FormulaCompletionItem(
                function.Name,
                function.Template,
                "Function",
                function.Template,
                null,
                FormulaCompletionKind.Function)));
        return items.ToImmutable();
    }

    private ImmutableArray<SourceMappingOutputItem> CreateOutputItems(
        ImmutableArray<SourceMappingOutputDocument> outputs) => outputs
        .Select(output => new SourceMappingOutputItem(
            output,
            output.ParameterId,
            GetOutputSubtitle(output),
            SourceMappingParameterCatalog.IsBuiltIn(output.ParameterId)))
        .ToImmutableArray();

    private string? GetOutputSubtitle(SourceMappingOutputDocument output)
    {
        ParameterDefinition? definition = SourceMappingParameterCatalog.FindBuiltIn(
            output.ParameterId);
        return definition?.DisplayNameResourceKey is string resourceKey
            ? parameterLocalizer(resourceKey)
            : output.Subtitle;
    }

    private void RefreshOutputItems()
    {
        outputItems = CreateOutputItems(Document.Outputs);
        Raise(nameof(OutputItems));
        Raise(nameof(SelectedOutputItem));
    }

    private void UpdateDirtyState() => IsDirty = !DocumentsEqual(
        CreateFormulaSnapshot(),
        appliedBaseline);

    private static bool DocumentsEqual(
        SourceMappingProfileDocument left,
        SourceMappingProfileDocument right) =>
        left.SchemaVersion == right.SchemaVersion
        && StringComparer.Ordinal.Equals(left.ProfileId, right.ProfileId)
        && StringComparer.Ordinal.Equals(left.VendorId, right.VendorId)
        && StringComparer.Ordinal.Equals(left.TechnologyId, right.TechnologyId)
        && StringComparer.Ordinal.Equals(left.AdapterId, right.AdapterId)
        && StringComparer.Ordinal.Equals(left.Channel, right.Channel)
        && left.InputIds.SequenceEqual(right.InputIds, StringComparer.Ordinal)
        && left.Outputs.SequenceEqual(right.Outputs);

    private static Task<SourceMappingValidationReport> ValidateDocumentAsync(
        SourceMappingProfileDocument document,
        CancellationToken cancellationToken) => Task.Run(() =>
        {
            ImmutableArray<SourceFormulaValidationDiagnostic> diagnostics =
                SourceFormulaCompiler.Validate(document.ToFormulaProfile());
            var states = Enumerable.Repeat(
                FormulaEditorState.Empty,
                document.Outputs.Length).ToArray();
            ImmutableArray<SourceFormulaValidationDiagnostic> mappedDiagnostics = diagnostics
                .Select(diagnostic => diagnostic.OutputId is string outputId
                    ? diagnostic with
                    {
                        OutputIndex = document.Outputs
                            .Select((output, index) => (output, index))
                            .First(pair => StringComparer.Ordinal.Equals(
                                pair.output.ParameterId,
                                outputId))
                            .index,
                    }
                    : diagnostic)
                .ToImmutableArray();
            foreach (SourceFormulaValidationDiagnostic diagnostic in mappedDiagnostics)
            {
                if (diagnostic.OutputIndex >= 0 && diagnostic.OutputIndex < states.Length)
                {
                    states[diagnostic.OutputIndex] = new FormulaEditorState(
                        diagnostic.Diagnostic,
                        null);
                }
            }

            if (!mappedDiagnostics.IsEmpty)
            {
                return new SourceMappingValidationReport(states.ToImmutableArray(), mappedDiagnostics);
            }

            try
            {
                CompiledSourceFormulaProgram program = SourceFormulaCompiler.Compile(document.ToFormulaProfile());
                _ = SourceFormulaRegistryBuilder.Create(StandardParameterCatalog.Definitions, [program]);
                return SourceMappingValidationReport.Empty(document.Outputs.Length);
            }
            catch (SourceFormulaCompilationException exception)
            {
                var diagnostic = new SourceFormulaDiagnostic(
                        exception.Code,
                        exception.Start,
                        exception.Length,
                        exception.Message);
                return CreateGlobalValidationReport(document.Outputs.Length, diagnostic);
            }
            catch (ArgumentException exception)
            {
                var diagnostic = new SourceFormulaDiagnostic(
                        SourceFormulaErrorCode.InvalidDefinition,
                        0,
                        0,
                        exception.Message);
                return CreateGlobalValidationReport(document.Outputs.Length, diagnostic);
            }
        }, cancellationToken);

    private static Task PostToUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    private static SourceMappingValidationReport CreateGlobalValidationReport(
        int outputCount,
        SourceFormulaDiagnostic diagnostic) => new(
            SourceMappingValidationReport.Empty(outputCount).OutputStates,
            [new SourceFormulaValidationDiagnostic(-1, null, diagnostic)]);

    private ImmutableArray<SourceMappingApplyError> CreateApplyValidationErrors(
        SourceMappingValidationReport report) => report.Diagnostics.Select(diagnostic =>
        {
            string parameterId = diagnostic.OutputId
                ?? (diagnostic.OutputIndex >= 0 && diagnostic.OutputIndex < Document.Outputs.Length
                    ? Document.Outputs[diagnostic.OutputIndex].ParameterId
                    : Document.ProfileId);
            return new SourceMappingApplyError(parameterId, diagnostic.Diagnostic);
        }).ToImmutableArray();

    private void LogValidation(SourceMappingValidationReport report, long started)
    {
        double elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (report.Diagnostics.IsEmpty)
        {
            SourceMappingEditorLog.ValidationCompleted(
                logger,
                Document.ProfileId,
                Document.AdapterId,
                Document.Outputs.Length,
                elapsedMilliseconds);
        }
        else
        {
            foreach (SourceFormulaValidationDiagnostic diagnostic in report.Diagnostics)
            {
                SourceMappingEditorLog.ValidationFailed(
                    logger,
                    Document.ProfileId,
                    Document.AdapterId,
                    diagnostic.Diagnostic.Code.ToString(),
                    elapsedMilliseconds);
            }
        }
    }
}

internal static partial class SourceMappingEditorLog
{
    [LoggerMessage(6620, LogLevel.Debug,
        "Source mapping validation completed: profile={ProfileId}; adapter={AdapterId}; outputs={OutputCount}; elapsedMs={ElapsedMilliseconds}")]
    internal static partial void ValidationCompleted(
        ILogger logger,
        string profileId,
        string adapterId,
        int outputCount,
        double elapsedMilliseconds);

    [LoggerMessage(6621, LogLevel.Warning,
        "Source mapping validation failed: profile={ProfileId}; adapter={AdapterId}; error={ErrorCode}; elapsedMs={ElapsedMilliseconds}")]
    internal static partial void ValidationFailed(
        ILogger logger,
        string profileId,
        string adapterId,
        string errorCode,
        double elapsedMilliseconds);

    [LoggerMessage(6622, LogLevel.Information,
        "Source mapping applied: profile={ProfileId}; adapter={AdapterId}; outputs={OutputCount}; elapsedMs={ElapsedMilliseconds}")]
    internal static partial void Applied(
        ILogger logger,
        string profileId,
        string adapterId,
        int outputCount,
        double elapsedMilliseconds);

    [LoggerMessage(6623, LogLevel.Error,
        "Source mapping apply failed: profile={ProfileId}; adapter={AdapterId}; error={ErrorCode}; elapsedMs={ElapsedMilliseconds}")]
    internal static partial void ApplyFailed(
        ILogger logger,
        Exception exception,
        string profileId,
        string adapterId,
        string errorCode,
        double elapsedMilliseconds);

    [LoggerMessage(6624, LogLevel.Information,
        "Source mapping changes discarded: profile={ProfileId}; adapter={AdapterId}; outputs={OutputCount}")]
    internal static partial void Discarded(
        ILogger logger,
        string profileId,
        string adapterId,
        int outputCount);

    [LoggerMessage(6625, LogLevel.Information,
        "Source mapping configuration folder opened: adapter={AdapterId}")]
    internal static partial void ConfigurationFolderOpened(ILogger logger, string adapterId);

    [LoggerMessage(6626, LogLevel.Warning,
        "Source mapping configuration folder open failed: adapter={AdapterId}; error={ErrorCode}")]
    internal static partial void ConfigurationFolderOpenFailed(
        ILogger logger,
        string adapterId,
        string errorCode);

    [LoggerMessage(6627, LogLevel.Information,
        "Source mapping formula formatted: adapter={AdapterId}; parameter={ParameterId}")]
    internal static partial void FormulaFormatted(
        ILogger logger,
        string adapterId,
        string parameterId);

    [LoggerMessage(6628, LogLevel.Warning,
        "Source mapping formula format failed: adapter={AdapterId}; parameter={ParameterId}; error={ErrorCode}")]
    internal static partial void FormulaFormatFailed(
        ILogger logger,
        string adapterId,
        string parameterId,
        string errorCode);
}
