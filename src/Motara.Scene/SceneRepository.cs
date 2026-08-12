using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Persistence;

namespace Motara.Scene;

public interface ISceneRepository
{
    bool HasPersistedState { get; }

    Task<SceneWorkspace> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(SceneWorkspace workspace, CancellationToken cancellationToken);
}

public sealed class SceneRepository : ISceneRepository, IDisposable
{
    private const string IndexFileName = "index.motara.json";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string scenesDirectory;
    private readonly string indexPath;
    private readonly SemaphoreSlim accessGate = new(1, 1);
    private readonly ILogger<SceneRepository> logger;

    public SceneRepository(string scenesDirectory)
        : this(scenesDirectory, NullLogger<SceneRepository>.Instance)
    {
    }

    public SceneRepository(string scenesDirectory, ILogger<SceneRepository> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenesDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        this.scenesDirectory = Path.GetFullPath(scenesDirectory);
        indexPath = Path.Combine(this.scenesDirectory, IndexFileName);
        this.logger = logger;
    }

    public bool HasPersistedState => File.Exists(indexPath);

    public async Task<SceneWorkspace> LoadAsync(CancellationToken cancellationToken)
    {
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(indexPath))
            {
                return SceneWorkspace.CreateDefault();
            }

            try
            {
                SceneIndex index = await ReadJsonAsync<SceneIndex>(indexPath, cancellationToken)
                    .ConfigureAwait(false);
                index.Validate();
                var scenes = ImmutableArray.CreateBuilder<SceneDocument>(index.SceneIds.Length);
                foreach (SceneId sceneId in index.SceneIds)
                {
                    string sceneRoot = SceneStorageLayout.GetSceneDirectory(scenesDirectory, sceneId);
                    var storage = new ScopedMotaraStorage(sceneRoot, "scene.motara.json");
                    ScopedMotaraScanResult scan = await storage.ScanAsync(cancellationToken)
                        .ConfigureAwait(false);
                    ScopedMotaraFile[] manifests = scan.Files
                        .Where(static file => file.Kind == ScopedMotaraFileKind.Manifest)
                        .ToArray();
                    if (manifests.Length != 1)
                    {
                        throw new InvalidDataException("Scene scope must contain exactly one manifest.");
                    }

                    SceneDocument scene = await ReadJsonAsync<SceneDocument>(
                        manifests[0].Path,
                        cancellationToken).ConfigureAwait(false);
                    if (scene.Id != sceneId)
                    {
                        throw new InvalidDataException("Scene document ID does not match its index entry.");
                    }

                    scenes.Add(scene);
                }

                var workspace = new SceneWorkspace(
                    SceneWorkspace.CurrentSchemaVersion,
                    index.ActiveSceneId,
                    scenes.MoveToImmutable());
                SceneRepositoryLog.LoadCompleted(logger, workspace.Scenes.Length);
                return workspace;
            }
            catch (Exception exception) when (exception is JsonException
                or ArgumentException
                or IOException
                or UnauthorizedAccessException)
            {
                SceneRepositoryLog.LoadFailed(logger, exception.GetType().Name);
                return SceneWorkspace.CreateDefault();
            }
        }
        finally
        {
            accessGate.Release();
        }
    }

    public async Task SaveAsync(SceneWorkspace workspace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        cancellationToken.ThrowIfCancellationRequested();
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(scenesDirectory);
            foreach (SceneDocument scene in workspace.Scenes)
            {
                string sceneDirectory = SceneStorageLayout.GetSceneDirectory(scenesDirectory, scene.Id);
                var storage = new ScopedMotaraStorage(sceneDirectory, "scene.motara.json");
                Directory.CreateDirectory(storage.MotaraDirectory);
                Directory.CreateDirectory(storage.MappingsDirectory);
                Directory.CreateDirectory(storage.AssetsDirectory);
                Directory.CreateDirectory(storage.EffectsDirectory);
                await WriteJsonAtomicallyAsync(
                    storage.ManifestPath,
                    scene,
                    cancellationToken).ConfigureAwait(false);
            }

            var index = new SceneIndex(
                SceneWorkspace.CurrentSchemaVersion,
                workspace.ActiveSceneId,
                workspace.Scenes.Select(static scene => scene.Id).ToImmutableArray());
            await WriteJsonAtomicallyAsync(indexPath, index, cancellationToken).ConfigureAwait(false);
            SceneRepositoryLog.SaveCompleted(
                logger,
                workspace.Scenes.Length,
                workspace.Scenes.Count(static scene => scene.MainModel is not null),
                workspace.Scenes.Sum(static scene => scene.Attachments.Length),
                workspace.Scenes.Sum(static scene => scene.Effects.Length));
        }
        finally
        {
            accessGate.Release();
        }
    }

    public void Dispose() => accessGate.Dispose();

    private string GetScenePath(SceneId sceneId) =>
        SceneStorageLayout.GetManifestPath(scenesDirectory, sceneId);

    private static async Task<T> ReadJsonAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        T? value = await JsonSerializer.DeserializeAsync<T>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);
        return value ?? throw new InvalidDataException("Scene JSON did not contain an object.");
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string targetPath,
        T value,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("Scene target path requires a directory.");
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
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

    private sealed record SceneIndex(
        int SchemaVersion,
        SceneId ActiveSceneId,
        ImmutableArray<SceneId> SceneIds)
    {
        internal void Validate()
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(
                SchemaVersion,
                SceneWorkspace.CurrentSchemaVersion);
            if (SceneIds.IsDefaultOrEmpty
                || SceneIds.Distinct().Count() != SceneIds.Length
                || !SceneIds.Contains(ActiveSceneId))
            {
                throw new InvalidDataException("Scene index is inconsistent.");
            }
        }
    }
}
