using System.Collections.Immutable;
using Motara.Persistence;

namespace Motara.App.Input;

public static class BuiltInInputActions
{
    public const string MenuFocusPreviousItem = "Menu.FocusPreviousItem";
    public const string MenuFocusNextItem = "Menu.FocusNextItem";
    public const string MenuFocusPreviousColumn = "Menu.FocusPreviousColumn";
    public const string MenuFocusNextColumn = "Menu.FocusNextColumn";
    public const string MenuOpenSubmenu = "Menu.OpenSubmenu";
    public const string MenuCloseSubmenu = "Menu.CloseSubmenu";
    public const string MenuCloseAll = "Menu.CloseAll";
    public const string MenuPanLeft = "MenuWorkspace.PanLeft";
    public const string MenuPanRight = "MenuWorkspace.PanRight";
    public const string MenuScrollUp = "MenuColumn.ScrollUp";
    public const string MenuScrollDown = "MenuColumn.ScrollDown";
    public const string CanvasScaleUp = "Canvas.ScaleUp";
    public const string CanvasScaleDown = "Canvas.ScaleDown";
    public const string CanvasRotateLeft = "Canvas.RotateLeft";
    public const string CanvasRotateRight = "Canvas.RotateRight";
    public const string CanvasMoveModel = "Canvas.MoveModel";
    public const string CaptureScreenshot = "Software.Screenshot";

    public static InputActionRegistry CreateRegistry(InputBindingProfile? userProfile = null)
    {
        ImmutableArray<InputActionDescriptor> descriptors = CreateDescriptors();
        InputBindingProfile defaults = InputBindingProfile.Create(
            descriptors.SelectMany(static descriptor => descriptor.DefaultBindings));
        var registry = new InputActionRegistry(userProfile ?? defaults);
        foreach (InputActionDescriptor descriptor in descriptors)
        {
            registry.Register(descriptor);
        }

        if (userProfile is not null)
        {
            registry.ReconcileProfile(userProfile);
        }

        return registry;
    }

    public static ImmutableArray<InputActionDescriptor> CreateDescriptors() =>
    [
        MenuKey(MenuFocusPreviousItem, "Up", InputModifiers.None),
        MenuKey(MenuFocusNextItem, "Down", InputModifiers.None),
        MenuKey(MenuFocusPreviousColumn, "Tab", InputModifiers.Shift),
        MenuKey(MenuFocusNextColumn, "Tab", InputModifiers.None),
        MenuKeys(MenuOpenSubmenu, [("Right", InputModifiers.None), ("Enter", InputModifiers.None)]),
        MenuKey(MenuCloseSubmenu, "Left", InputModifiers.None),
        MenuKey(MenuCloseAll, "Escape", InputModifiers.None),
        Descriptor(
            MenuPanLeft,
            InputBindingScope.MenuWorkspace,
            [
                InputGesture.Wheel(InputAxis.Vertical, InputDirection.Positive, InputModifiers.Control),
                InputGesture.TouchpadAxis(InputAxis.Horizontal, InputDirection.Positive),
            ]),
        Descriptor(
            MenuPanRight,
            InputBindingScope.MenuWorkspace,
            [
                InputGesture.Wheel(InputAxis.Vertical, InputDirection.Negative, InputModifiers.Control),
                InputGesture.TouchpadAxis(InputAxis.Horizontal, InputDirection.Negative),
            ]),
        Descriptor(
            MenuScrollUp,
            InputBindingScope.MenuColumn,
            [
                InputGesture.Wheel(InputAxis.Vertical, InputDirection.Positive, InputModifiers.None),
                InputGesture.TouchpadAxis(InputAxis.Vertical, InputDirection.Positive),
            ]),
        Descriptor(
            MenuScrollDown,
            InputBindingScope.MenuColumn,
            [
                InputGesture.Wheel(InputAxis.Vertical, InputDirection.Negative, InputModifiers.None),
                InputGesture.TouchpadAxis(InputAxis.Vertical, InputDirection.Negative),
            ]),
        Descriptor(CanvasScaleUp, InputBindingScope.Canvas,
            [InputGesture.Wheel(InputAxis.Vertical, InputDirection.Positive, InputModifiers.None)]),
        Descriptor(CanvasScaleDown, InputBindingScope.Canvas,
            [InputGesture.Wheel(InputAxis.Vertical, InputDirection.Negative, InputModifiers.None)]),
        Descriptor(CanvasRotateLeft, InputBindingScope.Canvas,
            [InputGesture.Wheel(InputAxis.Vertical, InputDirection.Positive, InputModifiers.Control)]),
        Descriptor(CanvasRotateRight, InputBindingScope.Canvas,
            [InputGesture.Wheel(InputAxis.Vertical, InputDirection.Negative, InputModifiers.Control)]),
        Descriptor(CanvasMoveModel, InputBindingScope.Canvas,
            [InputGesture.MouseButton("Left", InputModifiers.None)]),
        new InputActionDescriptor(
            CaptureScreenshot,
            "Input.Action.Software.Screenshot",
            "Input.Category.Software",
            [InputBindingScope.Global, InputBindingScope.Application],
            [new InputBinding(
                CaptureScreenshot,
                InputBindingScope.Global,
                InputGesture.KeyChord("F12", InputModifiers.None),
                isGlobalEnabled: true)],
            allowsGlobalRegistration: true,
            allowsExternalSequence: false),
    ];

    private static InputActionDescriptor MenuKey(
        string id,
        string key,
        InputModifiers modifiers) =>
        Descriptor(id, InputBindingScope.MenuColumn, [InputGesture.KeyChord(key, modifiers)]);

    private static InputActionDescriptor MenuKeys(
        string id,
        IEnumerable<(string Key, InputModifiers Modifiers)> keys) =>
        Descriptor(
            id,
            InputBindingScope.MenuColumn,
            keys.Select(key => InputGesture.KeyChord(key.Key, key.Modifiers)));

    private static InputActionDescriptor Descriptor(
        string id,
        InputBindingScope scope,
        IEnumerable<InputGesture> gestures)
    {
        ImmutableArray<InputBinding> defaults = gestures
            .Select(gesture => new InputBinding(id, scope, gesture))
            .ToImmutableArray();
        return new InputActionDescriptor(
            id,
            $"Input.Action.{id}",
            id.StartsWith("Menu", StringComparison.Ordinal)
                ? "Input.Category.Menu"
                : "Input.Category.Canvas",
            [scope],
            defaults,
            allowsGlobalRegistration: false,
            allowsExternalSequence: false);
    }
}
