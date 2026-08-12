namespace Motara.App.ViewModels;

public enum FormulaCompletionKind
{
    Input,
    Output,
    Function,
}

public sealed record FormulaCompletionItem(
    string DisplayText,
    string InsertText,
    string Category,
    string Description,
    double? CurrentValue,
    FormulaCompletionKind Kind);
