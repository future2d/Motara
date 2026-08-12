using System.Text.Json.Serialization;
using Motara.Media;

namespace Motara.Persistence;

public enum BackgroundKind
{
    Solid = 0,
    Image = 1,
    Video = 2,
    Signal = 3,
}

public sealed record VideoSignalSourceSelection
{
    public VideoSignalSourceSelection(VideoSignalProtocol protocol, string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        Protocol = protocol;
        SourceId = sourceId.Trim();
    }

    public VideoSignalProtocol Protocol { get; }
    public string SourceId { get; }
}

public enum BackgroundLayoutMode
{
    Fill = 0,
    Fit = 1,
    Stretch = 2,
    Center = 3,
    Tile = 4,
}

public sealed record BackgroundDefinition
{
    private static readonly string[] SupportedImageExtensions = [".png", ".jpg", ".webp", ".bmp"];
    private static readonly string[] SupportedVideoExtensions = [".mp4", ".mov", ".webm", ".mkv", ".avi", ".m4v"];

    [JsonConstructor]
    public BackgroundDefinition(
        BackgroundKind kind,
        string solidColor,
        string? imageAssetId,
        BackgroundLayoutMode layout,
        string? videoAssetId = null,
        BackgroundVideoOptions? videoOptions = null,
        VideoSignalSourceSelection? signalSource = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!Enum.IsDefined(layout))
        {
            throw new ArgumentOutOfRangeException(nameof(layout));
        }

        string normalizedColor = NormalizeColor(solidColor);
        if (kind == BackgroundKind.Solid)
        {
            if (imageAssetId is not null || videoAssetId is not null || signalSource is not null)
            {
                throw new ArgumentException("Solid backgrounds cannot reference an image asset.", nameof(imageAssetId));
            }
        }
        else if (kind == BackgroundKind.Image)
        {
            ValidateImageAssetId(imageAssetId);
            if (videoAssetId is not null || signalSource is not null) throw new ArgumentException("Images cannot reference a video signal.", nameof(videoAssetId));
        }
        else if (kind == BackgroundKind.Video)
        {
            ValidateVideoAssetId(videoAssetId);
            if (imageAssetId is not null || signalSource is not null) throw new ArgumentException("Videos cannot reference an image asset or signal.", nameof(imageAssetId));
        }
        else
        {
            ArgumentNullException.ThrowIfNull(signalSource);
            if (imageAssetId is not null || videoAssetId is not null)
            {
                throw new ArgumentException("Signal backgrounds cannot reference file assets.", nameof(imageAssetId));
            }
        }

        Kind = kind;
        SolidColor = normalizedColor;
        ImageAssetId = imageAssetId;
        Layout = layout;
        VideoAssetId = videoAssetId;
        VideoOptions = videoOptions ?? BackgroundVideoOptions.Default;
        SignalSource = signalSource;
    }

    public BackgroundKind Kind { get; }

    public string SolidColor { get; }

    public string? ImageAssetId { get; }

    public string? VideoAssetId { get; }

    public BackgroundVideoOptions VideoOptions { get; }

    public BackgroundLayoutMode Layout { get; }

    public VideoSignalSourceSelection? SignalSource { get; }

    public static BackgroundDefinition Solid(string color) =>
        new(BackgroundKind.Solid, color, null, BackgroundLayoutMode.Fill);

    public static BackgroundDefinition Image(string assetId, BackgroundLayoutMode layout) =>
        new(BackgroundKind.Image, "#000000FF", assetId, layout, null);

    public static BackgroundDefinition Video(
        string assetId,
        BackgroundLayoutMode layout,
        BackgroundVideoOptions? options = null) =>
        new(BackgroundKind.Video, "#000000FF", null, layout, assetId, options);

    public static BackgroundDefinition Signal(
        VideoSignalSourceSelection source,
        BackgroundLayoutMode layout = BackgroundLayoutMode.Fill) =>
        new(BackgroundKind.Signal, "#000000FF", null, layout, null, null, source);

    public static void ValidateImageAssetId(string? assetId)
    {
        if (assetId is null || assetId.Length is < 68 or > 69)
        {
            throw new ArgumentException("Background image asset ID is invalid.", nameof(assetId));
        }

        string extension = Path.GetExtension(assetId);
        if (!SupportedImageExtensions.Contains(extension, StringComparer.Ordinal)
            || assetId.Length != 64 + extension.Length
            || !assetId.AsSpan(0, 64).ContainsOnlyLowerHex())
        {
            throw new ArgumentException("Background image asset ID is invalid.", nameof(assetId));
        }
    }

    public static void ValidateVideoAssetId(string? assetId)
    {
        if (assetId is null || assetId.Length is < 68 or > 69)
            throw new ArgumentException("Background video asset ID is invalid.", nameof(assetId));
        string extension = Path.GetExtension(assetId);
        if (!SupportedVideoExtensions.Contains(extension, StringComparer.Ordinal)
            || assetId.Length != 64 + extension.Length
            || !assetId.AsSpan(0, 64).ContainsOnlyLowerHex())
            throw new ArgumentException("Background video asset ID is invalid.", nameof(assetId));
    }

    internal static void Validate(BackgroundDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _ = new BackgroundDefinition(
            definition.Kind,
            definition.SolidColor,
            definition.ImageAssetId,
            definition.Layout,
            definition.VideoAssetId,
            definition.VideoOptions,
            definition.SignalSource);
    }

    private static string NormalizeColor(string color)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(color);
        if (color.Length is not (7 or 9)
            || color[0] != '#'
            || !color.AsSpan(1).ContainsOnlyHex())
        {
            throw new ArgumentException(
                "Background color must use #RRGGBB or #RRGGBBAA format.",
                nameof(color));
        }

        string normalized = color.ToUpperInvariant();
        return normalized.Length == 7 ? normalized + "FF" : normalized;
    }
}

internal static class BackgroundDefinitionHexExtensions
{
    internal static bool ContainsOnlyHex(this ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool ContainsOnlyLowerHex(this ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
