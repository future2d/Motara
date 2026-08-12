namespace Motara.Core.Formulas;

public sealed record SourceFormulaDiagnostic(
    SourceFormulaErrorCode Code,
    int Start,
    int Length,
    string Message);

public sealed record SourceFormulaValidationDiagnostic(
    int OutputIndex,
    string? OutputId,
    SourceFormulaDiagnostic Diagnostic);
