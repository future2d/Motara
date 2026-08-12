using System.Collections.Immutable;
using Motara.Core.Formulas;
using Motara.Core.Parameters;

namespace Motara.App.Parameters;

internal static class SourceMappingParameterCatalog
{
    internal static bool IsBuiltIn(string parameterId) =>
        StandardParameterCatalog.Registry.TryGetSlot(parameterId, out _);

    internal static ParameterDefinition? FindBuiltIn(string parameterId) =>
        StandardParameterCatalog.Definitions.FirstOrDefault(definition =>
            StringComparer.Ordinal.Equals(definition.Id, parameterId));

    internal static SourceMappingProfileDocument NormalizeBuiltIns(
        SourceMappingProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        Dictionary<string, SourceMappingOutputDocument> configured = document.Outputs
            .ToDictionary(static output => output.ParameterId, StringComparer.Ordinal);
        var outputs = ImmutableArray.CreateBuilder<SourceMappingOutputDocument>(
            StandardParameterCatalog.Definitions.Length + document.Outputs.Length);
        foreach (ParameterDefinition definition in StandardParameterCatalog.Definitions)
        {
            configured.TryGetValue(definition.Id, out SourceMappingOutputDocument? existing);
            outputs.Add(new SourceMappingOutputDocument(
                definition.Id,
                Subtitle: null,
                existing?.Formula ?? string.Empty,
                definition.NeutralValue,
                definition.SuggestedMinimum,
                definition.SuggestedMaximum,
                existing?.Smoothing ?? 0));
        }

        outputs.AddRange(document.Outputs.Where(output => !IsBuiltIn(output.ParameterId)));
        return document with { Outputs = outputs.ToImmutable() };
    }
}
