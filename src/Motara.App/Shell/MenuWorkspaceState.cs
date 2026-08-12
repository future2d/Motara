namespace Motara.App.Shell;

public readonly record struct MenuWorkspaceLayout(
    double Left,
    double MinimumOffset,
    double MaximumOffset,
    double AppliedOffset,
    bool HasOverflow);

/// <summary>Calculates cascading-menu workspace geometry without retaining drag state.</summary>
public static class MenuWorkspaceState
{
    public static double CalculateLeft(
        IReadOnlyList<double> menuWidths,
        double gap,
        double railAnchor,
        double canvasWidth,
        double rightSafeMargin)
    {
        return CalculateLayout(
            menuWidths,
            gap,
            railAnchor,
            canvasWidth,
            rightSafeMargin,
            requestedOffset: 0).Left;
    }

    public static MenuWorkspaceLayout CalculateLayout(
        IReadOnlyList<double> menuWidths,
        double gap,
        double railAnchor,
        double canvasWidth,
        double rightSafeMargin,
        double requestedOffset)
    {
        ArgumentNullException.ThrowIfNull(menuWidths);
        ValidateNonNegativeFinite(gap, nameof(gap));
        ValidateNonNegativeFinite(railAnchor, nameof(railAnchor));
        ValidateNonNegativeFinite(canvasWidth, nameof(canvasWidth));
        ValidateNonNegativeFinite(rightSafeMargin, nameof(rightSafeMargin));
        ValidateNonNegativeFinite(requestedOffset, nameof(requestedOffset));

        double menuWidth = 0;
        foreach (double width in menuWidths)
        {
            ValidateNonNegativeFinite(width, nameof(menuWidths));
            menuWidth += width;
        }

        double workspaceWidth = menuWidth + (Math.Max(0, menuWidths.Count - 1) * gap);
        double rightBoundary = canvasWidth - rightSafeMargin;
        double overflow = Math.Max(0, railAnchor + workspaceWidth - rightBoundary);
        double appliedOffset = Math.Clamp(requestedOffset, 0, overflow);
        return new MenuWorkspaceLayout(
            railAnchor - overflow + appliedOffset,
            MinimumOffset: 0,
            MaximumOffset: overflow,
            AppliedOffset: appliedOffset,
            HasOverflow: overflow > 0);
    }

    private static void ValidateNonNegativeFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
