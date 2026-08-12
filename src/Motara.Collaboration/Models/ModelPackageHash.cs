using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Motara.Collaboration.Models;

internal static class ModelPackageHash
{
    internal static ModelContentId ComputeModelContentId(
        ImmutableArray<ModelPackageFile> files) =>
        ModelContentId.Parse(PackageHash.Format(HashProjection(files, includeMappings: false)));

    internal static PackageContentId ComputePackageContentId(
        ImmutableArray<ModelPackageFile> files) =>
        PackageContentId.Parse(PackageHash.Format(HashProjection(files, includeMappings: true)));

    private static byte[] HashProjection(
        ImmutableArray<ModelPackageFile> files,
        bool includeMappings)
    {
        using var projection = new MemoryStream();
        using (var writer = new BinaryWriter(projection, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(ModelPackageManifest.SchemaVersion);
            foreach (ModelPackageFile file in files)
            {
                if (!includeMappings && file.Kind == ModelPackageAssetKind.ParameterMapping)
                {
                    continue;
                }

                writer.Write((int)file.Kind);
                writer.Write(file.AssetId);
                writer.Write(file.Name ?? string.Empty);
                writer.Write(file.Group ?? string.Empty);
                writer.Write(file.Length);
                writer.Write(file.Sha256.AsSpan());
            }
        }

        return SHA256.HashData(projection.GetBuffer().AsSpan(0, checked((int)projection.Length)));
    }
}
