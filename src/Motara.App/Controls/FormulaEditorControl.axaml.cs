using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using Motara.Core.Formulas;
using Motara.App.ViewModels;

namespace Motara.App.Controls;

public sealed partial class FormulaEditorControl : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<FormulaEditorControl, string>(nameof(Text), string.Empty);

    private readonly TextEditor editor;
    private readonly TextBlock diagnosticText;
    private readonly TextBlock previewText;
    private bool synchronizingText;
    private FormulaCompletionItem[] completions = [];
    private CompletionWindow? completionWindow;
    private FormulaCompletionItem? selectedCompletion;
    private SourceFormulaDiagnostic? diagnostic;
    private int completionStartOffset;
    private int completionEndOffset;
    private List<TemplatePlaceholder> templatePlaceholders = [];
    private int templatePlaceholderIndex = -1;

    public FormulaEditorControl()
    {
        AvaloniaXamlLoader.Load(this);
        editor = this.FindControl<TextEditor>("Editor")!;
        diagnosticText = this.FindControl<TextBlock>("DiagnosticText")!;
        previewText = this.FindControl<TextBlock>("PreviewText")!;
        editor.TextChanged += (_, _) => UpdateTextFromEditor();
        editor.TextArea.KeyDown += OnEditorKeyDown;
        editor.TextArea.TextEntered += OnEditorTextEntered;
        editor.TextArea.TextView.LineTransformers.Add(
            new FormulaSyntaxColorizer(this, () => diagnostic));
        Loaded += (_, _) => ApplyScrollBarStyle();
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value ?? string.Empty);
    }

    public int CaretOffset
    {
        get => editor.CaretOffset;
        set => editor.CaretOffset = Math.Clamp(value, 0, editor.Document.TextLength);
    }

    public string SelectedText => editor.SelectedText;

    public bool IsCompletionOpen => completionWindow is not null;

    public void SetCompletions(IEnumerable<FormulaCompletionItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        completions = items.ToArray();
        CloseCompletion();
    }

    public bool OpenCompletion()
    {
        CloseCompletion();
        if (completions.Length == 0 || TopLevel.GetTopLevel(editor) is not Window)
        {
            return false;
        }

        int startOffset = FindIdentifierStart(editor.CaretOffset);
        var window = new CompletionWindow(editor.TextArea)
        {
            StartOffset = startOffset,
            EndOffset = editor.CaretOffset,
            CloseWhenCaretAtBeginning = false,
        };
        string prefix = editor.Document.GetText(startOffset, editor.CaretOffset - startOffset);
        FormulaCompletionData? preferred = null;
        foreach (FormulaCompletionItem item in completions)
        {
            var data = new FormulaCompletionData(this, item);
            window.CompletionList.CompletionData.Add(data);
            if (preferred is null && item.DisplayText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                preferred = data;
            }
        }

        window.CompletionList.SelectedItem = preferred ?? window.CompletionList.CompletionData[0];
        selectedCompletion = (preferred ?? (FormulaCompletionData)window.CompletionList.CompletionData[0]).Item;
        completionStartOffset = startOffset;
        completionEndOffset = editor.CaretOffset;
        window.CompletionList.SelectionChanged += (_, _) =>
        {
            if (window.CompletionList.SelectedItem is FormulaCompletionData selected)
            {
                selectedCompletion = selected.Item;
            }
        };

        completionWindow = window;
        window.CompletionList.Classes.Add("motara-formula-completion");
        var completionSurface = new Border
        {
            Background = ResolveThemeBrush(
            "FormulaCompletionSurface",
            Avalonia.Media.Brushes.White),
            BorderBrush = ResolveThemeBrush(
            "FormulaCompletionBorder",
            Avalonia.Media.Brushes.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
        };
        window.Child = null;
        completionSurface.Child = window.CompletionList;
        window.Child = completionSurface;
        window.CompletionList.Background = Avalonia.Media.Brushes.Transparent;
        window.CompletionList.BorderBrush = Avalonia.Media.Brushes.Transparent;
        window.CompletionList.BorderThickness = new Thickness(0);
        window.CompletionList.Foreground = ResolveThemeBrush(
            "TextPrimary",
            Avalonia.Media.Brushes.Black);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(completionWindow, window))
            {
                completionWindow = null;
            }
        };
        editor.Focus();
        window.Show();
        return true;
    }

    public bool AcceptSelectedCompletion()
    {
        if (completionWindow is null || selectedCompletion is null)
        {
            return false;
        }

        FormulaCompletionItem selected = selectedCompletion;
        int startOffset = completionStartOffset;
        int length = completionEndOffset - startOffset;
        completionWindow.Hide();
        completionWindow = null;
        selectedCompletion = null;
        ReplaceCompletion(selected, startOffset, length);
        return true;
    }

    public void InsertIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        int insertionOffset = editor.SelectionLength > 0
            ? editor.SelectionStart
            : editor.CaretOffset;
        int removalLength = editor.SelectionLength;
        using (editor.Document.RunUpdate())
        {
            editor.Document.Replace(insertionOffset, removalLength, identifier);
            editor.CaretOffset = insertionOffset + identifier.Length;
        }

        editor.Focus();
    }

    public void InsertCompletion(FormulaCompletionItem completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        int insertionOffset = editor.CaretOffset;
        using (editor.Document.RunUpdate())
        {
            editor.Document.Insert(insertionOffset, completion.InsertText);
            editor.CaretOffset = insertionOffset + completion.InsertText.Length;
        }

        templatePlaceholders = completion.Kind == FormulaCompletionKind.Function
            ? CreateTemplatePlaceholders(completion.InsertText, insertionOffset)
            : [];
        templatePlaceholderIndex = templatePlaceholders.Count > 0 ? 0 : -1;
        SelectCurrentPlaceholder();
        editor.Focus();
    }

    internal void ReplaceCompletion(FormulaCompletionItem completion, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(completion);
        using (editor.Document.RunUpdate())
        {
            editor.Document.Replace(offset, length, completion.InsertText);
            editor.CaretOffset = offset + completion.InsertText.Length;
        }

        templatePlaceholders = completion.Kind == FormulaCompletionKind.Function
            ? CreateTemplatePlaceholders(completion.InsertText, offset)
            : [];
        templatePlaceholderIndex = templatePlaceholders.Count > 0 ? 0 : -1;
        SelectCurrentPlaceholder();
        editor.Focus();
    }

    public bool AdvanceTemplatePlaceholder()
    {
        if (templatePlaceholderIndex < 0 || templatePlaceholderIndex + 1 >= templatePlaceholders.Count)
        {
            templatePlaceholders = [];
            templatePlaceholderIndex = -1;
            editor.SelectionLength = 0;
            return false;
        }

        templatePlaceholderIndex++;
        SelectCurrentPlaceholder();
        return true;
    }

    public void SetDiagnostic(SourceFormulaDiagnostic? diagnostic)
    {
        this.diagnostic = diagnostic;
        diagnosticText.Text = diagnostic?.Message;
        diagnosticText.IsVisible = diagnostic is not null;
        editor.TextArea.TextView.Redraw();
    }

    public void SetPreview(string? preview)
    {
        previewText.Text = preview;
        previewText.IsVisible = !string.IsNullOrWhiteSpace(preview);
    }

    public void Undo() => editor.Undo();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty && change.NewValue is string text)
        {
            UpdateEditorFromText(text);
        }
    }

    private void UpdateEditorFromText(string value)
    {
        if (synchronizingText || editor.Text == value)
        {
            return;
        }

        synchronizingText = true;
        editor.Text = value;
        synchronizingText = false;
    }

    private void UpdateTextFromEditor()
    {
        if (synchronizingText)
        {
            return;
        }

        synchronizingText = true;
        SetCurrentValue(TextProperty, editor.Text);
        synchronizingText = false;
    }

    private List<TemplatePlaceholder> CreateTemplatePlaceholders(
        string template,
        int insertionOffset)
    {
        int openParenthesis = template.IndexOf('(');
        int closeParenthesis = template.LastIndexOf(')');
        if (openParenthesis < 0 || closeParenthesis <= openParenthesis + 1)
        {
            return [];
        }

        var placeholders = new List<TemplatePlaceholder>();
        int argumentStart = openParenthesis + 1;
        while (argumentStart < closeParenthesis)
        {
            int separator = template.IndexOf(',', argumentStart, closeParenthesis - argumentStart);
            int argumentEnd = separator >= 0 ? separator : closeParenthesis;
            int contentStart = argumentStart;
            while (contentStart < argumentEnd && char.IsWhiteSpace(template[contentStart]))
            {
                contentStart++;
            }

            int contentEnd = argumentEnd;
            while (contentEnd > contentStart && char.IsWhiteSpace(template[contentEnd - 1]))
            {
                contentEnd--;
            }

            if (contentEnd > contentStart)
            {
                TextAnchor start = editor.Document.CreateAnchor(insertionOffset + contentStart);
                start.MovementType = AnchorMovementType.BeforeInsertion;
                start.SurviveDeletion = true;
                TextAnchor end = editor.Document.CreateAnchor(insertionOffset + contentEnd);
                end.MovementType = AnchorMovementType.AfterInsertion;
                end.SurviveDeletion = true;
                placeholders.Add(new TemplatePlaceholder(start, end));
            }

            if (separator < 0)
            {
                break;
            }

            argumentStart = separator + 1;
        }

        return placeholders;
    }

    private void SelectCurrentPlaceholder()
    {
        if (templatePlaceholderIndex < 0)
        {
            return;
        }

        TemplatePlaceholder placeholder = templatePlaceholders[templatePlaceholderIndex];
        if (placeholder.Start.IsDeleted || placeholder.End.IsDeleted)
        {
            return;
        }

        editor.Select(placeholder.Start.Offset, placeholder.End.Offset - placeholder.Start.Offset);
    }

    private int FindIdentifierStart(int caretOffset)
    {
        int start = caretOffset;
        while (start > 0 && SourceFormulaLanguage.IsIdentifierCharacter(editor.Document.GetCharAt(start - 1)))
        {
            start--;
        }

        return start;
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = OpenCompletion();
            return;
        }

        if (e.Key == Key.Tab && completionWindow is null && templatePlaceholderIndex >= 0)
        {
            e.Handled = AdvanceTemplatePlaceholder();
        }
    }

    private void OnEditorTextEntered(object? sender, TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text)
            && e.Text.All(SourceFormulaLanguage.IsIdentifierCharacter))
        {
            _ = OpenCompletion();
        }
    }

    private void CloseCompletion()
    {
        completionWindow?.Hide();
        completionWindow = null;
        selectedCompletion = null;
    }

    private void ApplyScrollBarStyle()
    {
        foreach (ScrollBar scrollBar in editor.GetVisualDescendants().OfType<ScrollBar>())
        {
            if (!scrollBar.Classes.Contains("motara-scrollbar"))
            {
                scrollBar.Classes.Add("motara-scrollbar");
            }
        }
    }

    internal Avalonia.Media.IBrush ResolveThemeBrush(
        string resourceKey,
        Avalonia.Media.IBrush fallback) =>
        this.FindResource(resourceKey) is Avalonia.Media.IBrush brush ? brush : fallback;

    private sealed record TemplatePlaceholder(TextAnchor Start, TextAnchor End);
}
