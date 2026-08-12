namespace Motara.App.Tracking;

internal sealed record SourceMappingPaths(
    string AdapterId,
    string DirectoryPath,
    string DefaultPath,
    string SelectionPath)
{
    internal static SourceMappingPaths ForAdapter(string root, string adapterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ValidateAdapterId(adapterId);
        string directory = Path.GetFullPath(root);
        return new SourceMappingPaths(
            adapterId,
            directory,
            Path.Combine(directory, $"default.{adapterId}.mapping.motara.json"),
            Path.Combine(directory, $"selection.{adapterId}.motara.json"));
    }

    internal string CreateNamedPath(string name)
    {
        string suffix = GetFileSuffix(AdapterId);
        int maximumStemLength = 100 - suffix.Length;
        string candidate = Path.GetFileName(name.Trim());
        if (candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[..^suffix.Length];
        }
        else if (candidate.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[..^".json".Length];
        }

        string stem = SanitizeStem(candidate, maximumStemLength);
        if (stem is "default" or "using")
        {
            stem += "-profile";
        }

        return Path.Combine(DirectoryPath, stem + suffix);
    }

    internal static string GetFileSuffix(string adapterId)
    {
        ValidateAdapterId(adapterId);
        return $".{adapterId}.mapping.motara.json";
    }

    private static void ValidateAdapterId(string adapterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        if (adapterId.Any(static value =>
                !char.IsAsciiLetterLower(value)
                && !char.IsAsciiDigit(value)
                && value != '-'))
        {
            throw new ArgumentException(
                "Adapter identifiers must use lower-case ASCII letters, digits, or hyphens.",
                nameof(adapterId));
        }
    }

    private static string SanitizeStem(string value, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Span<char> buffer = stackalloc char[Math.Min(value.Length, maximumLength)];
        int length = 0;
        bool previousWasSeparator = false;
        foreach (char character in value.Trim())
        {
            if (length == buffer.Length)
            {
                break;
            }

            bool allowed = char.IsLetterOrDigit(character) || character is '-' or '_';
            char normalized = allowed ? character : '-';
            if (normalized == '-' && previousWasSeparator)
            {
                continue;
            }

            buffer[length++] = normalized;
            previousWasSeparator = normalized == '-';
        }

        string result = new string(buffer[..length]).Trim('-', '_');
        return string.IsNullOrWhiteSpace(result) ? "profile" : result;
    }
}
