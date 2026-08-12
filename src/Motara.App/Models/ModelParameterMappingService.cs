using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.ViewModels;
using Motara.Core.Parameters;
using Motara.ModelRuntime.Abstractions;

namespace Motara.App.Models;

internal sealed class ModelParameterMappingService
{
    private readonly Func<ModelCatalogViewModel.ModelCatalogEntryViewModel, MotaraModelConfigurationStore>
        storeFactory;
    private readonly ILogger<ModelParameterMappingService> logger;

    internal ModelParameterMappingService(ILogger<ModelParameterMappingService>? logger = null)
        : this(static model => new(model.RootPath, model.DisplayName), logger)
    {
    }

    private ModelParameterMappingService(
        Func<ModelCatalogViewModel.ModelCatalogEntryViewModel, MotaraModelConfigurationStore> storeFactory,
        ILogger<ModelParameterMappingService>? logger)
    {
        this.storeFactory = storeFactory;
        this.logger = logger ?? NullLogger<ModelParameterMappingService>.Instance;
    }

    internal async Task<ModelParameterMappingDocument> LoadAsync(
        ModelCatalogViewModel.ModelCatalogEntryViewModel model,
        ModelCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(capabilities);

        MotaraModelConfigurationStore store = storeFactory(model);
        MotaraModelConfiguration? configuration;
        try
        {
            configuration = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            ModelParameterMappingServiceLog.InvalidConfigurationDetected(
                logger,
                exception.GetType().Name,
                model.Id.Value);
            configuration = null;
        }
        bool generated = configuration is null;
        if (configuration is null)
        {
            configuration = CreateDefaultConfiguration(model, capabilities);
            ModelParameterMappingServiceLog.DefaultConfigurationPrepared(
                logger,
                model.Id.Value);
        }

        return CreateDocument(model, configuration.ParameterSettings, capabilities, generated);
    }

    internal async Task SaveAsync(
        ModelParameterMappingDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateUniqueTargets(document.ParameterSettings);
        MotaraModelConfigurationStore store = storeFactory(document.Model);
        MotaraModelConfiguration? existing = await store.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        MotaraModelConfiguration updated = MotaraModelConfiguration.Create(
            document.Model.Id.Value,
            document.ParameterSettings,
            existing?.SourceMappingSelections ?? [],
            existing?.CollaborationModelInstanceId,
            existing?.Physics);
        await store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    internal async Task RepairAsync(
        ModelCatalogViewModel.ModelCatalogEntryViewModel model,
        ModelCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(capabilities);
        MotaraModelConfiguration repaired = CreateDefaultConfiguration(model, capabilities);
        await storeFactory(model).SaveAsync(repaired, cancellationToken).ConfigureAwait(false);
        ModelParameterMappingServiceLog.InvalidConfigurationRepaired(logger, model.Id.Value);
    }

    private static ModelParameterMappingDocument CreateDocument(
        ModelCatalogViewModel.ModelCatalogEntryViewModel model,
        ImmutableArray<ModelParameterSettingConfiguration> settings,
        ModelCapabilities capabilities,
        bool generated)
    {
        ValidateUniqueTargets(settings);
        HashSet<string> sources = StandardParameterCatalog.Definitions
            .Select(static definition => definition.Id)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> targets = capabilities.Parameters
            .Select(static parameter => parameter.Id)
            .ToHashSet(StringComparer.Ordinal);
        var issues = ImmutableArray.CreateBuilder<ModelParameterMappingIssue>();
        foreach (ModelParameterSettingConfiguration setting in settings)
        {
            if (setting.GlobalParameterId is { } globalId && !sources.Contains(globalId))
            {
                issues.Add(new(
                    ModelParameterMappingIssueCode.MissingSoftwareParameter,
                    globalId,
                    setting.ModelParameterId));
            }

            if (!targets.Contains(setting.ModelParameterId))
            {
                issues.Add(new(
                    ModelParameterMappingIssueCode.MissingModelParameter,
                    setting.GlobalParameterId ?? string.Empty,
                    setting.ModelParameterId));
            }
        }

        return new(model, capabilities, settings, issues.ToImmutable(), generated);
    }

    private static MotaraModelConfiguration CreateDefaultConfiguration(
        ModelCatalogViewModel.ModelCatalogEntryViewModel model,
        ModelCapabilities capabilities)
    {
        HashSet<string> targets = capabilities.Parameters
            .Select(static parameter => parameter.Id)
            .ToHashSet(StringComparer.Ordinal);
        Dictionary<string, ParameterDefinition> definitions = StandardParameterCatalog.Definitions
            .ToDictionary(static definition => definition.Id, StringComparer.Ordinal);
        Dictionary<string, ModelParameter> parameters = capabilities.Parameters
            .ToDictionary(static parameter => parameter.Id, StringComparer.Ordinal);
        return MotaraModelConfiguration.Create(
            model.Id.Value,
            StandardModelParameterMappings.All
                .Where(mapping => targets.Contains(mapping.ModelParameterId))
                .Select(mapping => CreateDefaultSetting(
                    mapping,
                    definitions[mapping.SourceParameterId],
                    parameters[mapping.ModelParameterId])));
    }

    private static void ValidateUniqueTargets(
        ImmutableArray<ModelParameterSettingConfiguration> settings)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (ModelParameterSettingConfiguration setting in settings)
        {
            ArgumentNullException.ThrowIfNull(setting);
            if (!targets.Add(setting.ModelParameterId))
            {
                throw new ArgumentException($"Duplicate model parameter setting: {setting.ModelParameterId}");
            }
        }
    }

    private static ModelParameterSettingConfiguration CreateDefaultSetting(
        ModelParameterMapping mapping,
        ParameterDefinition definition,
        ModelParameter parameter) => new(
            mapping.ModelParameterId,
            mapping.SourceParameterId,
            definition.SuggestedMinimum,
            definition.SuggestedMaximum,
            parameter.Minimum,
            parameter.Maximum,
            ClampInput: false,
            ClampOutput: false,
            EnableAutoBlink: mapping.ModelParameterId is "ParamEyeLOpen" or "ParamEyeROpen",
            EnableAutoBreath: mapping.ModelParameterId == "ParamBreath");
}

internal static partial class ModelParameterMappingServiceLog
{
    [LoggerMessage(6520, LogLevel.Warning,
        "Invalid model parameter configuration detected for {ModelId}; defaults were prepared in memory; error {ErrorType}")]
    internal static partial void InvalidConfigurationDetected(
        ILogger logger,
        string errorType,
        string modelId);

    [LoggerMessage(6521, LogLevel.Information,
        "Default model parameter configuration prepared for {ModelId}; persistence awaits explicit confirmation")]
    internal static partial void DefaultConfigurationPrepared(
        ILogger logger,
        string modelId);

    [LoggerMessage(6522, LogLevel.Information,
        "Invalid model parameter configuration rebuilt with defaults for {ModelId}")]
    internal static partial void InvalidConfigurationRepaired(ILogger logger, string modelId);
}
