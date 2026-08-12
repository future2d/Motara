using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Motara.Collaboration.Profile;

public sealed class LocalCollaborationProfileStore : IDisposable
{
    private const string FileName = "profile.motara.json";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim accessGate = new(1, 1);
    private readonly ILogger<LocalCollaborationProfileStore> logger;

    public LocalCollaborationProfileStore(
        string collaborationRoot,
        ILogger<LocalCollaborationProfileStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collaborationRoot);
        DocumentPath = Path.Combine(Path.GetFullPath(collaborationRoot), FileName);
        this.logger = logger ?? NullLogger<LocalCollaborationProfileStore>.Instance;
    }

    internal string DocumentPath { get; }

    public async Task<LocalCollaborationProfile?> LoadAsync(CancellationToken cancellationToken)
    {
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(DocumentPath))
            {
                LocalCollaborationProfileEvents.Loaded(logger, false);
                return null;
            }

            await using FileStream stream = new(
                DocumentPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            LocalCollaborationProfile profile = await JsonSerializer.DeserializeAsync<LocalCollaborationProfile>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The local collaboration profile is empty.");
            string normalized = LocalCollaborationProfile.NormalizeDisplayName(profile.DisplayName);
            if (profile.SchemaVersion != LocalCollaborationProfile.CurrentSchemaVersion
                || !StringComparer.Ordinal.Equals(profile.DisplayName, normalized))
            {
                throw new InvalidDataException("The local collaboration profile is invalid.");
            }

            LocalCollaborationProfileEvents.Loaded(logger, true);
            return profile;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException)
        {
            LocalCollaborationProfileEvents.Failed(logger, "load", exception.GetType().Name);
            throw;
        }
        finally
        {
            accessGate.Release();
        }
    }

    public async Task<LocalCollaborationProfile> SaveAsync(
        string displayName,
        CancellationToken cancellationToken)
    {
        string normalized;
        try
        {
            normalized = LocalCollaborationProfile.NormalizeDisplayName(displayName);
        }
        catch (ArgumentException exception)
        {
            LocalCollaborationProfileEvents.Failed(logger, "save", exception.GetType().Name);
            throw;
        }

        var profile = new LocalCollaborationProfile(
            LocalCollaborationProfile.CurrentSchemaVersion,
            normalized);
        await accessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DocumentPath)!);
            string temporaryPath = Path.Combine(
                Path.GetDirectoryName(DocumentPath)!,
                $".{Path.GetFileName(DocumentPath)}.{Guid.NewGuid():N}.tmp");
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
                        profile,
                        SerializerOptions,
                        cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, DocumentPath, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }

            LocalCollaborationProfileEvents.Saved(logger, normalized.Length);
            return profile;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            LocalCollaborationProfileEvents.Failed(logger, "save", exception.GetType().Name);
            throw;
        }
        finally
        {
            accessGate.Release();
        }
    }

    public void Dispose() => accessGate.Dispose();
}
