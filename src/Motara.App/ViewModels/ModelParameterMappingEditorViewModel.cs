using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Models;
using Motara.App.Parameters;
using Motara.App.Shell;
using Motara.Core.Formulas;
using Motara.Core.Parameters;

namespace Motara.App.ViewModels;

public enum ModelParameterMappingApplyResult
{
    Success,
    ValidationFailed,
    StorageFailure,
}

public enum ModelParameterBindingInputError
{
    InvalidSyntax,
    UnknownGlobalParameter,
}

public sealed record GlobalParameterEditorItem(
    string Id,
    string? Subtitle,
    double Minimum = 0,
    double Default = 0,
    double Maximum = 0);

public sealed record ModelParameterEditorItem(
    string Id,
    string Name,
    double Minimum,
    double Default,
    double Maximum);

public sealed record ModelParameterBindingEditorItem(
    string SourceParameterId,
    string ModelParameterId,
    bool HasMissingGlobalParameter,
    bool HasMissingModelParameter)
{
    public bool HasIssue => HasMissingGlobalParameter || HasMissingModelParameter;
}

public sealed class ModelParameterMappingEditorViewModel : INotifyPropertyChanged, IWorkspaceCloseGuard
{
    private readonly Func<ModelParameterMappingDocument, CancellationToken, Task>? saveAsync;
    private readonly ILogger logger;
    private readonly ModelParameterObservationSource? observationSource;
    private readonly ModelParameterMappingDocument sourceDocument;
    private readonly Stack<ImmutableArray<ModelParameterSettingConfiguration>> undo = new();
    private ImmutableArray<ModelParameterSettingConfiguration> baseline;
    private ImmutableArray<ModelParameterSettingConfiguration> parameterSettings;
    private string globalSearchText = string.Empty;
    private string modelSearchText = string.Empty;
    private ModelParameterEditorItem? selectedModelParameter;
    private GlobalParameterEditorItem? selectedGlobalParameter;
    private bool isCloseConfirmationVisible;
    private string currentInputValueText = "--";
    private string currentOutputValueText = "--";
    private string bindingInputText = string.Empty;
    private bool inputMinimumMissing;
    private bool inputMaximumMissing;
    private bool outputMinimumMissing;
    private bool outputMaximumMissing;

