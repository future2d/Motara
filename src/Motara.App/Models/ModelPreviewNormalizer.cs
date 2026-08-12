using SkiaSharp;

namespace Motara.App.Models;

internal sealed class ModelPreviewNormalizer
{
    internal const int OutputSize = 512;
    private static readonly SKSamplingOptions Sampling = new(
        SKCubicResampler.Mitchell);
    private readonly long maximumDecodedPixels = 64L * 1024 * 1024;

    internal Task NormalizeAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        return Task.Run(
            () => NormalizeCore(sourcePath, targetPath, cancellationToken),
            cancellationToken);
    }

    internal static bool IsNormalized(string path)
    {
        try
        {
            using SKCodec? codec = SKCodec.Create(path);
            return codec is not null
                && codec.EncodedFormat == SKEncodedImageFormat.Png
                && codec.Info.Width == OutputSize
                && codec.Info.Height == OutputSize;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return false;
        }
    }

    private void NormalizeCore(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SKImageInfo sourceInfo;
        SKBitmap bitmap;
        using (SKCodec codec = SKCodec.Create(sourcePath)
            ?? throw new InvalidDataException("The model preview could not be decoded."))
        {
            sourceInfo = codec.Info;
            if (sourceInfo.Width <= 0
                || sourceInfo.Height <= 0
                || (long)sourceInfo.Width * sourceInfo.Height > maximumDecodedPixels)
            {
                throw new InvalidDataException("The model preview dimensions are invalid.");
            }

            var decodeInfo = new SKImageInfo(
                sourceInfo.Width,
                sourceInfo.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Premul);
            bitmap = new SKBitmap(decodeInfo);
            SKCodecResult decodeResult = codec.GetPixels(decodeInfo, bitmap.GetPixels());
            if (decodeResult is not SKCodecResult.Success and not SKCodecResult.IncompleteInput)
            {
                bitmap.Dispose();
                throw new InvalidDataException("The model preview could not be decoded.");
            }
        }

        using SKBitmap decodedBitmap = bitmap;
        using SKImage source = SKImage.FromBitmap(bitmap);
        using SKSurface surface = SKSurface.Create(new SKImageInfo(
            OutputSize,
            OutputSize,
            SKColorType.Bgra8888,
            SKAlphaType.Premul))
            ?? throw new InvalidOperationException("The model preview surface could not be created.");
        int cropSize = Math.Min(sourceInfo.Width, sourceInfo.Height);
        float left = (sourceInfo.Width - cropSize) / 2f;
        float top = (sourceInfo.Height - cropSize) / 2f;
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(
            source,
            new SKRect(left, top, left + cropSize, top + cropSize),
            new SKRect(0, 0, OutputSize, OutputSize),
            Sampling);
        surface.Canvas.Flush();
        cancellationToken.ThrowIfCancellationRequested();

        using SKImage normalized = surface.Snapshot();
        using SKData png = normalized.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("The model preview could not be encoded.");
        string targetDirectory = Path.GetDirectoryName(Path.GetFullPath(targetPath))
            ?? throw new InvalidOperationException("The model preview target requires a directory.");
        Directory.CreateDirectory(targetDirectory);
        string temporary = Path.Combine(targetDirectory, $".preview-{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream output = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                png.SaveTo(output);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
