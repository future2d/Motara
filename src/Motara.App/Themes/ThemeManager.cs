using Avalonia.Controls;
using Avalonia.Media;

namespace Motara.App.Themes;

public interface IThemeManager
{
    ThemePalette Palette { get; }

    void Apply(IResourceDictionary resources);
}

public sealed class ThemeManager(ThemePalette palette) : IThemeManager
{
    public ThemePalette Palette { get; } = palette ?? throw new ArgumentNullException(nameof(palette));

    public void Apply(IResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        SetBrush(resources, nameof(Palette.CanvasBackground), Palette.CanvasBackground);
        SetBrush(resources, nameof(Palette.SurfaceFloating), Palette.SurfaceFloating);
        SetBrush(resources, nameof(Palette.SurfaceFloatingHover), Palette.SurfaceFloatingHover);
        SetBrush(resources, nameof(Palette.SurfaceFloatingSelected), Palette.SurfaceFloatingSelected);
        SetBrush(resources, nameof(Palette.SurfaceFloatingPressed), Palette.SurfaceFloatingPressed);
        SetBrush(resources, nameof(Palette.SurfaceFloatingHoverBorder), Palette.SurfaceFloatingHoverBorder);
        SetBrush(resources, nameof(Palette.SurfaceFloatingSelectedBorder), Palette.SurfaceFloatingSelectedBorder);
        SetBrush(resources, nameof(Palette.MenuRowSelectedBorder), Palette.MenuRowSelectedBorder);
        SetBrush(resources, nameof(Palette.TextPrimary), Palette.TextPrimary);
        SetBrush(resources, nameof(Palette.TextSecondary), Palette.TextSecondary);
        SetBrush(resources, nameof(Palette.TextOnAccent), Palette.TextOnAccent);
        SetBrush(resources, nameof(Palette.IconDefault), Palette.IconDefault);
        SetBrush(resources, nameof(Palette.IconActive), Palette.IconActive);
        SetBrush(resources, nameof(Palette.TooltipSurface), Palette.TooltipSurface);
        SetBrush(resources, nameof(Palette.TooltipBorder), Palette.TooltipBorder);
        SetBrush(resources, nameof(Palette.DividerSubtle), Palette.DividerSubtle);
        SetBrush(resources, nameof(Palette.MeterTrack), Palette.MeterTrack);
        SetBrush(resources, nameof(Palette.MeterFill), Palette.MeterFill);
        SetBrush(resources, nameof(Palette.BorderSubtle), Palette.BorderSubtle);
        SetBrush(resources, nameof(Palette.FocusRing), Palette.FocusRing);
        SetBrush(resources, nameof(Palette.FocusRingSoft), Palette.FocusRingSoft);
        SetBrush(resources, "TextControlBorderBrushFocused", Palette.FocusRing);
        SetBrush(resources, nameof(Palette.FloatingShadowPrimary), Palette.FloatingShadowPrimary);
        SetBrush(resources, nameof(Palette.FloatingShadowSecondary), Palette.FloatingShadowSecondary);
        SetBrush(resources, nameof(Palette.TooltipShadow), Palette.TooltipShadow);
        SetBrush(resources, nameof(Palette.OverlayTextFill), Palette.OverlayTextFill);
        SetBrush(resources, nameof(Palette.OverlayTextOutline), Palette.OverlayTextOutline);
        SetBrush(resources, nameof(Palette.OverlayTextShadow), Palette.OverlayTextShadow);
        SetBrush(resources, nameof(Palette.ActionPrimary), Palette.ActionPrimary);
        SetBrush(resources, nameof(Palette.ActionSecondary), Palette.ActionSecondary);
        SetBrush(resources, nameof(Palette.StateConnected), Palette.StateConnected);
        SetBrush(resources, nameof(Palette.StateDegraded), Palette.StateDegraded);
        SetBrush(resources, nameof(Palette.StateFaulted), Palette.StateFaulted);
        SetBrush(resources, nameof(Palette.CategoryCoral), Palette.CategoryCoral);
        SetBrush(resources, nameof(Palette.CategoryApricot), Palette.CategoryApricot);
        SetBrush(resources, nameof(Palette.CategorySage), Palette.CategorySage);
        SetBrush(resources, nameof(Palette.CategorySagePressed), Palette.CategorySagePressed);
        SetBrush(resources, nameof(Palette.CategoryRose), Palette.CategoryRose);
        SetBrush(resources, nameof(Palette.CategoryLilac), Palette.CategoryLilac);
        SetBrush(resources, "FormulaParameterForeground", Palette.FormulaParameterForeground);
        SetBrush(resources, "FormulaFunctionForeground", Palette.FormulaFunctionForeground);
        SetBrush(resources, "FormulaNumberForeground", Palette.FormulaNumberForeground);
        SetBrush(resources, "FormulaOperatorForeground", Palette.FormulaOperatorForeground);
        SetBrush(resources, "FormulaCompletionSurface", Palette.SurfaceFloating);
        SetBrush(resources, "FormulaCompletionBorder", Palette.BorderSubtle);
        SetBrush(resources, "FormulaCompletionSelection", Palette.SurfaceFloatingSelected);
        SetBrush(resources, "FormulaCompletionSelectionBorder", Palette.MenuRowSelectedBorder);
        resources["FloatingSurfaceShadow"] = new BoxShadows(
            new BoxShadow { OffsetY = 12, Blur = 32, Color = Palette.FloatingShadowPrimary },
            [new BoxShadow { OffsetY = 2, Blur = 8, Color = Palette.FloatingShadowSecondary }]);
        resources["TooltipElevation"] = new BoxShadows(
            new BoxShadow { OffsetY = 8, Blur = 18, Color = Palette.TooltipShadow });
    }

