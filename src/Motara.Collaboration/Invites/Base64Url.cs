namespace Motara.Collaboration.Invites;

internal static class Base64Url
{
    internal static string Encode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    internal static bool TryDecode(
        string? value,
        int maximumDecodedLength,
        out byte[] decoded)
    {
        decoded = [];
        if (value is null || maximumDecodedLength < 0)
        {
            return false;
        }

        int maximumEncodedLength = checked(((maximumDecodedLength + 2) / 3) * 4);
        if (value.Length > maximumEncodedLength)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!(character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-'
                or '_'))
            {
                return false;
            }
        }

        int paddingLength = (4 - (value.Length % 4)) % 4;
        string base64 = value.Replace('-', '+').Replace('_', '/') + new string('=', paddingLength);
        try
        {
            byte[] candidate = Convert.FromBase64String(base64);
            if (candidate.Length > maximumDecodedLength)
            {
                return false;
            }

            decoded = candidate;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