    internal ModelParameterMappingEditorViewModel(
        ModelParameterMappingDocument document,
        Func<ModelParameterMappingDocument, CancellationToken, Task>? saveAsync = null,
        ILogger? logger = null,
        ModelParameterObservationSource? observationSource = null,
        IEnumerable<SourceMappingOutputDocument>? sourceOutputs = null,
        Func<string, string>? parameterLocalizer = null,
        bool isExternalOutputMapping = false)
    {
        sourceDocument = document ?? throw new ArgumentNullException(nameof(document));
        this.saveAsync = saveAsync;
        this.logger = logger ?? NullLogger.Instance;
        this.observationSource = observationSource;
        IsExternalOutputMapping = isExternalOutputMapping;
        baseline = document.ParameterSettings;
        parameterSettings = baseline;
        Func<string, string> localize = parameterLocalizer ?? (static key => key);
        IEnumerable<GlobalParameterEditorItem> builtIns = StandardParameterCatalog.Definitions
            .Select(definition => new GlobalParameterEditorItem(
                definition.Id,
                definition.DisplayNameResourceKey is string resourceKey
                    ? localize(resourceKey)
                    : definition.Id,
                definition.SuggestedMinimum,
                definition.NeutralValue,
                definition.SuggestedMaximum));
        IEnumerable<GlobalParameterEditorItem> extensions = (sourceOutputs ?? [])
            .Where(output => !SourceMappingParameterCatalog.IsBuiltIn(output.ParameterId))
            .Select(output => new GlobalParameterEditorItem(
                output.ParameterId,
                output.Subtitle,
                output.SuggestedMinimum,
                output.NeutralValue,
                output.SuggestedMaximum));
        GlobalParameters = builtIns
            .Concat(extensions)
            .DistinctBy(static parameter => parameter.Id, StringComparer.Ordinal)
            .ToImmutableArray();
        ModelParameters = document.Capabilities.Parameters
            .Select(parameter => new ModelParameterEditorItem(
                parameter.Id,
                parameter.Name ?? localize("Workspace.ModelMapping.ParameterNameUnset"),
                parameter.Minimum,
                parameter.Default,
                parameter.Maximum))
            .ToImmutableArray();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ImmutableArray<GlobalParameterEditorItem> GlobalParameters { get; }

    public ImmutableArray<ModelParameterEditorItem> ModelParameters { get; }

    public bool IsExternalOutputMapping { get; }

    internal ImmutableArray<ModelParameterSettingConfiguration> ParameterSettings => parameterSettings;

    internal ImmutableArray<ModelParameterBindingConfiguration> ParameterBindings => parameterSettings
        .Where(static setting => setting.GlobalParameterId is not null)
        .Select(static setting => new ModelParameterBindingConfiguration(
            setting.GlobalParameterId!,
            setting.ModelParameterId))
        .ToImmutableArray();

    public ImmutableArray<ModelParameterBindingEditorItem> BindingItems => parameterSettings
        .Where(static setting => setting.GlobalParameterId is not null)
        .Select(setting => new ModelParameterBindingEditorItem(
            setting.GlobalParameterId!,
            setting.ModelParameterId,
            !GlobalParameters.Any(item => StringComparer.Ordinal.Equals(item.Id, setting.GlobalParameterId)),
            !ModelParameters.Any(item => StringComparer.Ordinal.Equals(item.Id, setting.ModelParameterId))))
        .ToImmutableArray();

    public IEnumerable<GlobalParameterEditorItem> FilteredGlobalParameters =>
        Filter(GlobalParameters, globalSearchText, static item => item.Id, static item => item.Subtitle);

    public IEnumerable<ModelParameterEditorItem> FilteredModelParameters =>
        string.IsNullOrWhiteSpace(modelSearchText)
            ? ModelParameters
            : ModelParameters.Where(item => item.Id.Contains(modelSearchText, StringComparison.OrdinalIgnoreCase));

    public string GlobalSearchText
    {
        get => globalSearchText;
        set
        {
            if (!Set(ref globalSearchText, value ?? string.Empty)) return;
            Raise(nameof(FilteredGlobalParameters));
        }
    }

    public string ModelSearchText
    {
        get => modelSearchText;
        set
        {
            if (!Set(ref modelSearchText, value ?? string.Empty)) return;
            Raise(nameof(FilteredModelParameters));
        }
    }

    public GlobalParameterEditorItem? SelectedGlobalParameter
    {
        get => selectedGlobalParameter;
        set => Set(ref selectedGlobalParameter, value);
    }

    public ModelParameterEditorItem? SelectedModelParameter
    {
        get => selectedModelParameter;
        set
        {
            if (!Set(ref selectedModelParameter, value)) return;
            ResetRangeInputErrors();
            SynchronizeBindingInput();
            RaiseSelectedProperties();
        }
    }

    public string? SelectedGlobalParameterId => SelectedSetting?.GlobalParameterId;

    public string BindingInputText
    {
        get => bindingInputText;
        set
        {
            if (!Set(ref bindingInputText, value ?? string.Empty)) return;
            Raise(nameof(BindingInputError));
            Raise(nameof(IsDirty));
        }
    }

    public ModelParameterBindingInputError? BindingInputError
    {
        get
        {
            string candidate = bindingInputText.Trim();
            if (string.IsNullOrEmpty(candidate))
            {
                return null;
            }

            if (!GlobalParameterId.IsValid(candidate))
            {
                return ModelParameterBindingInputError.InvalidSyntax;
            }

            return GlobalParameters.Any(item => StringComparer.Ordinal.Equals(item.Id, candidate))
                ? null
                : ModelParameterBindingInputError.UnknownGlobalParameter;
        }
    }

    public bool HasSelectedIssue => SelectedSetting?.GlobalParameterId is { } id
        && !GlobalParameters.Any(item => StringComparer.Ordinal.Equals(item.Id, id));

    public double? InputMinimum
    {
        get => inputMinimumMissing ? null : SelectedSetting?.InputMinimum ?? 0;
        set => SetRangeValue(
            ref inputMinimumMissing,
            value,
            nameof(InputMinimum),
            (setting, number) => setting with { InputMinimum = number });
    }

    public double? InputMaximum
    {
        get => inputMaximumMissing ? null : SelectedSetting?.InputMaximum ?? 0;
        set => SetRangeValue(
            ref inputMaximumMissing,
            value,
            nameof(InputMaximum),
            (setting, number) => setting with { InputMaximum = number });
    }

    public double? OutputMinimum
    {
        get => outputMinimumMissing ? null : SelectedSetting?.OutputMinimum ?? 0;
        set => SetRangeValue(
            ref outputMinimumMissing,
            value,
            nameof(OutputMinimum),
            (setting, number) => setting with { OutputMinimum = number });
    }

    public double? OutputMaximum
    {
        get => outputMaximumMissing ? null : SelectedSetting?.OutputMaximum ?? 0;
        set => SetRangeValue(
            ref outputMaximumMissing,
            value,
            nameof(OutputMaximum),
            (setting, number) => setting with { OutputMaximum = number });
    }

    public bool ClampInput
    {
        get => SelectedSetting?.ClampInput ?? false;
        set => UpdateSelected(setting => setting with { ClampInput = value });
    }

    public bool ClampOutput
    {
        get => SelectedSetting?.ClampOutput ?? false;
        set => UpdateSelected(setting => setting with { ClampOutput = value });
    }

    public bool EnableAutoBlink
    {
        get => SelectedSetting?.EnableAutoBlink ?? false;
        set => UpdateSelected(setting => setting with { EnableAutoBlink = value });
    }

    public bool EnableAutoBreath
    {
        get => SelectedSetting?.EnableAutoBreath ?? false;
        set => UpdateSelected(setting => setting with { EnableAutoBreath = value });
    }

    private GlobalParameterEditorItem? BoundGlobalParameter => SelectedGlobalParameterId is { } id
        ? GlobalParameters.FirstOrDefault(parameter => StringComparer.Ordinal.Equals(parameter.Id, id))
        : null;

    public double? InputDeclaredMinimum => BoundGlobalParameter?.Minimum;

    public double? InputDeclaredDefault => BoundGlobalParameter?.Default;

    public double? InputDeclaredMaximum => BoundGlobalParameter?.Maximum;

    public double? OutputDeclaredMinimum => SelectedModelParameter?.Minimum;

    public double? OutputDeclaredDefault => SelectedModelParameter?.Default;

    public double? OutputDeclaredMaximum => SelectedModelParameter?.Maximum;

    public bool HasRangeInputError => inputMinimumMissing
        || inputMaximumMissing
        || outputMinimumMissing
        || outputMaximumMissing;

    public string CurrentInputValueText => currentInputValueText;

    public string CurrentOutputValueText => currentOutputValueText;

    internal void RefreshCurrentValues()
    {
        ModelParameterEditorItem? selected = SelectedModelParameter;
        if (selected is null
            || observationSource?.TryGet(
                sourceDocument.Model.Id,
                selected.Id,
                out ModelParameterObservation observation) != true)
        {
            SetCurrentValues("--", "--");
            return;
        }

        SetCurrentValues(FormatValue(observation.InputValue), FormatValue(observation.OutputValue));
    }

    public bool HasSelection => SelectedModelParameter is not null;

    public bool IsDirty => !parameterSettings.SequenceEqual(baseline)
        || HasRangeInputError
        || HasPendingBindingDraft;

    private bool HasPendingBindingDraft => SelectedModelParameter is not null
        && !StringComparer.Ordinal.Equals(
            bindingInputText.Trim(),
            SelectedGlobalParameterId ?? string.Empty);

    public bool IsCloseConfirmationVisible
    {
        get => isCloseConfirmationVisible;
        private set => Set(ref isCloseConfirmationVisible, value);
    }

    public IEnumerable<GlobalParameterEditorItem> GetGlobalParameterCompletions(string? text)
    {
        string query = text?.Trim() ?? string.Empty;
        return string.IsNullOrEmpty(query)
            ? GlobalParameters
            : GlobalParameters.Where(item => item.Id.StartsWith(query, StringComparison.OrdinalIgnoreCase));
    }

    public bool TryBindGlobalParameterId(string? parameterId)
    {
        string candidate = parameterId?.Trim() ?? string.Empty;
        BindingInputText = candidate;
        if (SelectedModelParameter is null
            || BindingInputError is not null
            || string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        GlobalParameterEditorItem source = GlobalParameters.Single(item =>
            StringComparer.Ordinal.Equals(item.Id, candidate));
        ModelParameterEditorItem target = SelectedModelParameter;
        return UpdateSelected(setting => setting with
        {
            GlobalParameterId = candidate,
            InputMinimum = source.Minimum,
            InputMaximum = source.Maximum,
            OutputMinimum = target.Minimum,
            OutputMaximum = target.Maximum,
        });
    }

    public bool BindSelectedGlobalParameter() =>
        SelectedGlobalParameter is not null
        && TryBindGlobalParameterId(SelectedGlobalParameter.Id);

    public bool AddOrReplaceBinding() => BindSelectedGlobalParameter();

    public bool ClearSelectedBinding()
    {
        bool changed = UpdateSelected(setting => setting with { GlobalParameterId = null });
        if (changed)
        {
            SynchronizeBindingInput();
            ModelParameterMappingEditorLog.BindingCleared(
                logger,
                sourceDocument.Model.Id.Value,
                SelectedModelParameter!.Id);
        }

        return changed;
    }

    public bool RemoveBinding(ModelParameterBindingEditorItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ModelParameterEditorItem? previous = SelectedModelParameter;
        SelectedModelParameter = ModelParameters.FirstOrDefault(parameter =>
            StringComparer.Ordinal.Equals(parameter.Id, item.ModelParameterId));
        bool changed = ClearSelectedBinding();
        SelectedModelParameter = previous;
        return changed;
    }

    public bool AutoMatchStandardParameters()
    {
        Dictionary<string, GlobalParameterEditorItem> sources = GlobalParameters
            .ToDictionary(static definition => definition.Id, StringComparer.Ordinal);
        Dictionary<string, ModelParameterEditorItem> targets = ModelParameters
            .ToDictionary(static parameter => parameter.Id, StringComparer.Ordinal);
        ImmutableArray<ModelParameterSettingConfiguration> next = StandardModelParameterMappings.All
            .Where(mapping => targets.ContainsKey(mapping.ModelParameterId))
            .Select(mapping => CreateSetting(
                targets[mapping.ModelParameterId],
                mapping.SourceParameterId,
                sources[mapping.SourceParameterId]))
            .ToImmutableArray();
        return ReplaceDraft(next);
    }

    public bool Undo()
    {
        if (!undo.TryPop(out ImmutableArray<ModelParameterSettingConfiguration> previous)) return false;
        parameterSettings = previous;
        RaiseDraftChanged();
        return true;
    }

    public async Task<ModelParameterMappingApplyResult> ApplyAsync(CancellationToken cancellationToken)
    {
        if (saveAsync is null) return ModelParameterMappingApplyResult.StorageFailure;
        if (HasRangeInputError) return ModelParameterMappingApplyResult.ValidationFailed;
        if (!TryCommitBindingInput()) return ModelParameterMappingApplyResult.ValidationFailed;
        try
        {
            var document = sourceDocument with
            {
                ParameterSettings = parameterSettings,
                BindingIssues = CreateIssues(parameterSettings),
                WasGenerated = false,
            };
            await saveAsync(document, cancellationToken);
            baseline = parameterSettings;
            undo.Clear();
            Raise(nameof(IsDirty));
            ModelParameterMappingEditorLog.Applied(logger, document.Model.Id.Value, parameterSettings.Length);
            return ModelParameterMappingApplyResult.Success;
        }
        catch (ArgumentException exception)
        {
            ModelParameterMappingEditorLog.ApplyFailed(logger, exception, sourceDocument.Model.Id.Value);
            return ModelParameterMappingApplyResult.ValidationFailed;
        }
        catch (IOException exception)
        {
            ModelParameterMappingEditorLog.ApplyFailed(logger, exception, sourceDocument.Model.Id.Value);
            return ModelParameterMappingApplyResult.StorageFailure;
        }
    }

    public Task<bool> RequestCloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsDirty) return Task.FromResult(true);
        IsCloseConfirmationVisible = true;
        return Task.FromResult(false);
    }

