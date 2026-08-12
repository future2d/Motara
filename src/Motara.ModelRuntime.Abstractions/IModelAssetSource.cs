using System.Text;

namespace Motara.ModelRuntime.Abstractions;

public interface IModelAssetSource : IAsyncDisposable
{
    ValueTask<long> GetLengthAsync(string assetId, CancellationToken cancellationToken);

    ValueTask<Stream> OpenReadAsync(string assetId, CancellationToken cancellationToken);
}

public static class ModelAssetId
{
    public static string Normalize(string assetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        string normalized = assetId.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        if (normalized.StartsWith('/')
            || normalized.Contains(':'))
        {
            throw new ArgumentException("A model asset ID must be relative.", nameof(assetId));
        }

        string[] segments = normalized.Split('/');
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ArgumentException("A model asset ID contains an invalid segment.", nameof(assetId));
        }

        return normalized;
    }
}
