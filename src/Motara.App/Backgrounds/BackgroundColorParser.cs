using Avalonia.Media;

namespace Motara.App.Backgrounds;

internal static class BackgroundColorParser
{
    internal static Color Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length is not (7 or 9)
            || value[0] != '#'
            || !value.AsSpan(1).ContainsOnlyHex())
        {
            throw new ArgumentException(
                "Background color must use #RRGGBB or #RRGGBBAA format.",
                nameof(value));
        }

        byte red = ParseByte(value, 1);
        byte green = ParseByte(value, 3);
        byte blue = ParseByte(value, 5);
        byte alpha = value.Length == 9 ? ParseByte(value, 7) : byte.MaxValue;
        return Color.FromArgb(alpha, red, green, blue);
    }

    private static byte ParseByte(string value, int offset) =>
        Convert.ToByte(value.Substring(offset, 2), 16);

    private static bool ContainsOnlyHex(this ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
