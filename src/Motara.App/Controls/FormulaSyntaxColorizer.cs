using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Motara.Core.Formulas;

namespace Motara.App.Controls;

internal sealed class FormulaSyntaxColorizer(
    Control resourceOwner,
    Func<SourceFormulaDiagnostic?> diagnosticProvider) : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        string text = CurrentContext.Document.GetText(line);
        int index = 0;
        while (index < text.Length)
        {
            char value = text[index];
            if (char.IsDigit(value) || (value == '.' && index + 1 < text.Length && char.IsDigit(text[index + 1])))
            {
                int end = ReadNumber(text, index);
                Colorize(line.Offset + index, line.Offset + end, "FormulaNumberForeground");
                index = end;
                continue;
            }

            if (SourceFormulaLanguage.IsIdentifierCharacter(value))
            {
                int end = index + 1;
                while (end < text.Length && SourceFormulaLanguage.IsIdentifierCharacter(text[end]))
                {
                    end++;
                }

                int next = end;
                while (next < text.Length && char.IsWhiteSpace(text[next]))
                {
                    next++;
                }

                Colorize(
                    line.Offset + index,
                    line.Offset + end,
                    next < text.Length && text[next] == '('
                        ? "FormulaFunctionForeground"
                        : "FormulaParameterForeground");
                index = end;
                continue;
            }

            if (value is '+' or '-' or '*' or '/' or '(' or ')' or ',')
            {
                Colorize(line.Offset + index, line.Offset + index + 1, "FormulaOperatorForeground");
            }

            index++;
        }

        SourceFormulaDiagnostic? diagnostic = diagnosticProvider();
        if (diagnostic is null || diagnostic.Length <= 0)
        {
            return;
        }

        int diagnosticStart = Math.Max(line.Offset, diagnostic.Start);
        int diagnosticEnd = Math.Min(line.EndOffset, diagnostic.Start + diagnostic.Length);
        if (diagnosticEnd > diagnosticStart && TryGetBrush("StatusError", out IBrush? errorBrush))
        {
            ChangeLinePart(diagnosticStart, diagnosticEnd, element =>
            {
                element.TextRunProperties.SetForegroundBrush(errorBrush);
                element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
            });
        }
    }

    private static int ReadNumber(string text, int start)
    {
        int index = start;
        bool hasExponent = false;
        while (index < text.Length)
        {
            char value = text[index];
            if (char.IsDigit(value) || value == '.')
            {
                index++;
                continue;
            }

            if (!hasExponent && value is 'e' or 'E')
            {
                hasExponent = true;
                index++;
                if (index < text.Length && text[index] is '+' or '-')
                {
                    index++;
                }

                continue;
            }

            break;
        }

        return index;
    }

    private void Colorize(int start, int end, string resourceKey)
    {
        if (TryGetBrush(resourceKey, out IBrush? brush))
        {
            ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }

    private bool TryGetBrush(string key, out IBrush? brush)
    {
        if (resourceOwner.TryFindResource(key, out object? resource) && resource is IBrush found)
        {
            brush = found;
            return true;
        }

        brush = null;
        return false;
    }
}
