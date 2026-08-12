using System.Collections.Immutable;
using Motara.ModelRuntime.Abstractions;

namespace Motara.Collaboration.Models;

public enum ModelPackageAssetKind
{
    Descriptor,
    NativeModel,
    Texture,
    Physics,
    Pose,
    Motion,
    Expression,
    ParameterMapping,
}

public sealed record ModelPackageAsset
{
    public ModelPackageAsset(
        string assetId,
        ModelPackageAssetKind kind,
        string? name = null,
        string? group = null)
    {
        AssetId = ModelAssetId.Normalize(assetId);
        ModelPackageAssetMetadata.Validate(kind, name, group);
        Kind = kind;
        Name = name;
        Group = group;
    }

    public string AssetId { get; }

    public ModelPackageAssetKind Kind { get; }

    public string? Name { get; }

    public string? Group { get; }
}

public sealed record ModelPackageFile
{
    public ModelPackageFile(
        string assetId,
        ModelPackageAssetKind kind,
        long length,
        byte[] sha256,
        string? name = null,
        string? group = null)
    {
        AssetId = ModelAssetId.Normalize(assetId);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        ArgumentNullException.ThrowIfNull(sha256);
        if (sha256.Length != 32)
        {
            throw new ArgumentException("A file hash must contain 32 bytes.", nameof(sha256));
        }

        ModelPackageAssetMetadata.Validate(kind, name, group);
        Kind = kind;
        Length = length;
        Sha256 = [.. sha256];
        Name = name;
        Group = group;
    }

    public string AssetId { get; }

    public ModelPackageAssetKind Kind { get; }

    public long Length { get; }

    public ImmutableArray<byte> Sha256 { get; }

    public string? Name { get; }

    public string? Group { get; }
}

internal static class ModelPackageAssetMetadata
{
    internal static void Validate(
        ModelPackageAssetKind kind,
        string? name,
        string? group)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        switch (kind)
        {
            case ModelPackageAssetKind.Motion:
                Require(name, nameof(name));
                Require(group, nameof(group));
                break;

            case ModelPackageAssetKind.Pose:
            case ModelPackageAssetKind.Expression:
                Require(name, nameof(name));
                if (group is not null)
                {
                    throw new ArgumentException("Only motion assets can declare a group.", nameof(group));
                }

                break;

            default:
                if (name is not null || group is not null)
                {
                    throw new ArgumentException("Only auxiliary assets can declare animation metadata.");
                }

                break;
        }
    }

    private static void Require(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
    }
}
