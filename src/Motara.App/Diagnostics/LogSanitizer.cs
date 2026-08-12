using System.Text.RegularExpressions;

namespace Motara.App.Diagnostics;

internal static partial class LogSanitizer
{
    internal static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        string sanitized = UriQueryRegex().Replace(value, "$1?[redacted]");
        sanitized = CredentialRegex().Replace(sanitized, "$1=[redacted]");
        sanitized = BearerRegex().Replace(sanitized, "Bearer [redacted]");
        sanitized = WindowsPathRegex().Replace(sanitized, "[path]");
        return UnixPathRegex().Replace(sanitized, "[path]");
    }

    [GeneratedRegex(@"(?i)(\b[a-z][a-z0-9+.-]*://[^\s?]+)\?[^\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex UriQueryRegex();

    [GeneratedRegex(@"(?i)\b(token|password|authorization|cookie|secret)\s*[:=]\s*(?:Bearer[ -])?[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialRegex();

    [GeneratedRegex(@"(?i)\bBearer\s+[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"(?i)(?:[a-z]:\\|\\\\)[^\s\""']+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"(?<![:\w])/(?:[^/\s]+/)*[^/\s\""']+", RegexOptions.CultureInvariant)]
    private static partial Regex UnixPathRegex();
}
