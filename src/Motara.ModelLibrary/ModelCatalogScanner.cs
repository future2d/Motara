using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.ModelRuntime.Abstractions;

namespace Motara.ModelLibrary;

public sealed class ModelCatalogScanner : IModelCatalog
{
    private readonly string modelsRoot;
    private readonly ModelCatalogLimits limits;
    private readonly ILogger<ModelCatalogScanner> logger;
    private readonly object refreshGate = new();
    private Task<ModelCatalogSnapshot>? activeRefresh;
    private ModelCatalogSnapshot current = ModelCatalogSnapshot.Empty;

    public ModelCatalogScanner(string modelsRoot, ModelCatalogLimits? limits = null)
        : this(modelsRoot, NullLogger<ModelCatalogScanner>.Instance, limits)
    {
    }

    public ModelCatalogScanner(
        string modelsRoot,
        ILogger<ModelCatalogScanner> logger,
        ModelCatalogLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        ArgumentNullException.ThrowIfNull(logger);
        this.modelsRoot = Path.GetFullPath(modelsRoot);
        this.logger = logger;
        this.limits = limits ?? ModelCatalogLimits.Default;
    }

    public ModelCatalogSnapshot Current => Volatile.Read(ref current);

    public Task<ModelCatalogSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<ModelCatalogSnapshot> refresh;
        lock (refreshGate)
        {
            refresh = activeRefresh ??= RefreshAndClearAsync(cancellationToken);
        }

