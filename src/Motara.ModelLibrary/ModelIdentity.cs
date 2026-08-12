using System.Text;

namespace Motara.ModelLibrary;

public readonly struct ModelId : IEquatable<ModelId>
{
    private ModelId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ModelId Create(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new ModelId(value.Normalize(NormalizationForm.FormC));
    }

    public bool Equals(ModelId other) =>
        StringComparer.OrdinalIgnoreCase.Equals(Value, other.Value);

    public override bool Equals(object? obj) => obj is ModelId other && Equals(other);

    public override int GetHashCode() =>
        Value is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    public override string ToString() => Value ?? string.Empty;

    public static bool operator ==(ModelId left, ModelId right) => left.Equals(right);

    public static bool operator !=(ModelId left, ModelId right) => !left.Equals(right);
}

public sealed record ModelIdentity(ModelId Id, string DisplayName)
{
    private static readonly HashSet<string> WindowsReservedNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static ModelIdentity FromDescriptorFilename(string filename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        if (!StringComparer.Ordinal.Equals(filename, Path.GetFileName(filename)))
        {
            throw new ArgumentException("Descriptor filename cannot contain a path.", nameof(filename));
        }

        const string fullSuffix = ".model3.json";
        string displayName;
        if (StringComparer.OrdinalIgnoreCase.Equals(filename, "model3.json"))
        {
            displayName = "model3";
        }
        else if (filename.EndsWith(fullSuffix, StringComparison.OrdinalIgnoreCase))
        {
            displayName = filename[..^fullSuffix.Length];
        }
        else
        {
            throw new ArgumentException("Descriptor filename is not supported.", nameof(filename));
        }

        displayName = displayName.Normalize(NormalizationForm.FormC);
        ValidateDisplayName(displayName, filename);
        return new ModelIdentity(ModelId.Create(displayName), displayName);
    }

    public static bool IsDescriptorFilename(string filename) =>
        StringComparer.OrdinalIgnoreCase.Equals(filename, "model3.json")
        || (filename.Length > ".model3.json".Length
            && filename.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase));

    private static void ValidateDisplayName(string displayName, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(displayName)
            || displayName is "." or ".."
            || displayName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || displayName.EndsWith(' ')
            || displayName.EndsWith('.'))
        {
            throw new ArgumentException("Model display name is invalid.", parameterName);
        }

        string deviceStem = displayName.Split('.', 2)[0];
        if (WindowsReservedNames.Contains(deviceStem))
        {
            throw new ArgumentException("Model display name is reserved.", parameterName);
        }
    }
}