    public void CancelClose() => IsCloseConfirmationVisible = false;

    public void DiscardAndClose()
    {
        parameterSettings = baseline;
        undo.Clear();
        IsCloseConfirmationVisible = false;
        SynchronizeBindingInput();
        RaiseDraftChanged();
    }

    private ModelParameterSettingConfiguration? SelectedSetting => SelectedModelParameter is null
        ? null
        : parameterSettings.FirstOrDefault(setting => StringComparer.Ordinal.Equals(
            setting.ModelParameterId,
            SelectedModelParameter.Id));

    private void SetCurrentValues(string input, string output)
    {
        if (currentInputValueText != input)
        {
            currentInputValueText = input;
            Raise(nameof(CurrentInputValueText));
        }

        if (currentOutputValueText != output)
        {
            currentOutputValueText = output;
            Raise(nameof(CurrentOutputValueText));
        }
    }

    private static string FormatValue(double? value) => value is double number
        ? number.ToString("0.###", CultureInfo.InvariantCulture)
        : "--";

    private bool UpdateSelected(
        Func<ModelParameterSettingConfiguration, ModelParameterSettingConfiguration> update)
    {
        if (SelectedModelParameter is null) return false;
        ModelParameterSettingConfiguration current = SelectedSetting
            ?? CreateSetting(SelectedModelParameter, null, null);
        ModelParameterSettingConfiguration next = update(current);
        ImmutableArray<ModelParameterSettingConfiguration> draft = parameterSettings
            .Where(setting => !StringComparer.Ordinal.Equals(
                setting.ModelParameterId,
                SelectedModelParameter.Id))
            .Append(next)
            .ToImmutableArray();
        return ReplaceDraft(draft);
    }

