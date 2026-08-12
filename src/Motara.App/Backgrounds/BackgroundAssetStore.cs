using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Motara.ModelLibrary;
using Motara.Media;
using Motara.Persistence;
using SkiaSharp;

namespace Motara.App.Backgrounds;

internal sealed class BackgroundAssetStore : IBackgroundAssetStore
{
    internal const long MaximumEncodedBytes = 64L * 1024 * 1024;
    internal const long MaximumVideoEncodedBytes = 4L * 1024 * 1024 * 1024;
    internal const long MaximumDecodedPixels = 64L * 1024 * 1024;
    private const int CopyBufferSize = 128 * 1024;

    private readonly string backgroundsRoot;
    private readonly ILogger<BackgroundAssetStore> logger;
    private readonly IVideoDecoder videoDecoder;

    internal BackgroundAssetStore(
        IAppDataPaths paths,
        ILogger<BackgroundAssetStore> logger)
        : this(paths, logger, CreateDefaultVideoDecoder())
    {
    }

    internal BackgroundAssetStore(
        IAppDataPaths paths,
        ILogger<BackgroundAssetStore> logger,
        IVideoDecoder videoDecoder)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        backgroundsRoot = Path.Combine(paths.DataRoot, "Backgrounds");
        this.logger = logger;
        this.videoDecoder = videoDecoder ?? throw new ArgumentNullException(nameof(videoDecoder));
    }

    public Task<BackgroundAssetImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return Task.Run(
            () => ImportCoreAsync(sourcePath, cancellationToken),
            CancellationToken.None);
    }

    public string GetManagedPath(string assetId)
    {
        BackgroundDefinition.ValidateImageAssetId(assetId);
        return Path.Combine(backgroundsRoot, assetId);
    }

    public string GetManagedVideoPath(string assetId)
    {
        BackgroundDefinition.ValidateVideoAssetId(assetId);
        return Path.Combine(backgroundsRoot, assetId);
    }

    public Task<BackgroundAssetImportResult> ImportVideoAsync(string sourcePath, CancellationToken cancellationToken) =>
        Task.Run(() => ImportVideoCoreAsync(sourcePath, cancellationToken), CancellationToken.None);

    private async Task<BackgroundAssetImportResult> ImportVideoCoreAsync(string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        BackgroundAssetStoreLog.VideoImportStarted(logger);
        string? temporaryPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (extension is not (".mp4" or ".mov" or ".webm" or ".mkv" or ".avi" or ".m4v"))
            {
                throw new InvalidDataException("Background video format is unsupported.");
            }

            Directory.CreateDirectory(backgroundsRoot);
            temporaryPath = Path.Combine(backgroundsRoot, $".import-{Guid.NewGuid():N}.tmp");
            (byte[] hash, long byteCount) = await CopyAndHashAsync(
                Path.GetFullPath(sourcePath),
                temporaryPath,
                MaximumVideoEncodedBytes,
                "video",
                cancellationToken).ConfigureAwait(false);
            VideoStreamInfo stream = await videoDecoder.ProbeAsync(temporaryPath, cancellationToken)
                .ConfigureAwait(false);
            string assetId = Convert.ToHexStringLower(hash) + extension;
            string targetPath = GetManagedVideoPath(assetId);
            try
            {
                File.Move(temporaryPath, targetPath, overwrite: false);
                temporaryPath = null;
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                BackgroundAssetStoreLog.VideoImportReused(logger, assetId);
            }

            BackgroundAssetStoreLog.VideoImportCompleted(
                logger,
                assetId,
                extension[1..],
                stream.Width,
                stream.Height,
                stream.FramesPerSecond,
                stream.HasAlpha,
                byteCount);
            return new BackgroundAssetImportResult(
                assetId,
                targetPath,
                extension[1..],
                stream.Width,
                stream.Height);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            BackgroundAssetStoreLog.VideoImportCancelled(logger);
            throw;
        }
        catch (Exception exception)
        {
            BackgroundAssetStoreLog.VideoImportFailed(logger, exception.GetType().Name);
            throw;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private async Task<BackgroundAssetImportResult> ImportCoreAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        BackgroundAssetStoreLog.ImportStarted(logger);
        string? temporaryPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedSourcePath = Path.GetFullPath(sourcePath);
            Directory.CreateDirectory(backgroundsRoot);
            temporaryPath = Path.Combine(backgroundsRoot, $".import-{Guid.NewGuid():N}.tmp");

            (byte[] hash, long byteCount) = await CopyAndHashAsync(
                normalizedSourcePath,
                temporaryPath,
                MaximumEncodedBytes,
                "image",
                cancellationToken).ConfigureAwait(false);
            long decodeStarted = Stopwatch.GetTimestamp();
            DecodedImageInfo decoded = DecodeAndValidate(temporaryPath, cancellationToken);
            long decodeMilliseconds = (long)Stopwatch.GetElapsedTime(decodeStarted).TotalMilliseconds;
            string hashText = Convert.ToHexStringLower(hash);
            string assetId = hashText + decoded.Extension;
            string targetPath = GetManagedPath(assetId);

            bool reused = false;
            try
            {
                File.Move(temporaryPath, targetPath, overwrite: false);
                temporaryPath = null;
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                reused = true;
            }

            if (reused)
            {
                BackgroundAssetStoreLog.ImportReused(logger, assetId);
            }

            BackgroundAssetStoreLog.ImportCompleted(
                logger,
                assetId,
                decoded.Extension[1..],
                decoded.Width,
                decoded.Height,
                byteCount,
                decodeMilliseconds);
            return new BackgroundAssetImportResult(
                assetId,
                targetPath,
                decoded.Extension[1..],
                decoded.Width,
                decoded.Height);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            BackgroundAssetStoreLog.ImportCancelled(logger);
            throw;
        }
        catch (Exception exception)
        {
            BackgroundAssetStoreLog.ImportFailed(logger, exception.GetType().Name);
            throw;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // A later import can safely ignore an orphaned uniquely named temporary file.
                }
                catch (UnauthorizedAccessException)
                {
                    // The original import failure remains the actionable error.
                }
            }
        }
    }

    private static async Task<(byte[] Hash, long ByteCount)> CopyAndHashAsync(
        string sourcePath,
        string temporaryPath,
        long maximumBytes,
        string assetKind,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length <= 0 || source.Length > maximumBytes)
        {
            throw new InvalidDataException($"Background {assetKind} encoded size is invalid.");
        }

        await using var destination = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long byteCount = 0;
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(
                    buffer.AsMemory(0, CopyBufferSize),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                byteCount += read;
                if (byteCount > maximumBytes)
                {
                    throw new InvalidDataException($"Background {assetKind} encoded size is invalid.");
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            return (hash.GetHashAndReset(), byteCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static DecodedImageInfo DecodeAndValidate(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SKCodec codec = SKCodec.Create(path)
            ?? throw new InvalidDataException("Background image is not a supported image.");
        SKImageInfo sourceInfo = codec.Info;
        if (sourceInfo.Width <= 0
            || sourceInfo.Height <= 0
            || (long)sourceInfo.Width * sourceInfo.Height > MaximumDecodedPixels)
        {
            throw new InvalidDataException("Background image decoded dimensions are invalid.");
        }

        string extension = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => ".png",
            SKEncodedImageFormat.Jpeg => ".jpg",
            SKEncodedImageFormat.Webp => ".webp",
            SKEncodedImageFormat.Bmp => ".bmp",
            _ => throw new InvalidDataException("Background image encoding is unsupported."),
        };
        var decodeInfo = new SKImageInfo(
            sourceInfo.Width,
            sourceInfo.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var bitmap = new SKBitmap(decodeInfo);
        SKCodecResult decodeResult = codec.GetPixels(decodeInfo, bitmap.GetPixels());
        if (decodeResult != SKCodecResult.Success)
        {
            throw new InvalidDataException("Background image decoding failed.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new DecodedImageInfo(extension, sourceInfo.Width, sourceInfo.Height);
    }

    private readonly record struct DecodedImageInfo(string Extension, int Width, int Height);

    private static FfmpegVideoDecoder CreateDefaultVideoDecoder()
    {
        string ffmpegRoot = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        return new FfmpegVideoDecoder(
            Path.Combine(ffmpegRoot, "ffprobe.exe"),
            Path.Combine(ffmpegRoot, "ffmpeg.exe"));
    }
}
