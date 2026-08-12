namespace Motara.App.Collaboration;

internal static class InvitationLinkDisplay
{
    private const int VisibleTokenSuffixLength = 6;

    internal static string Format(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        int tokenStart = value.LastIndexOf('/') + 1;
        if (tokenStart <= 0 || tokenStart >= value.Length)
        {
            return "...";
        }

        int tokenLength = value.Length - tokenStart;
        int suffixLength = Math.Min(
            VisibleTokenSuffixLength,
            Math.Max(0, (tokenLength - 1) / 2));
        return suffixLength == 0
            ? $"{value[..tokenStart]}..."
            : $"{value[..tokenStart]}...{value[^suffixLength..]}";
    }
}
