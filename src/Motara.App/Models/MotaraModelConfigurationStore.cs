using System.Text.Json;
using Motara.Persistence;

namespace Motara.App.Models;

internal sealed class MotaraModelConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    internal MotaraModelConfigurationStore(string modelDirectory, string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        if (!StringComparer.Ordinal.Equals(modelName, System.IO.Path.GetFileName(modelName)))
        {
            throw new ArgumentException("Model name cannot contain a path.", nameof(modelName));
        }

        var storage = new ScopedMotaraStorage(modelDirectory, "model.motara.json", modelName);
        Path = storage.ManifestPath;
        MappingsDirectory = storage.MappingsDirectory;
    }

    internal string Path { get; }

    internal string MappingsDirectory { get; }

    internal async Task<MotaraModelConfiguration?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        await using var stream = new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        MotaraModelConfiguration configuration =
            await JsonSerializer.DeserializeAsync<MotaraModelConfiguration>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("Model configuration is empty.");
        configuration.Validate();
        return configuration;
    }

    internal async Task SaveAsync(
        MotaraModelConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        string directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("Model configuration requires a directory.");
        Directory.CreateDirectory(directory);
        string temporary = System.IO.Path.Combine(directory, $".model-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, configuration, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, Path, overwrite: true);
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
