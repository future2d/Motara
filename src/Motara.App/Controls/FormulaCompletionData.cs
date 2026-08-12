using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Motara.App.ViewModels;

namespace Motara.App.Controls;

internal sealed class FormulaCompletionData(
    FormulaEditorControl owner,
    FormulaCompletionItem item) : ICompletionData
{
    public FormulaCompletionItem Item { get; } = item;

    public IImage Image => null!;

    public string Text => Item.DisplayText;

    public object Content
    {
        get
        {
            var primary = new TextBlock
            {
                Text = Item.DisplayText,
                FontSize = 13,
                Foreground = owner.ResolveThemeBrush("TextPrimary", Brushes.Black),
            };
            primary.Classes.Add("formula-completion-primary");
            var secondary = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(Item.Description)
                    ? Item.Category
                    : Item.Description,
                FontSize = 11,
                Foreground = owner.ResolveThemeBrush("TextSecondary", Brushes.Gray),
                TextWrapping = TextWrapping.Wrap,
            };
            secondary.Classes.Add("formula-completion-secondary");
            return new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    primary,
                    secondary,
                },
            };
        }
    }

    public object Description => null!;

    public double Priority => 0;

    public void Complete(
        TextArea textArea,
        ISegment completionSegment,
        EventArgs insertionRequestEventArgs) =>
        owner.ReplaceCompletion(Item, completionSegment.Offset, completionSegment.Length);
}
