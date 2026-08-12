namespace Motara.Collaboration.Invites;

public static class InvitationLinkParser
{
    private const int MaximumInputLength = 8192;

    public static bool TryParse(string? text, out InvitationCandidate candidate)
    {
        candidate = default;
        if (string.IsNullOrEmpty(text)
            || text.Length > MaximumInputLength
            || !string.Equals(text, text.Trim(), StringComparison.Ordinal)
            || text.Contains('%', StringComparison.Ordinal)
            || !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
            || uri.UserInfo.Length != 0)
        {
            return false;
        }

        string[] segments = uri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? token = null;
        InvitationKind kind = default;
        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals("www.motara.org", StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && segments is ["invite", var webKind, var webToken]
            && TryParseKind(webKind, out kind))
        {
            token = webToken;
        }
        else if (uri.Scheme.Equals("motara", StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals("invite", StringComparison.OrdinalIgnoreCase)
            && uri.Port == -1
            && segments is [var appKind, var appToken]
            && TryParseKind(appKind, out kind))
        {
            token = appToken;
        }

        if (token is null || !IsTokenShape(token))
        {
            return false;
        }

        candidate = new InvitationCandidate(kind, token);
        return true;
    }

    private static bool TryParseKind(string value, out InvitationKind kind)
    {
        if (value.Equals("friend", StringComparison.Ordinal))
        {
            kind = InvitationKind.Friend;
            return true;
        }

        if (value.Equals("session", StringComparison.Ordinal))
        {
            kind = InvitationKind.Session;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool IsTokenShape(string token)
    {
        int dotCount = 0;
        foreach (char character in token)
        {
            if (character == '.')
            {
                dotCount++;
                continue;
            }

            if (!(character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-'
                or '_'))
            {
                return false;
            }
        }

        return dotCount == 2 && !token.StartsWith('.') && !token.EndsWith('.');
    }
}