    private bool ReplaceDraft(ImmutableArray<ModelParameterSettingConfiguration> next)
    {
        if (next.SequenceEqual(parameterSettings)) return false;
        undo.Push(parameterSettings);
        parameterSettings = next;
        RaiseDraftChanged();
        return true;
    }

    private ImmutableArray<ModelParameterMappingIssue> CreateIssues(
        ImmutableArray<ModelParameterSettingConfiguration> settings) => settings
        .SelectMany(setting =>
        {
            var issues = new List<ModelParameterMappingIssue>();
            if (setting.GlobalParameterId is { } id
                && !GlobalParameters.Any(item => StringComparer.Ordinal.Equals(item.Id, id)))
            {
                issues.Add(new(ModelParameterMappingIssueCode.MissingSoftwareParameter, id, setting.ModelParameterId));
            }

            if (!ModelParameters.Any(item => StringComparer.Ordinal.Equals(item.Id, setting.ModelParameterId)))
            {
                issues.Add(new(ModelParameterMappingIssueCode.MissingModelParameter,
                    setting.GlobalParameterId ?? string.Empty,
                    setting.ModelParameterId));
            }

            return issues;
        })
        .ToImmutableArray();

    private bool TryCommitBindingInput()
    {
        if (SelectedModelParameter is null)
        {
            return true;
        }

        string candidate = bindingInputText.Trim();
        if (BindingInputError is not null)
        {
            return false;
        }

        return string.IsNullOrEmpty(candidate)
            ? SelectedGlobalParameterId is null || ClearSelectedBinding()
            : StringComparer.Ordinal.Equals(SelectedGlobalParameterId, candidate)
                || TryBindGlobalParameterId(candidate);
    }

