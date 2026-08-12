using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Transformation;
using Avalonia.Styling;

namespace Motara.App.Controls;

internal static class ShellToolTip
{
    public static void Configure(
        Control resourceHost,
        Control owner,
        string content,
        PlacementMode placement)
    {
        string closedTransform = placement switch
        {
            PlacementMode.Right => "translate(6px, 0px)",
            PlacementMode.Left => "translate(-6px, 0px)",
            PlacementMode.Bottom => "translate(0px, 6px)",
            _ => "translate(0px, -6px)",
        };
        var toolTip = new ToolTip
        {
            Content = content,
            Opacity = 0,
            RenderTransform = TransformOperations.Parse(closedTransform),
            Theme = (ControlTheme)resourceHost.FindResource("ShellToolTipTheme")!,
        };
        ToolTip.SetTip(owner, toolTip);
        ToolTip.SetPlacement(owner, placement);
        owner.PropertyChanged += (_, args) =>
        {
            if (args.Property != ToolTip.IsOpenProperty)
            {
                return;
            }

            bool isOpen = ToolTip.GetIsOpen(owner);
            toolTip.Opacity = isOpen ? 1 : 0;
            toolTip.RenderTransform = TransformOperations.Parse(
                isOpen ? "translate(0px, 0px)" : closedTransform);
        };
    }
}
