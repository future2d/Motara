using System.Diagnostics.CodeAnalysis;

namespace Motara.Collaboration.Models;

public readonly record struct ModelInstanceId
{
    public ModelInstanceId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A model instance ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct ModelGeneration
{
    public ModelGeneration(ulong value)
    {
        if (value == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A model generation starts at one.");
        }

        Value = value;
    }

    public ulong Value { get; }

    public ModelGeneration Next() => new(checked(Value + 1));
}

public readonly record struct ModelContentId
{
    private ModelContentId(string value) => Value = value;

    public string Value { get; }

    public static ModelContentId Parse(string value) =>
        new(PackageHash.Parse(value, nameof(value)));

    public override string ToString() => Value;
}

public readonly record struct PackageContentId
{
    private PackageContentId(string value) => Value = value;

    public string Value { get; }

    public static PackageContentId Parse(string value) =>
        new(PackageHash.Parse(value, nameof(value)));

    public override string ToString() => Value;
}

internal static class PackageHash
{
    internal const string Prefix = "sha256-v1:";

    internal static string Format(ReadOnlySpan<byte> hash) =>
        Prefix + Convert.ToHexStringLower(hash);

    internal static string Parse(
        [NotNull] string? value,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != Prefix.Length + 64
            || !value.StartsWith(Prefix, StringComparison.Ordinal)
            || !value.AsSpan(Prefix.Length).ContainsOnlyHexDigits())
        {
            throw new ArgumentException("A content ID must be a schema-1 SHA-256 value.", parameterName);
        }

        return value;
    }

    private static bool ContainsOnlyHexDigits(this ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
