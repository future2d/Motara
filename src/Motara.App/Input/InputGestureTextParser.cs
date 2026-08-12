using Motara.Persistence;

namespace Motara.App.Input;

public static class InputGestureTextParser
{
    public static InputGesture Parse(
        InputGestureKind kind,
        string text,
        int sequenceTimeoutMilliseconds = 500)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        string normalized = text.Trim();
        return kind switch
        {
            InputGestureKind.KeyChord => ParsePrimary(normalized, mouse: false),
            InputGestureKind.KeySequence => InputGesture.KeySequence(
                normalized.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                sequenceTimeoutMilliseconds),
            InputGestureKind.MouseButton => ParsePrimary(normalized, mouse: true),
            InputGestureKind.Wheel => ParseAxis(normalized, touchpad: false),
            InputGestureKind.TouchpadAxis => ParseAxis(normalized, touchpad: true),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static InputGesture ParsePrimary(string text, bool mouse)
    {
        string[] parts = text.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException("The input gesture requires a key or mouse button.");
        }

        InputModifiers modifiers = ParseModifiers(parts.Take(parts.Length - 1));
        return mouse
            ? InputGesture.MouseButton(parts[^1], modifiers)
            : InputGesture.KeyChord(parts[^1], modifiers);
    }

    private static InputGesture ParseAxis(string text, bool touchpad)
    {
        int separator = text.LastIndexOf('+');
        string axisText = separator >= 0 ? text[(separator + 1)..] : text;
        InputModifiers modifiers = separator >= 0
            ? ParseModifiers(text[..separator].Split(
                '+',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : InputModifiers.None;
        string[] parts = axisText.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !Enum.TryParse(parts[0], ignoreCase: true, out InputAxis axis)
            || !Enum.TryParse(parts[1], ignoreCase: true, out InputDirection direction))
        {
            throw new ArgumentException("Use axis:direction, for example vertical:positive.");
        }

        return touchpad
            ? InputGesture.TouchpadAxis(axis, direction, modifiers)
            : InputGesture.Wheel(axis, direction, modifiers);
    }

    private static InputModifiers ParseModifiers(IEnumerable<string> values)
    {
        InputModifiers result = InputModifiers.None;
        foreach (string value in values)
        {
            result |= value.ToLowerInvariant() switch
            {
                "ctrl" or "control" => InputModifiers.Control,
                "alt" => InputModifiers.Alt,
                "shift" => InputModifiers.Shift,
                "meta" or "win" or "cmd" => InputModifiers.Meta,
                _ => throw new ArgumentException($"Unknown input modifier '{value}'."),
            };
        }

        return result;
    }
}
