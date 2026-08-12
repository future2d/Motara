namespace Motara.App.Backgrounds;

internal sealed record BackgroundAssetImportResult(
    string AssetId,
    string ManagedPath,
    string Extension,
    int PixelWidth,
    int PixelHeight);

internal interface IBackgroundAssetStore
{
    Task<BackgroundAssetImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken);

    string GetManagedPath(string assetId);

    Task<BackgroundAssetImportResult> ImportVideoAsync(
        string sourcePath,
        CancellationToken cancellationToken) =>
        Task.FromException<BackgroundAssetImportResult>(new NotSupportedException("Video import is not supported by this asset store."));

    string GetManagedVideoPath(string assetId) => throw new NotSupportedException("Video assets are not supported by this asset store.");
}
