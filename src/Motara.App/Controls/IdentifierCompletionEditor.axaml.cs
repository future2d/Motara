using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Motara.App.ViewModels;
using Motara.Core.Formulas;

namespace Motara.App.Controls;

internal sealed partial class IdentifierCompletionEditor : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<IdentifierCompletionEditor, string>(nameof(Text), string.Empty);

    private readonly TextEditor editor;
    private readonly TextBlock placeholder;
    private FormulaCompletionItem[] completions = [];
    private CompletionWindow? completionWindow;
    private bool synchronizingText;

    public IdentifierCompletionEditor()
    {
        AvaloniaXamlLoader.Load(this);
        editor = this.FindControl<TextEditor>("Editor")!;
        placeholder = this.FindControl<TextBlock>("Placeholder")!;
        editor.TextChanged += (_, _) => UpdateTextFromEditor();
        editor.TextArea.KeyDown += OnEditorKeyDown;
        editor.TextArea.TextEntered += OnEditorTextEntered;
        editor.GotFocus += (_, _) => UpdatePlaceholder();
        editor.LostFocus += (_, _) => UpdatePlaceholder();
        UpdatePlaceholder();
    }

    public event EventHandler<string>? Submitted;

    public event EventHandler<string>? CompletionAccepted;

    public event EventHandler? TextChanged;

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value ?? string.Empty);
    }

    public string? PlaceholderText
    {
        get => placeholder.Text;
        set => placeholder.Text = value;
    }

    public int CaretOffset
    {
        get => editor.CaretOffset;
        set => editor.CaretOffset = Math.Clamp(value, 0, editor.Document.TextLength);
    }

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
        foreach (FormulaCompletionItem item in completions)
        {
            window.CompletionList.CompletionData.Add(new IdentifierCompletionData(this, item));
        }

        completionWindow = window;
        window.CompletionList.Classes.Add("motara-formula-completion");
        var surface = new Border
        {
            Background = ResolveThemeBrush("FormulaCompletionSurface", Brushes.White),
            BorderBrush = ResolveThemeBrush("FormulaCompletionBorder", Brushes.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
        };
        window.Child = null;
        surface.Child = window.CompletionList;
        window.Child = surface;
        window.CompletionList.Background = Brushes.Transparent;
        window.CompletionList.BorderBrush = Brushes.Transparent;
        window.CompletionList.BorderThickness = new Thickness(0);
        window.CompletionList.Foreground = ResolveThemeBrush("TextPrimary", Brushes.Black);
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

    internal void ReplaceCompletion(FormulaCompletionItem item, int offset, int length)
    {
        using (editor.Document.RunUpdate())
        {
            editor.Document.Replace(offset, length, item.InsertText);
            editor.CaretOffset = offset + item.InsertText.Length;
        }

        CompletionAccepted?.Invoke(this, item.InsertText);
        editor.Focus();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty && change.NewValue is string text)
        {
            UpdateEditorFromText(text);
            if (!synchronizingText)
            {
                TextChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = OpenCompletion();
        }
        else if (e.Key == Key.Enter && completionWindow is null)
        {
            Submitted?.Invoke(this, Text);
            e.Handled = true;
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

    private void UpdateEditorFromText(string value)
    {
        if (synchronizingText || editor.Text == value)
        {
            return;
        }

        synchronizingText = true;
        editor.Text = value;
        editor.CaretOffset = editor.Document.TextLength;
        synchronizingText = false;
        UpdatePlaceholder();
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
        UpdatePlaceholder();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdatePlaceholder() =>
        placeholder.IsVisible = string.IsNullOrEmpty(editor.Text) && !editor.IsKeyboardFocusWithin;

    private int FindIdentifierStart(int caretOffset)
    {
        int start = caretOffset;
        while (start > 0 && SourceFormulaLanguage.IsIdentifierCharacter(editor.Document.GetCharAt(start - 1)))
        {
            start--;
        }

        return start;
    }

    private void CloseCompletion()
    {
        completionWindow?.Hide();
        completionWindow = null;
    }

    private IBrush ResolveThemeBrush(string key, IBrush fallback) =>
        this.FindResource(key) is IBrush brush ? brush : fallback;

    private sealed class IdentifierCompletionData(
        IdentifierCompletionEditor owner,
        FormulaCompletionItem item) : ICompletionData
    {
        public IImage Image => null!;

        public string Text => item.DisplayText;

        public object Content
        {
            get
            {
                var primary = new TextBlock
                {
                    Text = item.DisplayText,
                    FontSize = 13,
                    Foreground = owner.ResolveThemeBrush("TextPrimary", Brushes.Black),
                };
                primary.Classes.Add("formula-completion-primary");
                var secondary = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(item.Description) ? item.Category : item.Description,
                    FontSize = 11,
                    Foreground = owner.ResolveThemeBrush("TextSecondary", Brushes.Gray),
                };
                secondary.Classes.Add("formula-completion-secondary");
                return new StackPanel
                {
                    Spacing = 2,
                    Children = { primary, secondary },
                };
            }
        }

        public object Description => null!;

        public double Priority => 0;

        public void Complete(
            TextArea textArea,
            ISegment completionSegment,
            EventArgs insertionRequestEventArgs) =>
            owner.ReplaceCompletion(item, completionSegment.Offset, completionSegment.Length);
    }
}
