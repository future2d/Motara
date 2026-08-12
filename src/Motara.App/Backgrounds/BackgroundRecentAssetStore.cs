using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Motara.ModelLibrary;
using Motara.Persistence;

namespace Motara.App.Backgrounds;

internal enum BackgroundRecentAssetKind
{
    Image = 0,
    Video = 1,
}

internal sealed record BackgroundRecentAsset(
    string AssetId,
    string DisplayName);

internal sealed record BackgroundRecentAssets(
    ImmutableArray<BackgroundRecentAsset> Images,
    ImmutableArray<BackgroundRecentAsset> Videos)
{
    internal static BackgroundRecentAssets Empty { get; } = new([], []);
}

internal interface IBackgroundRecentAssetStore
{
    Task<BackgroundRecentAssets> LoadAsync(CancellationToken cancellationToken);

    Task<BackgroundRecentAssets> RememberAsync(
        BackgroundRecentAssetKind kind,
        string assetId,
        string displayName,
        CancellationToken cancellationToken);

    Task<BackgroundRecentAssets> RemoveAsync(
        BackgroundRecentAssetKind kind,
        string assetId,
        CancellationToken cancellationToken);
}

internal sealed class BackgroundRecentAssetStore : IBackgroundRecentAssetStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumItemsPerKind = 5;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AccessGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string targetPath;
    private readonly SemaphoreSlim accessGate;
    private readonly ILogger<BackgroundRecentAssetStore> logger;

    internal BackgroundRecentAssetStore(
        IAppDataPaths paths,
        ILogger<BackgroundRecentAssetStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        targetPath = Path.Combine(paths.DataRoot, "Backgrounds", "recent.json");
        accessGate = AccessGates.GetOrAdd(targetPath, static _ => new SemaphoreSlim(1, 1));
        this.logger = logger;
    }

    public async Task<BackgroundRecentAssets> LoadAsync(CancellationToken cancellationToken)
    {
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            accessGate.Release();
        }
    }

    public async Task<BackgroundRecentAssets> RememberAsync(
        BackgroundRecentAssetKind kind,
        string assetId,
        string displayName,
        CancellationToken cancellationToken)
    {
        Validate(kind, assetId, displayName);
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BackgroundRecentAssets current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            BackgroundRecentAsset item = new(assetId, Path.GetFileName(displayName));
            ImmutableArray<BackgroundRecentAsset> next = AddToFront(
                kind == BackgroundRecentAssetKind.Image ? current.Images : current.Videos,
                item);
            var result = kind == BackgroundRecentAssetKind.Image
                ? new BackgroundRecentAssets(next, current.Videos)
                : new BackgroundRecentAssets(current.Images, next);
            await SaveCoreAsync(result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            accessGate.Release();
        }
    }

    public async Task<BackgroundRecentAssets> RemoveAsync(
        BackgroundRecentAssetKind kind,
        string assetId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BackgroundRecentAssets current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            ImmutableArray<BackgroundRecentAsset> next = (kind == BackgroundRecentAssetKind.Image
                    ? current.Images
                    : current.Videos)
                .Where(item => !StringComparer.Ordinal.Equals(item.AssetId, assetId))
                .ToImmutableArray();
            var result = kind == BackgroundRecentAssetKind.Image
                ? new BackgroundRecentAssets(next, current.Videos)
                : new BackgroundRecentAssets(current.Images, next);
            await SaveCoreAsync(result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            accessGate.Release();
        }
    }

    private async Task<BackgroundRecentAssets> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(targetPath))
        {
            return BackgroundRecentAssets.Empty;
        }

        try
        {
            await using FileStream stream = new(
                targetPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            PersistedRecentAssets? document = await JsonSerializer.DeserializeAsync<PersistedRecentAssets>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            if (document is null || document.SchemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException("Background recent assets schema is unsupported.");
            }

            return new BackgroundRecentAssets(
                Normalize(document.Images, BackgroundRecentAssetKind.Image),
                Normalize(document.Videos, BackgroundRecentAssetKind.Video));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            BackgroundRecentAssetStoreLog.LoadFailed(logger, exception.GetType().Name);
            return BackgroundRecentAssets.Empty;
        }
    }

    private async Task SaveCoreAsync(BackgroundRecentAssets recent, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(targetPath);
        Directory.CreateDirectory(directory!);
        string temporaryPath = Path.Combine(directory!, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new PersistedRecentAssets(CurrentSchemaVersion, recent.Images, recent.Videos),
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static ImmutableArray<BackgroundRecentAsset> AddToFront(
        ImmutableArray<BackgroundRecentAsset> source,
        BackgroundRecentAsset item) => source
        .Where(existing => !StringComparer.Ordinal.Equals(existing.AssetId, item.AssetId))
        .Prepend(item)
        .Take(MaximumItemsPerKind)
        .ToImmutableArray();

    private static ImmutableArray<BackgroundRecentAsset> Normalize(
        ImmutableArray<BackgroundRecentAsset> source,
        BackgroundRecentAssetKind kind)
    {
        if (source.IsDefaultOrEmpty)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<BackgroundRecentAsset>(MaximumItemsPerKind);
        foreach (BackgroundRecentAsset item in source)
        {
            if (item is null)
            {
                continue;
            }
            try
            {
                Validate(kind, item.AssetId, item.DisplayName);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (builder.Any(existing => StringComparer.Ordinal.Equals(existing.AssetId, item.AssetId)))
            {
                continue;
            }

            builder.Add(new(item.AssetId, Path.GetFileName(item.DisplayName)));
            if (builder.Count == MaximumItemsPerKind)
            {
                break;
            }
        }

        return builder.ToImmutable();
    }

    private static void Validate(BackgroundRecentAssetKind kind, string assetId, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (kind == BackgroundRecentAssetKind.Image)
        {
            BackgroundDefinition.ValidateImageAssetId(assetId);
        }
        else if (kind == BackgroundRecentAssetKind.Video)
        {
            BackgroundDefinition.ValidateVideoAssetId(assetId);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private sealed record PersistedRecentAssets(
        int SchemaVersion,
        ImmutableArray<BackgroundRecentAsset> Images,
        ImmutableArray<BackgroundRecentAsset> Videos);
}

internal static partial class BackgroundRecentAssetStoreLog
{
    [LoggerMessage(6790, LogLevel.Warning, "Background recent assets load failed with {ErrorType}; using an empty history")]
    internal static partial void LoadFailed(ILogger logger, string errorType);
}
