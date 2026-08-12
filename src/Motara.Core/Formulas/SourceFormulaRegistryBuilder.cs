using Motara.Core.Parameters;

namespace Motara.Core.Formulas;

public static class SourceFormulaRegistryBuilder
{
    public static ParameterRegistry Create(
        IEnumerable<ParameterDefinition> baseDefinitions,
        IEnumerable<CompiledSourceFormulaProgram> programs)
    {
        ArgumentNullException.ThrowIfNull(baseDefinitions);
        ArgumentNullException.ThrowIfNull(programs);

        var definitions = new List<ParameterDefinition>();
        var byId = new Dictionary<string, ParameterDefinition>(StringComparer.Ordinal);
        foreach (ParameterDefinition definition in baseDefinitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (!byId.TryAdd(definition.Id, definition))
            {
                throw new ArgumentException(
                    $"Duplicate base parameter identifier: {definition.Id}",
                    nameof(baseDefinitions));
            }

            definitions.Add(definition);
        }

        foreach (CompiledSourceFormulaProgram program in programs)
        {
            ArgumentNullException.ThrowIfNull(program);
            foreach (SourceFormulaDefinition output in program.OutputDefinitions)
            {
                if (byId.TryGetValue(output.OutputId, out ParameterDefinition? existing))
                {
                    EnsureCompatible(existing, output);
                    continue;
                }

                var added = new ParameterDefinition(
                    output.OutputId,
                    output.NeutralValue,
                    output.SuggestedMinimum,
                    output.SuggestedMaximum,
                    $"Parameter.SourceFormula.{output.OutputId}",
                    ParameterDefinitionOrigin.SourceFormula);
                byId.Add(added.Id, added);
                definitions.Add(added);
            }
        }

        return ParameterRegistry.Create(definitions);
    }

    private static void EnsureCompatible(
        ParameterDefinition existing,
        SourceFormulaDefinition output)
    {
        if (existing.NeutralValue != output.NeutralValue
            || existing.SuggestedMinimum != output.SuggestedMinimum
            || existing.SuggestedMaximum != output.SuggestedMaximum
            || !existing.IsWritable)
        {
            throw new ArgumentException(
                $"Formula metadata conflicts with registered parameter: {output.OutputId}",
                nameof(output));
        }
    }
}
