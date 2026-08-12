using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Motara.ModelRuntime.Abstractions;

namespace Motara.Collaboration.Models;

public interface IRemoteModelPackage : IAsyncDisposable
{
}

public interface IRemoteModelPackageSource : IRemoteModelPackage
{
    ModelPackageManifest Manifest { get; }

    IModelAssetSource Assets { get; }
}

public sealed class RemoteModelPackage : IModelAssetSource, IRemoteModelPackageSource
{
    private Dictionary<string, byte[]>? assets;
    private readonly ILogger logger;

    internal RemoteModelPackage(ModelPackageManifest manifest, Dictionary<string, byte[]> assets, ILogger logger)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        this.assets = assets;
        this.logger = logger;
    }

    public ModelPackageManifest Manifest { get; }

    public IModelAssetSource Assets => this;

    public ValueTask<long> GetLengthAsync(string assetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult((long)GetAsset(assetId).Length);
    }

    public ValueTask<Stream> OpenReadAsync(string assetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = GetAsset(assetId);
        return ValueTask.FromResult<Stream>(
            new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: false));
    }

    public ValueTask DisposeAsync()
    {
        Dictionary<string, byte[]>? owned = Interlocked.Exchange(ref assets, null);
        if (owned is null)
        {
            return ValueTask.CompletedTask;
        }

        long releasedBytes = 0;
        foreach (byte[] bytes in owned.Values)
        {
            releasedBytes += bytes.Length;
            CryptographicOperations.ZeroMemory(bytes);
        }

        int fileCount = owned.Count;
        owned.Clear();
        ModelPackageTransferEvents.Released(logger, fileCount, releasedBytes);
        return ValueTask.CompletedTask;
    }

    private byte[] GetAsset(string assetId)
    {
        string normalized = ModelAssetId.Normalize(assetId);
        Dictionary<string, byte[]> current = assets
            ?? throw new ObjectDisposedException(nameof(RemoteModelPackage));
        return current.TryGetValue(normalized, out byte[]? bytes)
            ? bytes
            : throw new FileNotFoundException("The remote model asset was not declared.", normalized);
    }
}
