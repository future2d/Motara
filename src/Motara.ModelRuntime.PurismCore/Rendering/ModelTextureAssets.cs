using System.Collections.Immutable;
using System.Security.Cryptography;
using Motara.ModelRuntime.Abstractions;
using SkiaSharp;

namespace Motara.ModelRuntime.PurismCore;

internal sealed class ModelTextureAssets : IDisposable
{
    private const int MaximumDimension = 16_384;
    private const long MaximumPixels = 64L * 1024 * 1024;
    private const long MaximumEncodedBytes = 128L * 1024 * 1024;
    private readonly object stateGate = new();
    private ImmutableArray<byte[]> encodedTextures;
    private int activeDecodeCount;

    private ModelTextureAssets(ImmutableArray<byte[]> encodedTextures)
    {
        this.encodedTextures = encodedTextures;
    }

    internal int Count
    {
        get
        {
            lock (stateGate)
            {
                return IsDisposed ? 0 : encodedTextures.Length;
            }
        }
    }

    internal bool IsDisposed { get; private set; }

    internal static async Task<ModelTextureAssets> LoadAsync(
        IModelAssetSource assets,
        ImmutableArray<string> textureAssetIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assets);
        if (textureAssetIds.IsDefault)
        {
            throw new ArgumentException("Texture asset IDs must be initialized.", nameof(textureAssetIds));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var encoded = ImmutableArray.CreateBuilder<byte[]>(textureAssetIds.Length);
        try
        {
            foreach (string assetId in textureAssetIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long length = await assets.GetLengthAsync(assetId, cancellationToken).ConfigureAwait(false);
                ValidateEncodedLength(length);
                byte[] bytes = new byte[(int)length];
                await using Stream stream = await assets.OpenReadAsync(assetId, cancellationToken)
                    .ConfigureAwait(false);
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                encoded.Add(bytes);
            }

            return new ModelTextureAssets(encoded.MoveToImmutable());
        }
        catch
        {
            Clear(encoded);
            throw;
        }
    }

    internal static async Task<ModelTextureAssets> LoadAsync(
        ImmutableArray<string> texturePaths,
        CancellationToken cancellationToken)
    {
        if (texturePaths.IsDefault)
        {
            throw new ArgumentException("Texture paths must be initialized.", nameof(texturePaths));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var encoded = ImmutableArray.CreateBuilder<byte[]>(texturePaths.Length);
        try
        {
            foreach (string path in texturePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(path);
                var file = new FileInfo(path);
                if (!file.Exists)
                {
                    throw new FileNotFoundException("Texture file was not found.", path);
                }

                ValidateEncodedLength(file.Length);
                encoded.Add(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
            }

            return new ModelTextureAssets(encoded.MoveToImmutable());
        }
        catch
        {
            Clear(encoded);
            throw;
        }
    }

    internal Task<CpuTextureSet> DecodeCpuTexturesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImmutableArray<byte[]> snapshot;
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            activeDecodeCount++;
            snapshot = encodedTextures;
        }

        try
        {
            return Task.Run(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var decoded = ImmutableArray.CreateBuilder<SKBitmap>(snapshot.Length);
                    try
                    {
                        foreach (byte[] encoded in snapshot)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            decoded.Add(Decode(encoded));
                        }

                        return CpuTextureSet.Create(decoded.MoveToImmutable());
                    }
                    catch
                    {
                        foreach (SKBitmap bitmap in decoded)
                        {
                            bitmap.Dispose();
                        }

                        throw;
                    }
                }
                finally
                {
                    ReleaseDecodeLease();
                }
            }, CancellationToken.None);
        }
        catch
        {
            ReleaseDecodeLease();
            throw;
        }
    }

    public void Dispose()
    {
        ImmutableArray<byte[]> released;
        lock (stateGate)
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            if (activeDecodeCount == 0)
            {
                released = encodedTextures;
                encodedTextures = [];
            }
            else
            {
                released = [];
            }
        }

        Clear(released);
    }

    private void ReleaseDecodeLease()
    {
        ImmutableArray<byte[]> released = [];
        lock (stateGate)
        {
            activeDecodeCount--;
            if (activeDecodeCount == 0 && IsDisposed)
            {
                released = encodedTextures;
                encodedTextures = [];
            }
        }

        Clear(released);
    }

    private static void ValidateEncodedLength(long length)
    {
        if (length <= 0 || length > MaximumEncodedBytes || length > int.MaxValue)
        {
            throw new InvalidDataException("Texture file size is invalid.");
        }
    }

    private static SKBitmap Decode(byte[] encoded)
    {
        using SKData data = SKData.CreateCopy(encoded);
        using SKCodec codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("Texture data is not a supported image.");
        SKImageInfo info = codec.Info;
        if (info.Width <= 0
            || info.Height <= 0
            || info.Width > MaximumDimension
            || info.Height > MaximumDimension
            || (long)info.Width * info.Height > MaximumPixels)
        {
            throw new InvalidDataException("Decoded texture dimensions are invalid.");
        }

        var bitmap = new SKBitmap(new SKImageInfo(
            info.Width,
            info.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul));
        SKCodecResult result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result is not SKCodecResult.Success)
        {
            bitmap.Dispose();
            throw new InvalidDataException("Texture decoding failed.");
        }

        return bitmap;
    }

    private static void Clear(IEnumerable<byte[]> encodedTextures)
    {
        foreach (byte[] encoded in encodedTextures)
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }
}
