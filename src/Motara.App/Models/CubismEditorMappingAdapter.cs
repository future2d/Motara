using System.Collections.Immutable;
using Motara.Core.Parameters;
using Motara.ModelLibrary;
using Motara.ModelRuntime.Abstractions;
using Motara.App.ViewModels;
using Motara.Output.CubismEditor;

namespace Motara.App.Models;

/// <summary>Adapts the independent Cubism Editor mapping to the shared parameter-mapping editor model.</summary>
internal static class CubismEditorMappingAdapter
{
    internal static ModelParameterMappingDocument CreateEditorDocument(
        CubismEditorMappingDocument mapping,
        IEnumerable<CubismEditorModelParameter> editorParameters)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(editorParameters);
        mapping.Validate();
        ImmutableArray<ModelParameter> parameters = editorParameters
            .Select(parameter => new ModelParameter(
                parameter.Id,
                parameter.Minimum,
                parameter.Default,
                parameter.Maximum,
                parameter.Name))
            .ToImmutableArray();
        var capabilities = new ModelCapabilities(
            new ModelCanvasInfo(1, 1, 1),
            parameters,
            textureCount: 0,
            drawableCount: 0);
        var entry = new ModelCatalogViewModel.ModelCatalogEntryViewModel(
            ModelId.Create("cubism-editor"),
            "Cubism Editor",
            string.Empty,
            IsSelectable: true,
            IsViewed: false,
            IsCurrentMainModel: false,
            FormatSummary: "Cubism Editor",
            TextureCount: 0);
        Dictionary<string, ModelParameter> targets = parameters.ToDictionary(
            static parameter => parameter.Id,
            StringComparer.Ordinal);
        ImmutableArray<ModelParameterSettingConfiguration> settings = mapping.Bindings
            .Select(binding => CreateSetting(binding, targets))
            .ToImmutableArray();
        return new ModelParameterMappingDocument(entry, capabilities, settings, [], wasGenerated: false);
    }

    internal static CubismEditorMappingDocument CreateOutputDocument(
        ModelParameterMappingDocument document) => new(
            CubismEditorMappingDocument.CurrentSchemaVersion,
            document.ParameterSettings
                .Where(static setting => !string.IsNullOrWhiteSpace(setting.GlobalParameterId))
                .Select(static setting => new CubismEditorParameterBinding(
                    setting.GlobalParameterId!,
                    setting.ModelParameterId))
                .ToImmutableArray());

    private static ModelParameterSettingConfiguration CreateSetting(
        CubismEditorParameterBinding binding,
        Dictionary<string, ModelParameter> targets)
    {
        ParameterDefinition? source = StandardParameterCatalog.Definitions
            .FirstOrDefault(definition => StringComparer.Ordinal.Equals(
                definition.Id,
                binding.SoftwareParameterId));
        bool hasTarget = targets.TryGetValue(binding.CubismParameterId, out ModelParameter? target);
        double inputMinimum = source?.SuggestedMinimum ?? -1;
        double inputMaximum = source?.SuggestedMaximum ?? 1;
        return new ModelParameterSettingConfiguration(
            binding.CubismParameterId,
            binding.SoftwareParameterId,
            inputMinimum,
            inputMaximum,
            hasTarget ? target!.Minimum : -1,
            hasTarget ? target!.Maximum : 1,
            ClampInput: false,
            ClampOutput: false,
            EnableAutoBlink: false,
            EnableAutoBreath: false);
    }
}
