using System.Security.Cryptography;

namespace Motara.ModelRuntime.Abstractions;

public sealed class MemoryModelAssetSource : IModelAssetSource
{
    private Dictionary<string, byte[]>? assets;

    private MemoryModelAssetSource(Dictionary<string, byte[]> assets)
    {
        this.assets = assets;
    }

    public static MemoryModelAssetSource Create(IReadOnlyDictionary<string, byte[]> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var owned = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach ((string assetId, byte[] bytes) in assets)
            {
                ArgumentNullException.ThrowIfNull(bytes);
                string normalized = ModelAssetId.Normalize(assetId);
                if (!owned.TryAdd(normalized, bytes.ToArray()))
                {
                    throw new ArgumentException("Model asset IDs must be unique.", nameof(assets));
                }
            }

            return new MemoryModelAssetSource(owned);
        }
        catch
        {
            ZeroAssets(owned);
            throw;
        }
    }

    public ValueTask<long> GetLengthAsync(
        string assetId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = GetAsset(assetId);
        return ValueTask.FromResult((long)bytes.Length);
    }

    public ValueTask<Stream> OpenReadAsync(
        string assetId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = GetAsset(assetId);
        Stream stream = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: false);
        return ValueTask.FromResult(stream);
    }

    public ValueTask DisposeAsync()
    {
        Dictionary<string, byte[]>? owned = Interlocked.Exchange(ref assets, null);
        if (owned is not null)
        {
            ZeroAssets(owned);
        }

        return ValueTask.CompletedTask;
    }

    private byte[] GetAsset(string assetId)
    {
        string normalized = ModelAssetId.Normalize(assetId);
        Dictionary<string, byte[]> owned = assets
            ?? throw new ObjectDisposedException(nameof(MemoryModelAssetSource));
        return owned.TryGetValue(normalized, out byte[]? bytes)
            ? bytes
            : throw new FileNotFoundException("The model asset was not declared.", normalized);
    }

    private static void ZeroAssets(Dictionary<string, byte[]> owned)
    {
        foreach (byte[] bytes in owned.Values)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        owned.Clear();
    }
}
