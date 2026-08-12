using System.Collections.Immutable;
using Motara.Core.Formulas;

namespace Motara.App.ViewModels;

public sealed record FormulaEditorState(
    SourceFormulaDiagnostic? Diagnostic,
    double? PreviewValue)
{
    public static FormulaEditorState Empty { get; } = new(null, null);
}

public sealed record SourceMappingValidationReport(
    ImmutableArray<FormulaEditorState> OutputStates,
    ImmutableArray<SourceFormulaValidationDiagnostic> Diagnostics)
{
    public static SourceMappingValidationReport Empty(int outputCount) => new(
        Enumerable.Repeat(FormulaEditorState.Empty, outputCount).ToImmutableArray(),
        []);

    public static SourceMappingValidationReport FromSingle(
        int outputCount,
        FormulaEditorState state)
    {
        var states = Enumerable.Repeat(FormulaEditorState.Empty, outputCount).ToArray();
        if (states.Length > 0)
        {
            states[0] = state;
        }

        ImmutableArray<SourceFormulaValidationDiagnostic> diagnostics = state.Diagnostic is null
            ? []
            : [new SourceFormulaValidationDiagnostic(0, null, state.Diagnostic)];
        return new SourceMappingValidationReport(states.ToImmutableArray(), diagnostics);
    }
}

public sealed record SourceMappingApplyError(
    string ParameterId,
    SourceFormulaDiagnostic Diagnostic);
