using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Motara.Tracking.iFacialMocap;

/// <summary>Parses bounded iFacialMocap UDP payloads independently from socket ownership.</summary>
public static class IFacialMocapPacketParser
{
    /// <summary>Maximum accepted UDP payload size, matching the official receive example.</summary>
    public const int MaximumPacketBytes = 8192;

    private const int MaximumFieldCount = 128;
    private const int MaximumBlendShapeCount = 96;
    private const int MaximumNameLength = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Attempts to parse one UTF-8 protocol payload.</summary>
    public static bool TryParse(
        ReadOnlySpan<byte> utf8Packet,
        out IFacialMocapPacket? packet)
    {
        packet = null;
        if (utf8Packet.IsEmpty || utf8Packet.Length > MaximumPacketBytes)
        {
            return false;
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(utf8Packet);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        string[] fields = text.Split(
            '|',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length == 0 || fields.Length > MaximumFieldCount)
        {
            return false;
        }

        var blendShapes = ImmutableDictionary.CreateBuilder<string, double>(StringComparer.Ordinal);
        IFacialMocapHeadPose? head = null;
        IFacialMocapEulerAngles? rightEye = null;
        IFacialMocapEulerAngles? leftEye = null;

        foreach (string field in fields)
        {
            if (field.StartsWith("=head#", StringComparison.Ordinal))
            {
                head = TryParseHead(field.AsSpan("=head#".Length));
                continue;
            }

            if (field.StartsWith("rightEye#", StringComparison.Ordinal))
            {
                rightEye = TryParseEuler(field.AsSpan("rightEye#".Length));
                continue;
            }

            if (field.StartsWith("leftEye#", StringComparison.Ordinal))
            {
                leftEye = TryParseEuler(field.AsSpan("leftEye#".Length));
                continue;
            }

            if (TryParseBlendShape(field.AsSpan(), out string? name, out double value))
            {
                blendShapes[name] = value;
                if (blendShapes.Count > MaximumBlendShapeCount)
                {
                    return false;
                }
            }
        }

        if (blendShapes.Count == 0 && head is null)
        {
            return false;
        }

        packet = new IFacialMocapPacket(
            blendShapes.ToImmutable(),
            head,
            rightEye,
            leftEye);
        return true;
    }

    private static bool TryParseBlendShape(
        ReadOnlySpan<char> field,
        out string name,
        out double value)
    {
        name = string.Empty;
        value = 0;
        int separator = field.IndexOf('&');
        if (separator < 0)
        {
            separator = field.IndexOf('-');
        }

        if (separator <= 0 || separator >= field.Length - 1)
        {
            return false;
        }

        ReadOnlySpan<char> nameSpan = field[..separator].Trim();
        if (nameSpan.IsEmpty
            || nameSpan.Length > MaximumNameLength
            || !IsParameterName(nameSpan)
            || !TryParseFinite(field[(separator + 1)..], out value))
        {
            return false;
        }

        name = nameSpan.ToString();
        return true;
    }

    private static IFacialMocapHeadPose? TryParseHead(ReadOnlySpan<char> values)
    {
        if (!TryParseNumbers(values, 6, out double[] parsed))
        {
            return null;
        }

        return new IFacialMocapHeadPose(
            parsed[0],
            parsed[1],
            parsed[2],
            parsed[3],
            parsed[4],
            parsed[5]);
    }

    private static IFacialMocapEulerAngles? TryParseEuler(ReadOnlySpan<char> values)
    {
        if (!TryParseNumbers(values, 3, out double[] parsed))
        {
            return null;
        }

        return new IFacialMocapEulerAngles(parsed[0], parsed[1], parsed[2]);
    }

    private static bool TryParseNumbers(
        ReadOnlySpan<char> values,
        int expectedCount,
        out double[] parsed)
    {
        string[] segments = values.ToString().Split(',', StringSplitOptions.TrimEntries);
        parsed = new double[expectedCount];
        if (segments.Length != expectedCount)
        {
            return false;
        }

        for (int index = 0; index < expectedCount; index++)
        {
            if (!TryParseFinite(segments[index], out parsed[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseFinite(ReadOnlySpan<char> text, out double value) =>
        double.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value)
        && double.IsFinite(value);

    private static bool IsParameterName(ReadOnlySpan<char> name)
    {
        foreach (char character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }
}
