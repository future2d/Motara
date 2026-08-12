using System.Collections.Immutable;

namespace Motara.Core.Formulas;

public sealed record FormulaFunctionDefinition(string Name, int Arity, string Template);

/// <summary>Defines formula syntax shared by the compiler and editor.</summary>
public static class SourceFormulaLanguage
{
    public static ImmutableArray<FormulaFunctionDefinition> Functions { get; } =
    [
        new("abs", 1, "abs(value)"),
        new("min", 2, "min(left, right)"),
        new("max", 2, "max(left, right)"),
        new("clamp", 3, "clamp(value, min, max)"),
        new("degToRad", 1, "degToRad(degrees)"),
    ];

    public static bool IsIdentifierCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '.';

    public static bool TryGetFunction(string name, out FormulaFunctionDefinition definition)
    {
        foreach (FormulaFunctionDefinition candidate in Functions)
        {
            if (StringComparer.Ordinal.Equals(candidate.Name, name))
            {
                definition = candidate;
                return true;
            }
        }

        definition = null!;
        return false;
    }
}
