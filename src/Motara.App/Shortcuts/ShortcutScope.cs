using Motara.Persistence;

namespace Motara.App.Shortcuts;

/// <summary>Runtime scope used by the shortcut workspace. Persistence continues to use InputBindingScope.</summary>
public enum ShortcutTriggerScope
{
    Global = 0,
    Application = 1,
    Canvas = 2,
    MenuWorkspace = 3,
    MenuColumn = 4,
    CurrentSource = 5,
}

public static class ShortcutScope
{
    public static ShortcutTriggerScope FromBinding(InputBindingScope scope) => (ShortcutTriggerScope)(int)scope;

    public static InputBindingScope ToBinding(ShortcutTriggerScope scope) => (InputBindingScope)(int)scope;

    public static bool IsGlobal(ShortcutTriggerScope scope) => scope == ShortcutTriggerScope.Global;
}