    private void SynchronizeBindingInput()
    {
        if (!Set(
            ref bindingInputText,
            SelectedGlobalParameterId ?? string.Empty,
            nameof(BindingInputText)))
        {
            return;
        }

        Raise(nameof(BindingInputError));
        Raise(nameof(IsDirty));
    }

    private static ModelParameterSettingConfiguration CreateSetting(
        ModelParameterEditorItem target,
        string? globalId,
        GlobalParameterEditorItem? source) => new(
            target.Id,
            globalId,
            source?.Minimum ?? target.Minimum,
            source?.Maximum ?? target.Maximum,
            target.Minimum,
            target.Maximum,
            ClampInput: false,
            ClampOutput: false,
            EnableAutoBlink: target.Id is "ParamEyeLOpen" or "ParamEyeROpen",
            EnableAutoBreath: target.Id == "ParamBreath");

    private static IEnumerable<T> Filter<T>(
        IEnumerable<T> source,
        string search,
        Func<T, string> id,
        Func<T, string?> subtitle) => string.IsNullOrWhiteSpace(search)
            ? source
            : source.Where(item => id(item).Contains(search, StringComparison.OrdinalIgnoreCase)
                || (subtitle(item)?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));

    private void RaiseDraftChanged()
    {
        Raise(nameof(ParameterSettings));
        Raise(nameof(ParameterBindings));
        Raise(nameof(BindingItems));
        Raise(nameof(IsDirty));
        RaiseSelectedProperties();
    }

