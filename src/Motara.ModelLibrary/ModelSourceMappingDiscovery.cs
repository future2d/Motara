using System.Collections.Immutable;
using System.Text.Json;
using Motara.Persistence;

namespace Motara.ModelLibrary;

public sealed record ModelSourceMappingCandidate(
    string VendorId,
    string TechnologyId,
    string AdapterId,
    string ProfileId,
    string Channel,
    string FileName,
    string FullPath);

public static class ModelSourceMappingDiscovery
{
    private const long MaximumDocumentBytes = 16 * 1024 * 1024;

    public static Task<ImmutableArray<ModelSourceMappingCandidate>> DiscoverAsync(
        string modelDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        string root = Path.GetFullPath(modelDirectory);
        return Task.Run(() => DiscoverCoreAsync(root, cancellationToken), cancellationToken);
    }

    private static async Task<ImmutableArray<ModelSourceMappingCandidate>> DiscoverCoreAsync(
        string root,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var candidates = ImmutableArray.CreateBuilder<ModelSourceMappingCandidate>();
        var storage = new ScopedMotaraStorage(root, "model.motara.json");
        ScopedMotaraScanResult scan = await storage.ScanAsync(cancellationToken).ConfigureAwait(false);
        foreach (string path in scan.Files
            .Where(static file => file.Kind == ScopedMotaraFileKind.Mapping)
            .Select(static file => file.Path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.Length > MaximumDocumentBytes)
            {
                continue;
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using JsonDocument json = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement rootElement = json.RootElement;
            if (!TryIdentity(rootElement, "vendorId", out string vendorId)
                || !TryIdentity(rootElement, "technologyId", out string technologyId)
                || !TryIdentity(rootElement, "adapterId", out string adapterId)
                || !TryIdentity(rootElement, "profileId", out string profileId)
                || !TryIdentity(rootElement, "channel", out string channel))
            {
                continue;
            }

            candidates.Add(new ModelSourceMappingCandidate(
                vendorId,
                technologyId,
                adapterId,
                profileId,
                channel,
                Path.GetFileName(path),
                Path.GetFullPath(path)));
        }

        return candidates
            .OrderBy(static candidate => candidate.AdapterId, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.ProfileId, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static bool TryIdentity(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.String
            || property.GetString() is not string text
            || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }
}
