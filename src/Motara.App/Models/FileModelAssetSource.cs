using System.Collections.Immutable;
using Motara.ModelLibrary;
using Motara.ModelRuntime.Abstractions;

namespace Motara.App.Models;

internal sealed class FileModelAssetSource : IModelAssetSource
{
    private readonly Dictionary<string, string> paths;

    private FileModelAssetSource(
        Dictionary<string, string> paths,
        string descriptorAssetId,
        string nativeModelAssetId,
        ImmutableArray<string> textureAssetIds)
    {
        this.paths = paths;
        DescriptorAssetId = descriptorAssetId;
        NativeModelAssetId = nativeModelAssetId;
        TextureAssetIds = textureAssetIds;
    }

    internal string DescriptorAssetId { get; }

    internal string NativeModelAssetId { get; }

    internal ImmutableArray<string> TextureAssetIds { get; }

    internal static FileModelAssetSource Create(ModelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        const string descriptorAssetId = "model/model3.json";
        const string nativeModelAssetId = "model/model.moc3";
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [descriptorAssetId] = Path.GetFullPath(descriptor.DescriptorPath),
            [nativeModelAssetId] = Path.GetFullPath(descriptor.NativeModelPath),
        };
        var textureIds = ImmutableArray.CreateBuilder<string>(descriptor.TexturePaths.Length);
        for (int index = 0; index < descriptor.TexturePaths.Length; index++)
        {
            string extension = Path.GetExtension(descriptor.TexturePaths[index]);
            string assetId = ModelAssetId.Normalize($"textures/{index}{extension}");
            if (!paths.TryAdd(assetId, Path.GetFullPath(descriptor.TexturePaths[index])))
            {
                throw new ArgumentException("Model texture assets must be unique.", nameof(descriptor));
            }

            textureIds.Add(assetId);
        }

        foreach (ModelAuxiliaryAsset asset in descriptor.AuxiliaryAssets)
        {
            string path = ResolveAuxiliaryPath(descriptor.RootPath, asset.AssetId);
            if (paths.TryGetValue(asset.AssetId, out string? existingPath))
            {
                if (!string.Equals(existingPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Model auxiliary asset ids must be unique.", nameof(descriptor));
                }

                continue;
            }

            paths.Add(asset.AssetId, path);
        }

        return new FileModelAssetSource(
            paths,
            descriptorAssetId,
            nativeModelAssetId,
            textureIds.MoveToImmutable());
    }

    public ValueTask<long> GetLengthAsync(string assetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new FileInfo(GetPath(assetId)).Length);
    }

    public ValueTask<Stream> OpenReadAsync(string assetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(
            GetPath(assetId),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string ResolveAuxiliaryPath(string rootPath, string assetId)
    {
        string root = Path.GetFullPath(rootPath);
        string path = Path.GetFullPath(Path.Combine(
            root,
            assetId.Replace('/', Path.DirectorySeparatorChar)));
        string relative = Path.GetRelativePath(root, path);
        if (Path.IsPathFullyQualified(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Model auxiliary assets must remain within the model root.", nameof(assetId));
        }

        return path;
    }

    private string GetPath(string assetId) => paths.TryGetValue(
        ModelAssetId.Normalize(assetId),
        out string? path)
        ? path
        : throw new FileNotFoundException("The model asset was not declared.");
}