    private void RaiseSelectedProperties()
    {
        Raise(nameof(SelectedGlobalParameterId));
        Raise(nameof(HasSelectedIssue));
        Raise(nameof(InputMinimum));
        Raise(nameof(InputMaximum));
        Raise(nameof(OutputMinimum));
        Raise(nameof(OutputMaximum));
        Raise(nameof(ClampInput));
        Raise(nameof(ClampOutput));
        Raise(nameof(EnableAutoBlink));
        Raise(nameof(EnableAutoBreath));
        Raise(nameof(InputDeclaredMinimum));
        Raise(nameof(InputDeclaredDefault));
        Raise(nameof(InputDeclaredMaximum));
        Raise(nameof(OutputDeclaredMinimum));
        Raise(nameof(OutputDeclaredDefault));
        Raise(nameof(OutputDeclaredMaximum));
        Raise(nameof(HasRangeInputError));
        Raise(nameof(CurrentInputValueText));
        Raise(nameof(CurrentOutputValueText));
        Raise(nameof(HasSelection));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(propertyName);
        return true;
    }

    private void SetRangeValue(
        ref bool isMissing,
        double? value,
        string propertyName,
        Func<ModelParameterSettingConfiguration, double, ModelParameterSettingConfiguration> update)
    {
        if (value is null)
        {
            if (!isMissing)
            {
                isMissing = true;
                Raise(propertyName);
                Raise(nameof(HasRangeInputError));
                Raise(nameof(IsDirty));
            }

            return;
        }

        bool wasMissing = isMissing;
        isMissing = false;
        bool changed = UpdateSelected(setting => update(setting, value.Value));
        if (wasMissing && !changed)
        {
            Raise(propertyName);
        }

        if (wasMissing)
        {
            Raise(nameof(HasRangeInputError));
            Raise(nameof(IsDirty));
        }
    }

    private void ResetRangeInputErrors()
    {
        inputMinimumMissing = false;
        inputMaximumMissing = false;
        outputMinimumMissing = false;
        outputMaximumMissing = false;
    }

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal static partial class ModelParameterMappingEditorLog
{
    [LoggerMessage(6600, LogLevel.Information,
        "Model parameter settings applied for {ModelId} with {SettingCount} settings")]
    internal static partial void Applied(ILogger logger, string modelId, int settingCount);

    [LoggerMessage(6601, LogLevel.Warning,
        "Model parameter settings apply failed for {ModelId}")]
    internal static partial void ApplyFailed(ILogger logger, Exception exception, string modelId);

    [LoggerMessage(6602, LogLevel.Information,
        "Model parameter binding cleared for {ModelId} parameter {ModelParameterId}")]
    internal static partial void BindingCleared(
        ILogger logger,
        string modelId,
        string modelParameterId);
}
