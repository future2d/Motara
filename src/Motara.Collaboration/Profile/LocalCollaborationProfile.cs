namespace Motara.Collaboration.Profile;

public sealed record LocalCollaborationProfile(int SchemaVersion, string DisplayName)
{
    public const int CurrentSchemaVersion = 1;

    public static string NormalizeDisplayName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string normalized = value.Trim();
        if (normalized.Length is < 1 or > 80 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The collaboration display name is invalid.",
                nameof(value));
        }

        return normalized;
    }
}
