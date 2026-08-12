using System.Text;

namespace Motara.Core.Formulas;

public static class SourceFormulaIdentifierRewriter
{
    public static string Rename(string formula, string oldId, string newId)
    {
        ArgumentNullException.ThrowIfNull(formula);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newId);
        if (StringComparer.Ordinal.Equals(oldId, newId))
        {
            return formula;
        }

        StringBuilder? result = null;
        int sourceOffset = 0;
        int tokenStart = 0;
        while (tokenStart < formula.Length)
        {
            if (!SourceFormulaLanguage.IsIdentifierCharacter(formula[tokenStart]))
            {
                tokenStart++;
                continue;
            }

            int tokenEnd = tokenStart + 1;
            while (tokenEnd < formula.Length
                && SourceFormulaLanguage.IsIdentifierCharacter(formula[tokenEnd]))
            {
                tokenEnd++;
            }

            if (formula.AsSpan(tokenStart, tokenEnd - tokenStart).SequenceEqual(oldId))
            {
                result ??= new StringBuilder(formula.Length + Math.Max(0, newId.Length - oldId.Length));
                result.Append(formula, sourceOffset, tokenStart - sourceOffset);
                result.Append(newId);
                sourceOffset = tokenEnd;
            }

            tokenStart = tokenEnd;
        }

        if (result is null)
        {
            return formula;
        }

        result.Append(formula, sourceOffset, formula.Length - sourceOffset);
        return result.ToString();
    }
}
