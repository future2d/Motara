namespace Motara.Core.Formulas;

public static class SourceFormulaFormatter
{
    public static string Format(string expression) =>
        SourceFormulaCompiler.FormatExpression(expression);
}
