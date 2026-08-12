using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Core.Formulas;

namespace Motara.App.Tracking;

internal sealed class SourceMappingProfileStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SourceMappingPaths paths;
    private readonly SemaphoreSlim gate;
    private readonly ILogger<SourceMappingProfileStore> logger;

    internal SourceMappingProfileStore(
        SourceMappingPaths paths,
        ILogger<SourceMappingProfileStore>? logger = null)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        gate = Gates.GetOrAdd(paths.DirectoryPath, static _ => new SemaphoreSlim(1, 1));
        this.logger = logger ?? NullLogger<SourceMappingProfileStore>.Instance;
    }

    internal ILogger Logger => logger;

    internal string DirectoryPath => paths.DirectoryPath;

    internal async Task InitializeAsync(
        SourceMappingProfileDocument builtIn,
        CancellationToken cancellationToken)
    {
        ValidateForAdapter(builtIn);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(paths.DirectoryPath);
            SourceMappingProfileDocument? defaultDocument = await LoadValidForAdapterUnlockedAsync(
                paths.DefaultPath,
                cancellationToken).ConfigureAwait(false);
            if (defaultDocument is null)
            {
                defaultDocument = builtIn;
                await WriteAtomicUnlockedAsync(paths.DefaultPath, builtIn, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (!InputIdsMatch(defaultDocument, builtIn.InputIds))
            {
                SourceMappingProfileLog.InputSchemaReset(
                    logger,
                    paths.AdapterId,
                    defaultDocument.InputIds.Length,
                    builtIn.InputIds.Length);
                defaultDocument = builtIn;
                await WriteAtomicUnlockedAsync(paths.DefaultPath, builtIn, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (!StringComparer.Ordinal.Equals(defaultDocument.ProfileId, builtIn.ProfileId))
            {
                SourceMappingProfileLog.BuiltInProfileReset(
                    logger,
                    paths.AdapterId,
                    defaultDocument.ProfileId,
                    builtIn.ProfileId);
                defaultDocument = builtIn;
                await WriteAtomicUnlockedAsync(paths.DefaultPath, builtIn, cancellationToken)
                    .ConfigureAwait(false);
            }

            SourceMappingSelectionDocument? selection = await LoadSelectionUnlockedAsync(
                cancellationToken).ConfigureAwait(false);
            SourceMappingProfileDocument? selected = selection is null
                ? null
                : await LoadSelectedDocumentUnlockedAsync(selection, cancellationToken)
                    .ConfigureAwait(false);
            if (selection is null
                || selected is null
                || !InputIdsMatch(selected, builtIn.InputIds))
            {
                if (selected is not null && !InputIdsMatch(selected, builtIn.InputIds))
                {
                    SourceMappingProfileLog.InputSchemaReset(
                        logger,
                        paths.AdapterId,
                        selected.InputIds.Length,
                        builtIn.InputIds.Length);
                }
                await WriteSelectionUnlockedAsync(
                    SourceMappingSelectionDocument.Create(
                        paths.AdapterId,
                        defaultDocument.ProfileId,
                        Path.GetFileName(paths.DefaultPath)),
                    cancellationToken)
                    .ConfigureAwait(false);
            }

            SourceMappingProfileLog.Initialized(logger, paths.AdapterId);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<SourceMappingProfileDocument> LoadSelectedAsync(
        SourceMappingProfileDocument fallback,
        CancellationToken cancellationToken)
    {
        ValidateForAdapter(fallback);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SourceMappingSelectionDocument? selection = await LoadSelectionUnlockedAsync(
                cancellationToken).ConfigureAwait(false);
            SourceMappingProfileDocument? loaded = selection is null
                ? null
                : await LoadSelectedDocumentUnlockedAsync(selection, cancellationToken)
                    .ConfigureAwait(false);
            if (loaded is not null)
            {
                SourceMappingProfileLog.Loaded(
                    logger,
                    loaded.ProfileId,
                    loaded.AdapterId,
                    loaded.Outputs.Length);
                return loaded;
            }

            SourceMappingProfileLog.Fallback(
                logger,
                fallback.ProfileId,
                fallback.AdapterId,
                fallback.Outputs.Length,
                "MissingOrInvalidSelection",
                SourceMappingProfileDocument.CurrentSchemaVersion);
            return fallback;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<SourceMappingProfileDocument> LoadDefaultAsync(
        SourceMappingProfileDocument fallback,
        CancellationToken cancellationToken)
    {
        ValidateForAdapter(fallback);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SourceMappingProfileDocument? loaded = await LoadValidForAdapterUnlockedAsync(
                paths.DefaultPath,
                cancellationToken).ConfigureAwait(false);
            SourceMappingProfileDocument result = loaded ?? fallback;
            SourceMappingProfileLog.DefaultLoaded(
                logger,
                result.AdapterId,
                result.Outputs.Length,
                loaded is not null);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<SourceMappingProfileDocument> ImportAsDraftAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string fullSourcePath = Path.GetFullPath(sourcePath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SourceMappingProfileDocument document = await ReadRequiredUnlockedAsync(
                fullSourcePath,
                cancellationToken).ConfigureAwait(false);
            ValidateForAdapter(document);
            string destination = paths.CreateNamedPath(Path.GetFileNameWithoutExtension(fullSourcePath));
            await WriteAtomicUnlockedAsync(destination, document, cancellationToken).ConfigureAwait(false);
            SourceMappingProfileLog.Imported(logger, paths.AdapterId, document.Outputs.Length);
            return document;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or IOException)
        {
            SourceMappingProfileLog.Failed(logger, "Import", exception.GetType().Name);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<string> SaveAsAsync(
        SourceMappingProfileDocument document,
        string name,
        CancellationToken cancellationToken)
    {
        ValidateForAdapter(document);
        string destination = paths.CreateNamedPath(name);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAtomicUnlockedAsync(destination, document, cancellationToken).ConfigureAwait(false);
            SourceMappingProfileLog.SavedAs(logger, paths.AdapterId, document.Outputs.Length);
            return destination;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task SaveSelectedAsync(
        SourceMappingProfileDocument document,
        CancellationToken cancellationToken)
    {
        ValidateForAdapter(document);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string targetPath = await ResolveSaveTargetUnlockedAsync(document, cancellationToken)
                .ConfigureAwait(false);
            if (PathEquals(targetPath, paths.DefaultPath))
            {
                SourceMappingProfileDocument? defaultDocument =
                    await LoadValidForAdapterUnlockedAsync(paths.DefaultPath, cancellationToken)
                        .ConfigureAwait(false);
                if (defaultDocument is null || !DocumentsEquivalent(defaultDocument, document))
                {
                    targetPath = Path.Combine(
                        paths.DirectoryPath,
                        $"global.{paths.AdapterId}.mapping.motara.json");
                }
            }

            if (!PathEquals(targetPath, paths.DefaultPath))
            {
                await WriteAtomicUnlockedAsync(targetPath, document, cancellationToken)
                    .ConfigureAwait(false);
            }

            await WriteSelectionUnlockedAsync(
                SourceMappingSelectionDocument.Create(
                    paths.AdapterId,
                    document.ProfileId,
                    Path.GetFileName(targetPath)),
                cancellationToken).ConfigureAwait(false);
            SourceMappingProfileLog.Saved(
                logger,
                document.ProfileId,
                document.AdapterId,
                document.Outputs.Length);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> ResolveSaveTargetUnlockedAsync(
        SourceMappingProfileDocument document,
        CancellationToken cancellationToken)
    {
        SourceMappingSelectionDocument? selection = await LoadSelectionUnlockedAsync(cancellationToken)
            .ConfigureAwait(false);
        if (selection is not null
            && StringComparer.Ordinal.Equals(selection.ProfileId, document.ProfileId))
        {
            return Path.Combine(paths.DirectoryPath, selection.FileName);
        }

        string suffix = SourceMappingPaths.GetFileSuffix(paths.AdapterId);
        foreach (string candidate in Directory.Exists(paths.DirectoryPath)
            ? Directory.EnumerateFiles(paths.DirectoryPath, "*" + suffix, SearchOption.TopDirectoryOnly)
            : [])
        {
            SourceMappingProfileDocument? existing = await LoadValidForAdapterUnlockedAsync(
                candidate,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null
                && StringComparer.Ordinal.Equals(existing.ProfileId, document.ProfileId))
            {
                return candidate;
            }
        }

        return paths.CreateNamedPath(document.ProfileId);
    }

    private async Task<SourceMappingSelectionDocument?> LoadSelectionUnlockedAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.SelectionPath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                paths.SelectionPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            SourceMappingSelectionDocument selection =
                await JsonSerializer.DeserializeAsync<SourceMappingSelectionDocument>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("Mapping selection is empty.");
            selection.Validate(paths.AdapterId);
            return selection;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return null;
        }
    }

    private async Task<SourceMappingProfileDocument?> LoadSelectedDocumentUnlockedAsync(
        SourceMappingSelectionDocument selection,
        CancellationToken cancellationToken)
    {
        SourceMappingProfileDocument? document = await LoadValidForAdapterUnlockedAsync(
            Path.Combine(paths.DirectoryPath, selection.FileName),
            cancellationToken).ConfigureAwait(false);
        return document is not null
            && StringComparer.Ordinal.Equals(document.ProfileId, selection.ProfileId)
                ? document
                : null;
    }

    private static bool InputIdsMatch(
        SourceMappingProfileDocument document,
        IEnumerable<string> expectedInputIds) =>
        document.InputIds.SequenceEqual(expectedInputIds, StringComparer.Ordinal);

    private async Task WriteSelectionUnlockedAsync(
        SourceMappingSelectionDocument selection,
        CancellationToken cancellationToken)
    {
        selection.Validate(paths.AdapterId);
        string temporary = Path.Combine(
            paths.DirectoryPath,
            $".mapping-selection-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(paths.DirectoryPath);
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
                await JsonSerializer.SerializeAsync(stream, selection, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, paths.SelectionPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool DocumentsEquivalent(
        SourceMappingProfileDocument left,
        SourceMappingProfileDocument right) =>
        left.SchemaVersion == right.SchemaVersion
        && StringComparer.Ordinal.Equals(left.ProfileId, right.ProfileId)
        && StringComparer.Ordinal.Equals(left.VendorId, right.VendorId)
        && StringComparer.Ordinal.Equals(left.TechnologyId, right.TechnologyId)
        && StringComparer.Ordinal.Equals(left.AdapterId, right.AdapterId)
        && StringComparer.Ordinal.Equals(left.Channel, right.Channel)
        && left.InputIds.SequenceEqual(right.InputIds)
        && left.Outputs.SequenceEqual(right.Outputs);

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void ValidateForAdapter(SourceMappingProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        if (!StringComparer.Ordinal.Equals(document.AdapterId, paths.AdapterId))
        {
            throw new ArgumentException("Mapping adapter does not match the repository.", nameof(document));
        }
    }

    private static async Task<SourceMappingProfileDocument?> LoadValidUnlockedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return await ReadRequiredUnlockedAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<SourceMappingProfileDocument?> LoadValidForAdapterUnlockedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        SourceMappingProfileDocument? document = await LoadValidUnlockedAsync(
            path,
            cancellationToken).ConfigureAwait(false);
        return document is not null
            && StringComparer.Ordinal.Equals(document.AdapterId, paths.AdapterId)
                ? document
                : null;
    }

    private static async Task<SourceMappingProfileDocument> ReadRequiredUnlockedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        SourceMappingProfileDocument document =
            await JsonSerializer.DeserializeAsync<SourceMappingProfileDocument>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("Mapping document is empty.");
        document.Validate();
        return document;
    }

    private static async Task WriteAtomicUnlockedAsync(
        string path,
        SourceMappingProfileDocument document,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Mapping path requires a directory.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".mapping-{Guid.NewGuid():N}.tmp");
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
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
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

internal sealed record SourceMappingSelectionDocument(
    int SchemaVersion,
    string AdapterId,
    string ProfileId,
    string FileName)
{
    internal const int CurrentSchemaVersion = 1;

    internal static SourceMappingSelectionDocument Create(
        string adapterId,
        string profileId,
        string fileName) => new(CurrentSchemaVersion, adapterId, profileId, fileName);

    internal void Validate(string expectedAdapterId)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(SchemaVersion, CurrentSchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(AdapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(FileName);
        if (!StringComparer.Ordinal.Equals(AdapterId, expectedAdapterId)
            || !StringComparer.Ordinal.Equals(FileName, Path.GetFileName(FileName))
            || !FileName.EndsWith(
                SourceMappingPaths.GetFileSuffix(AdapterId),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Mapping selection identity or file name is invalid.");
        }
    }
}

internal static partial class SourceMappingProfileLog
{
    [LoggerMessage(6610, LogLevel.Information,
        "Source mapping profile loaded: profile={ProfileId}; adapter={AdapterId}; outputs={OutputCount}")]
    internal static partial void Loaded(
        ILogger logger,
        string profileId,
        string adapterId,
        int outputCount);

    [LoggerMessage(6611, LogLevel.Information,
        "Source mapping profile saved: profile={ProfileId}; adapter={AdapterId}; outputs={OutputCount}")]
    internal static partial void Saved(
        ILogger logger,
        string profileId,
        string adapterId,
        int outputCount);

    [LoggerMessage(6612, LogLevel.Warning, "Source mapping profile {Operation} failed with {ErrorType}")]
    internal static partial void Failed(ILogger logger, string operation, string errorType);

    [LoggerMessage(6613, LogLevel.Warning,
        "Source mapping profile fallback: profile={ProfileId}; adapter={AdapterId}; outputs={OutputCount}; error={ErrorCode}; schema={SchemaVersion}")]
    internal static partial void Fallback(
        ILogger logger,
        string profileId,
        string adapterId,
        int outputCount,
        string errorCode,
        int schemaVersion);

    [LoggerMessage(6614, LogLevel.Information, "Source mapping repository initialized: adapter={AdapterId}")]
    internal static partial void Initialized(ILogger logger, string adapterId);

    [LoggerMessage(6615, LogLevel.Information,
        "Source mapping profile imported: adapter={AdapterId}; outputs={OutputCount}")]
    internal static partial void Imported(ILogger logger, string adapterId, int outputCount);

    [LoggerMessage(6616, LogLevel.Information,
        "Source mapping profile saved as named profile: adapter={AdapterId}; outputs={OutputCount}")]
    internal static partial void SavedAs(ILogger logger, string adapterId, int outputCount);

    [LoggerMessage(6617, LogLevel.Information,
        "Source mapping default loaded: adapter={AdapterId}; outputs={OutputCount}; fromDisk={FromDisk}")]
    internal static partial void DefaultLoaded(
        ILogger logger,
        string adapterId,
        int outputCount,
        bool fromDisk);

    [LoggerMessage(6618, LogLevel.Warning,
        "Source mapping input schema changed; adapter={AdapterId}; oldInputs={OldInputCount}; newInputs={NewInputCount}; local profiles reset")]
    internal static partial void InputSchemaReset(
        ILogger logger,
        string adapterId,
        int oldInputCount,
        int newInputCount);

    [LoggerMessage(6619, LogLevel.Information,
        "Source mapping built-in profile changed; adapter={AdapterId}; oldProfile={OldProfileId}; newProfile={NewProfileId}; default reset")]
    internal static partial void BuiltInProfileReset(
        ILogger logger,
        string adapterId,
        string oldProfileId,
        string newProfileId);
}