        return refresh.WaitAsync(cancellationToken);
    }

    private async Task<ModelCatalogSnapshot> RefreshAndClearAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (refreshGate)
            {
                activeRefresh = null;
            }
        }
    }

    private async Task<ModelCatalogSnapshot> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        ModelCatalogLog.RefreshStarted(logger);
        try
        {
            ImmutableArray<string> candidates = await Task.Run(
                () => DiscoverCandidates(cancellationToken),
                cancellationToken).ConfigureAwait(false);
            ImmutableArray<ModelCatalogEntry>.Builder entries = ImmutableArray.CreateBuilder<ModelCatalogEntry>();
            HashSet<string> duplicateDirectories = candidates
                .GroupBy(
                    static path => Path.GetDirectoryName(path) ?? string.Empty,
                    PathComparer)
                .Where(static group => group.Count() > 1)
                .Select(static group => group.Key)
                .ToHashSet(PathComparer);

            foreach (string descriptorPath in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(await CreateEntryAsync(
                    descriptorPath,
                    duplicateDirectories,
                    cancellationToken).ConfigureAwait(false));
            }

            MarkDuplicateNames(entries);
            ImmutableArray<ModelCatalogEntry> ordered = entries
                .OrderBy(static entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static entry => entry.DescriptorPath, PathComparer)
                .ToImmutableArray();
            var snapshot = new ModelCatalogSnapshot(
                Current.Revision + 1,
                ModelCatalogStatus.Ready,
                ordered,
                null);
            Volatile.Write(ref current, snapshot);
            ModelCatalogLog.RefreshCompleted(
                logger,
                ordered.Length,
                ordered.Count(static entry => entry.IsSelectable));
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CatalogLimitException)
        {
            ModelCatalogLog.RefreshFailed(logger, ModelErrorCode.SizeLimitExceeded);
            return new ModelCatalogSnapshot(
                Current.Revision,
                ModelCatalogStatus.Faulted,
                Current.Entries,
                new ModelError(ModelErrorCode.SizeLimitExceeded));
        }
        catch (IOException)
        {
            ModelCatalogLog.RefreshFailed(logger, ModelErrorCode.IoFailure);
            return CreateIoFailure();
        }
        catch (UnauthorizedAccessException)
        {
            ModelCatalogLog.RefreshFailed(logger, ModelErrorCode.IoFailure);
            return CreateIoFailure();
        }
    }

    private ImmutableArray<string> DiscoverCandidates(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(modelsRoot))
        {
            Directory.CreateDirectory(modelsRoot);
        }

        var candidates = ImmutableArray.CreateBuilder<string>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((modelsRoot, 0));
        int fileCount = 0;
        while (pending.TryPop(out (string Path, int Depth) directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (directory.Depth > limits.MaxDepth)
            {
                throw new CatalogLimitException();
            }

            foreach (string entryPath in Directory.EnumerateFileSystemEntries(directory.Path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(entryPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push((entryPath, directory.Depth + 1));
                    continue;
                }

                fileCount++;
                if (fileCount > limits.MaxFiles)
                {
                    throw new CatalogLimitException();
                }

                if (ModelIdentity.IsDescriptorFilename(Path.GetFileName(entryPath)))
                {
                    candidates.Add(Path.GetFullPath(entryPath));
                }
            }
        }

        return candidates.ToImmutable();
    }

    private async Task<ModelCatalogEntry> CreateEntryAsync(
        string descriptorPath,
        HashSet<string> duplicateDirectories,
        CancellationToken cancellationToken)
    {
        ModelIdentity identity;
        try
        {
            identity = ModelIdentity.FromDescriptorFilename(Path.GetFileName(descriptorPath));
        }
        catch (ArgumentException)
        {
            return InvalidEntry(descriptorPath, new ModelError(ModelErrorCode.InvalidDescriptor));
        }

        string directory = Path.GetDirectoryName(descriptorPath) ?? string.Empty;
        if (duplicateDirectories.Contains(directory))
        {
            return new ModelCatalogEntry(
                identity.Id,
                identity.DisplayName,
                descriptorPath,
                null,
                new ModelError(ModelErrorCode.InvalidDescriptor));
        }

        try
        {
            ModelDescriptor descriptor = await ModelDescriptorReader.ReadAsync(
                descriptorPath,
                limits.MaxDescriptorBytes,
                cancellationToken).ConfigureAwait(false);
            if (descriptor.ParameterNames.Count > 0)
            {
                ModelCatalogLog.DisplayMetadataLoaded(logger, descriptor.ParameterNames.Count);
            }
            if (descriptor.MissingOptionalAssetCount > 0)
            {
                ModelCatalogLog.OptionalAssetsOmitted(logger, descriptor.MissingOptionalAssetCount);
            }
            return new ModelCatalogEntry(
                identity.Id,
                descriptor.Nickname ?? identity.DisplayName,
                descriptorPath,
                descriptor,
                null);
        }
        catch (ModelDescriptorException exception)
        {
            return new ModelCatalogEntry(
                identity.Id,
                identity.DisplayName,
                descriptorPath,
                null,
                new ModelError(exception.Code));
        }
    }

    private static void MarkDuplicateNames(ImmutableArray<ModelCatalogEntry>.Builder entries)
    {
        foreach (IGrouping<ModelId, ModelCatalogEntry> duplicateGroup in entries
            .GroupBy(static entry => entry.Id)
            .Where(static group => group.Count() > 1))
        {
            foreach (ModelCatalogEntry duplicate in duplicateGroup.ToArray())
            {
                int index = entries.IndexOf(duplicate);
                entries[index] = duplicate with
                {
                    Descriptor = null,
                    Error = new ModelError(ModelErrorCode.NameConflict),
                };
            }
        }
    }

    private static ModelCatalogEntry InvalidEntry(string descriptorPath, ModelError error) => new(
        default,
        Path.GetFileNameWithoutExtension(descriptorPath),
        descriptorPath,
        null,
        error);

    private ModelCatalogSnapshot CreateIoFailure() => new(
        Current.Revision,
        ModelCatalogStatus.Faulted,
        Current.Entries,
        new ModelError(ModelErrorCode.IoFailure));

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed class CatalogLimitException : Exception;
}

internal static partial class ModelCatalogLog
{
    [LoggerMessage(5000, LogLevel.Debug, "Model catalog refresh started")]
    internal static partial void RefreshStarted(ILogger logger);

    [LoggerMessage(5001, LogLevel.Information,
        "Model catalog refresh completed with {EntryCount} entries and {SelectableCount} selectable entries")]
    internal static partial void RefreshCompleted(
        ILogger logger,
        int entryCount,
        int selectableCount);

    [LoggerMessage(5002, LogLevel.Warning, "Model catalog refresh failed with {ErrorCode}")]
    internal static partial void RefreshFailed(ILogger logger, ModelErrorCode errorCode);

    [LoggerMessage(5003, LogLevel.Debug,
        "Model display metadata loaded with {ParameterNameCount} parameter names")]
    internal static partial void DisplayMetadataLoaded(ILogger logger, int parameterNameCount);

    [LoggerMessage(5004, LogLevel.Information,
        "Model catalog omitted {OptionalAssetCount} unavailable optional assets")]
    internal static partial void OptionalAssetsOmitted(ILogger logger, int optionalAssetCount);
}
