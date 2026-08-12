using System.Collections.Immutable;
using Motara.ModelRuntime.Abstractions;

namespace Motara.Collaboration.Models;

public sealed record ModelPackageChunk
{
    public ModelPackageChunk(
        PackageContentId packageContentId,
        ModelGeneration generation,
        string assetId,
        long offset,
        byte[] data,
        byte[] sha256)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(sha256);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (data.Length == 0)
        {
            throw new ArgumentException("A model package chunk cannot be empty.", nameof(data));
        }

        if (sha256.Length != 32)
        {
            throw new ArgumentException("A chunk hash must contain 32 bytes.", nameof(sha256));
        }

        PackageContentId = packageContentId;
        Generation = generation;
        AssetId = ModelAssetId.Normalize(assetId);
        Offset = offset;
        Data = [.. data];
        Sha256 = [.. sha256];
    }

    public PackageContentId PackageContentId { get; }

    public ModelGeneration Generation { get; }

    public string AssetId { get; }

    public long Offset { get; }

    public ImmutableArray<byte> Data { get; }

    public ImmutableArray<byte> Sha256 { get; }
}