    private static void SetBrush(IResourceDictionary resources, string key, Color color)
    {
        if (resources.TryGetValue(key, out object? value) && value is SolidColorBrush brush)
        {
            brush.Color = color;
            return;
        }

        resources[key] = new SolidColorBrush(color);
    }
}

public sealed record ThemePalette(
    Color CanvasBackground,
    Color SurfaceFloating,
    Color SurfaceFloatingHover,
    Color SurfaceFloatingSelected,
    Color SurfaceFloatingPressed,
    Color SurfaceFloatingHoverBorder,
    Color SurfaceFloatingSelectedBorder,
    Color MenuRowSelectedBorder,
    Color TextPrimary,
    Color TextSecondary,
    Color TextOnAccent,
    Color IconDefault,
    Color IconActive,
    Color TooltipSurface,
    Color TooltipBorder,
    Color DividerSubtle,
    Color MeterTrack,
    Color MeterFill,
    Color BorderSubtle,
    Color FocusRing,
    Color FocusRingSoft,
    Color FloatingShadowPrimary,
    Color FloatingShadowSecondary,
    Color TooltipShadow,
    Color OverlayTextFill,
    Color OverlayTextOutline,
    Color OverlayTextShadow,
    Color ActionPrimary,
    Color ActionSecondary,
    Color StateConnected,
    Color StateDegraded,
    Color StateFaulted,
    Color CategoryCoral,
    Color CategoryApricot,
    Color CategorySage,
    Color CategorySagePressed,
    Color CategoryRose,
    Color CategoryLilac,
    Color FormulaParameterForeground,
    Color FormulaFunctionForeground,
    Color FormulaNumberForeground,
    Color FormulaOperatorForeground)
{
    public static ThemePalette WarmNeutralLight { get; } = new(
        Color.Parse("#F4F3F1"),
        Color.Parse("#FFFDFB"),
        Color.Parse("#F8EFEC"),
        Color.Parse("#F1DED9"),
        Color.Parse("#F1DED9"),
        Color.Parse("#EBDCD8"),
        Color.Parse("#DDBDB5"),
        Color.Parse("#E5C7C0"),
        Color.Parse("#403A3A"),
        Color.Parse("#746B69"),
        Color.Parse("#FFFDFB"),
        Color.Parse("#675D5B"),
        Color.Parse("#774F46"),
        Color.Parse("#FFFDFB"),
        Color.Parse("#DED5D2"),
        Color.Parse("#EBE3E0"),
        Color.Parse("#EBE4E1"),
        Color.Parse("#BD887E"),
        Color.Parse("#DED5D2"),
        Color.Parse("#9E6257"),
        Color.Parse("#599E6257"),
        Color.Parse("#1F53423E"),
        Color.Parse("#1453423E"),
        Color.Parse("#2E433632"),
        Color.Parse("#514745"),
        Color.Parse("#F0FFFDFB"),
        Color.Parse("#F2FFFDFB"),
        Color.Parse("#DFB4AD"),
        Color.Parse("#C8D5C2"),
        Color.Parse("#52705A"),
        Color.Parse("#8C681E"),
        Color.Parse("#9C3F46"),
        Color.Parse("#DFB4AD"),
        Color.Parse("#E7C9AD"),
        Color.Parse("#C8D5C2"),
        Color.Parse("#B8C7B1"),
        Color.Parse("#DEC4CA"),
        Color.Parse("#D5CDDD"),
        Color.Parse("#46604D"),
        Color.Parse("#874E45"),
        Color.Parse("#805427"),
        Color.Parse("#5F5553"));
}
