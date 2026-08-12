using Motara.Persistence;

namespace Motara.App.Shortcuts;

internal static class ShortcutGestureFormatter
{
    internal static string Format(InputGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        var parts = new List<string>(5);
        AddModifier(InputModifiers.Control, "Ctrl");
        AddModifier(InputModifiers.Alt, "Alt");
        AddModifier(InputModifiers.Shift, "Shift");
        AddModifier(InputModifiers.Meta, "Win");
        parts.Add(gesture.Kind switch
        {
            InputGestureKind.KeyChord => gesture.Primary!,
            InputGestureKind.MouseButton => $"Mouse {gesture.Primary}",
            InputGestureKind.KeySequence => string.Join(" > ", gesture.Sequence),
            InputGestureKind.Wheel => $"Wheel {gesture.Axis} {gesture.Direction}",
            InputGestureKind.TouchpadAxis => $"Touchpad {gesture.Axis} {gesture.Direction}",
            _ => gesture.CanonicalText,
        });
        return string.Join(" + ", parts);

        void AddModifier(InputModifiers modifier, string text)
        {
            if (gesture.Modifiers.HasFlag(modifier)) parts.Add(text);
        }
    }
}
