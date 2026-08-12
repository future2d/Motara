using System.Collections;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Motara.Collaboration.Models;

public sealed class RemoteModelPackageReceiver : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly ModelPackageManifest manifest;
    private readonly ILogger<RemoteModelPackageReceiver> logger;
    private readonly long started = Stopwatch.GetTimestamp();
    private Dictionary<string, AssetBuffer>? buffers;
    private long acceptedChunkCount;
    private ReceiverState state = ReceiverState.Accepting;

    private RemoteModelPackageReceiver(
        ModelPackageManifest manifest,
        Dictionary<string, AssetBuffer> buffers,
        ILogger<RemoteModelPackageReceiver> logger)
    {
        this.manifest = manifest;
        this.buffers = buffers;
        this.logger = logger;
    }

    public static RemoteModelPackageReceiver Begin(
        ModelPackageManifest manifest,
        ModelPackageLimits limits,
        ILogger<RemoteModelPackageReceiver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(limits);
        ILogger<RemoteModelPackageReceiver> actualLogger =
            logger ?? NullLogger<RemoteModelPackageReceiver>.Instance;
        try
        {
            ValidateManifest(manifest);
            Dictionary<string, AssetBuffer> buffers = CreateBuffers(manifest, limits);
            ModelPackageTransferEvents.Started(
                actualLogger,
                buffers.Count,
                buffers.Values.Sum(static buffer => (long)buffer.Bytes.Length));
            return new RemoteModelPackageReceiver(manifest, buffers, actualLogger);
        }
        catch (ModelPackageException exception)
        {
            ModelPackageTransferEvents.Rejected(actualLogger, exception.ErrorCode);
            throw;
        }
    }

    public ValueTask AcceptChunkAsync(
        ModelPackageChunk chunk,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateEnvelope(chunk);
        byte[] actualHash = SHA256.HashData(chunk.Data.AsSpan());
        if (!CryptographicOperations.FixedTimeEquals(actualHash, chunk.Sha256.AsSpan()))
        {
            throw Reject(ModelPackageErrorCode.ChunkHashMismatch, "The chunk hash does not match its data.");
        }

        lock (gate)
        {
            EnsureAccepting();
            Dictionary<string, AssetBuffer> current = buffers!;
            if (!current.TryGetValue(chunk.AssetId, out AssetBuffer? buffer))
            {
                throw Reject(ModelPackageErrorCode.AssetNotDeclared, "The chunk asset is not declared.");
            }

            if (chunk.Offset > buffer.Bytes.LongLength - chunk.Data.Length)
            {
                throw Reject(ModelPackageErrorCode.ChunkOutOfRange, "The chunk exceeds its declared asset range.");
            }

            int offset = checked((int)chunk.Offset);
            for (int index = 0; index < chunk.Data.Length; index++)
            {
                int destination = offset + index;
                if (buffer.Received[destination]
                    && buffer.Bytes[destination] != chunk.Data[index])
                {
                    throw Reject(ModelPackageErrorCode.ConflictingChunk, "A duplicate chunk conflicts with received data.");
                }
            }

            for (int index = 0; index < chunk.Data.Length; index++)
            {
                int destination = offset + index;
                if (!buffer.Received[destination])
                {
                    buffer.Bytes[destination] = chunk.Data[index];
                    buffer.Received[destination] = true;
                    buffer.ReceivedBytes++;
                }
            }

            acceptedChunkCount++;
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<RemoteModelPackage> CompleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            EnsureAccepting();
            if (buffers!.Values.Any(static buffer => buffer.ReceivedBytes != buffer.Bytes.LongLength))
            {
                throw Reject(ModelPackageErrorCode.PackageIncomplete, "The model package is incomplete.");
            }

            state = ReceiverState.Completing;
        }

        try
        {
            return await Task.Run(CompleteCore, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await AbortAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask AbortAsync()
    {
        Dictionary<string, AssetBuffer>? owned;
        lock (gate)
        {
            if (state is ReceiverState.Aborted or ReceiverState.Completed)
            {
                return ValueTask.CompletedTask;
            }

            state = ReceiverState.Aborted;
            owned = buffers;
            buffers = null;
        }

        long releasedBytes = ZeroBuffers(owned);
        ModelPackageTransferEvents.Aborted(logger, releasedBytes);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => AbortAsync();

    public bool IsComplete
    {
        get
        {
            lock (gate)
            {
                return state == ReceiverState.Accepting
                    && buffers is not null
                    && buffers.Values.All(static buffer => buffer.ReceivedBytes == buffer.Bytes.LongLength);
            }
        }
    }

    private RemoteModelPackage CompleteCore()
    {
        Dictionary<string, AssetBuffer> current;
        lock (gate)
        {
            if (state != ReceiverState.Completing || buffers is null)
            {
                throw Reject(ModelPackageErrorCode.ReceiverUnavailable, "The receiver is unavailable.");
            }

            current = buffers;
        }

        foreach (ModelPackageFile file in manifest.Files)
        {
            byte[] actualHash = SHA256.HashData(current[file.AssetId].Bytes);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, file.Sha256.AsSpan()))
            {
                throw Reject(ModelPackageErrorCode.ManifestHashMismatch, "A received asset does not match its manifest.");
            }
        }

        if (ModelPackageHash.ComputePackageContentId(manifest.Files) != manifest.PackageContentId)
        {
            throw Reject(ModelPackageErrorCode.ManifestHashMismatch, "The package manifest content ID is invalid.");
        }

        Dictionary<string, byte[]> packageAssets = current.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Bytes,
            StringComparer.OrdinalIgnoreCase);
        lock (gate)
        {
            if (state != ReceiverState.Completing)
            {
                throw Reject(ModelPackageErrorCode.ReceiverUnavailable, "The receiver was aborted.");
            }

            buffers = null;
            state = ReceiverState.Completed;
        }

        long totalBytes = packageAssets.Values.Sum(static bytes => (long)bytes.Length);
        ModelPackageTransferEvents.Completed(
            logger,
            packageAssets.Count,
            totalBytes,
            acceptedChunkCount,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new RemoteModelPackage(manifest, packageAssets, logger);
    }

    private void ValidateEnvelope(ModelPackageChunk chunk)
    {
        if (chunk.PackageContentId != manifest.PackageContentId)
        {
            throw Reject(ModelPackageErrorCode.PackageIdMismatch, "The chunk package ID is not current.");
        }

        if (chunk.Generation != manifest.Generation)
        {
            throw Reject(ModelPackageErrorCode.GenerationMismatch, "The chunk generation is not current.");
        }
    }

    private void EnsureAccepting()
    {
        if (state != ReceiverState.Accepting || buffers is null)
        {
            throw Reject(ModelPackageErrorCode.ReceiverUnavailable, "The receiver no longer accepts chunks.");
        }
    }

    private ModelPackageException Reject(ModelPackageErrorCode errorCode, string message)
    {
        ModelPackageTransferEvents.Rejected(logger, errorCode);
        return new ModelPackageException(errorCode, message);
    }

    private static Dictionary<string, AssetBuffer> CreateBuffers(
        ModelPackageManifest manifest,
        ModelPackageLimits limits)
    {
        if (manifest.Files.IsDefaultOrEmpty || manifest.Files.Length > limits.MaxFileCount)
        {
            throw new ModelPackageException(
                ModelPackageErrorCode.FileCountLimitExceeded,
                "The manifest exceeds the receiver file-count limit.");
        }

        var created = new Dictionary<string, AssetBuffer>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        try
        {
            foreach (ModelPackageFile file in manifest.Files)
            {
                if (file.Length > limits.MaxFileBytes || file.Length > int.MaxValue)
                {
                    throw new ModelPackageException(
                        ModelPackageErrorCode.FileSizeLimitExceeded,
                        "A manifest asset exceeds the receiver file-size limit.");
                }

                if (file.Length > limits.MaxPackageBytes - totalBytes)
                {
                    throw new ModelPackageException(
                        ModelPackageErrorCode.PackageSizeLimitExceeded,
                        "The manifest exceeds the receiver package-size limit.");
                }

                if (!created.TryAdd(file.AssetId, new AssetBuffer(checked((int)file.Length))))
                {
                    throw new ModelPackageException(
                        ModelPackageErrorCode.DuplicateAssetId,
                        "The manifest contains duplicate asset IDs.");
                }

                totalBytes += file.Length;
            }

            return created;
        }
        catch
        {
            ZeroBuffers(created);
            throw;
        }
    }

    private static void ValidateManifest(ModelPackageManifest manifest)
    {
        if (ModelPackageHash.ComputeModelContentId(manifest.Files) != manifest.ModelContentId
            || ModelPackageHash.ComputePackageContentId(manifest.Files) != manifest.PackageContentId)
        {
            throw new ModelPackageException(
                ModelPackageErrorCode.ManifestHashMismatch,
                "The model package manifest content IDs are invalid.");
        }

        string? previousAssetId = null;
        foreach (ModelPackageFile file in manifest.Files)
        {
            if (previousAssetId is not null
                && StringComparer.Ordinal.Compare(previousAssetId, file.AssetId) >= 0)
            {
                throw new ModelPackageException(
                    ModelPackageErrorCode.ManifestHashMismatch,
                    "The model package manifest files are not in canonical order.");
            }

            previousAssetId = file.AssetId;
        }
    }

    private static long ZeroBuffers(Dictionary<string, AssetBuffer>? owned)
    {
        if (owned is null)
        {
            return 0;
        }

        long releasedBytes = 0;
        foreach (AssetBuffer buffer in owned.Values)
        {
            releasedBytes += buffer.Bytes.Length;
            CryptographicOperations.ZeroMemory(buffer.Bytes);
        }

        owned.Clear();
        return releasedBytes;
    }

    private sealed class AssetBuffer(int length)
    {
        internal byte[] Bytes { get; } = new byte[length];

        internal BitArray Received { get; } = new(length);

        internal long ReceivedBytes { get; set; }
    }

    private enum ReceiverState
    {
        Accepting,
        Completing,
        Completed,
        Aborted,
    }
}
