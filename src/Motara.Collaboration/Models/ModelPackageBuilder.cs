using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.ModelRuntime.Abstractions;

namespace Motara.Collaboration.Models;

public sealed class ModelPackageBuilder
{
    private const int BufferSize = 64 * 1024;
    private readonly ILogger<ModelPackageBuilder> logger;

    public ModelPackageBuilder(ILogger<ModelPackageBuilder>? logger = null) =>
        this.logger = logger ?? NullLogger<ModelPackageBuilder>.Instance;

    public async Task<ModelPackageManifest> BuildAsync(
        IModelAssetSource source,
        ModelPackageInput input,
        ModelPackageLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);
        long started = Stopwatch.GetTimestamp();

        try
        {
            ModelPackageEvents.BuildStarted(logger, input.Assets.Length);
            ModelPackageAsset[] assets = ValidateAndOrderAssets(input.Assets, limits);
            long[] lengths = await PreflightLengthsAsync(
                source, assets, limits, cancellationToken).ConfigureAwait(false);
            var files = ImmutableArray.CreateBuilder<ModelPackageFile>(assets.Length);

            for (int index = 0; index < assets.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] hash = await HashAssetAsync(
                    source,
                    assets[index].AssetId,
                    lengths[index],
                    cancellationToken).ConfigureAwait(false);
                files.Add(new ModelPackageFile(
                    assets[index].AssetId,
                    assets[index].Kind,
                    lengths[index],
                    hash,
                    assets[index].Name,
                    assets[index].Group));
            }

            ImmutableArray<ModelPackageFile> completedFiles = files.MoveToImmutable();
            ModelContentId modelContentId = ModelPackageHash.ComputeModelContentId(completedFiles);
            PackageContentId packageContentId = ModelPackageHash.ComputePackageContentId(completedFiles);
            long totalBytes = lengths.Sum();
            ModelPackageEvents.BuildCompleted(
                logger,
                completedFiles.Length,
                totalBytes,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new ModelPackageManifest(
                input.ModelInstanceId,
                modelContentId,
                packageContentId,
                input.Generation,
                input.DisplayName,
                completedFiles);
        }
        catch (ModelPackageException exception)
        {
            ModelPackageEvents.BuildFailed(
                logger,
                exception.ErrorCode,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ModelPackageEvents.BuildUnexpectedFailure(
                logger,
                exception.GetType().Name,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    private static ModelPackageAsset[] ValidateAndOrderAssets(
        ImmutableArray<ModelPackageAsset> declaredAssets,
        ModelPackageLimits limits)
    {
        if (declaredAssets.Length > limits.MaxFileCount)
        {
            throw new ModelPackageException(
                ModelPackageErrorCode.FileCountLimitExceeded,
                "The model package exceeds the file-count limit.");
        }

        ModelPackageAsset[] assets = [.. declaredAssets];
        Array.Sort(assets, static (left, right) =>
            StringComparer.Ordinal.Compare(left.AssetId, right.AssetId));
        var uniqueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ModelPackageAsset asset in assets)
        {
            if (!uniqueIds.Add(asset.AssetId))
            {
                throw new ModelPackageException(
                    ModelPackageErrorCode.DuplicateAssetId,
                    "The model package contains duplicate normalized asset IDs.");
            }
        }

        return assets;
    }

    private static async Task<long[]> PreflightLengthsAsync(
        IModelAssetSource source,
        ModelPackageAsset[] assets,
        ModelPackageLimits limits,
        CancellationToken cancellationToken)
    {
        var lengths = new long[assets.Length];
        long totalLength = 0;
        for (int index = 0; index < assets.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long length = await source.GetLengthAsync(
                assets[index].AssetId, cancellationToken).ConfigureAwait(false);
            if (length < 0 || length > limits.MaxFileBytes)
            {
                throw new ModelPackageException(
                    ModelPackageErrorCode.FileSizeLimitExceeded,
                    "A model package asset exceeds the file-size limit.");
            }

            if (length > limits.MaxPackageBytes - totalLength)
            {
                throw new ModelPackageException(
                    ModelPackageErrorCode.PackageSizeLimitExceeded,
                    "The model package exceeds the aggregate size limit.");
            }

            lengths[index] = length;
            totalLength += length;
        }

        return lengths;
    }

    private static async Task<byte[]> HashAssetAsync(
        IModelAssetSource source,
        string assetId,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await source.OpenReadAsync(
            assetId, cancellationToken).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long actualLength = 0;
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(
                    buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                actualLength = checked(actualLength + read);
                if (actualLength > expectedLength)
                {
                    throw new ModelPackageException(
                        ModelPackageErrorCode.AssetLengthChanged,
                        "A model package asset length changed while it was being read.");
                }

                hash.AppendData(buffer.AsSpan(0, read));
            }

            if (actualLength != expectedLength)
            {
                throw new ModelPackageException(
                    ModelPackageErrorCode.AssetLengthChanged,
                    "A model package asset length changed while it was being read.");
            }

            return hash.GetHashAndReset();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

}
