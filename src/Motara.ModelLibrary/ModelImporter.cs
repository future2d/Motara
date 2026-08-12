using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.ModelRuntime.Abstractions;
using SharpCompress.Archives;
using System.IO.Compression;

namespace Motara.ModelLibrary;

public enum ModelImportConflictBehavior
{
    Cancel = 0,
    Replace = 1,
}

public sealed record ModelImportResult(bool Succeeded, ModelId? ModelId, ModelError? Error)
{
    public static ModelImportResult Success(ModelId modelId) => new(true, modelId, null);

    public static ModelImportResult Failure(ModelErrorCode errorCode) =>
        new(false, null, new ModelError(errorCode));
}

public interface IModelImporter
{
    Task<ModelImportResult> ImportDescriptorAsync(
        string descriptorPath,
        ModelImportConflictBehavior conflictBehavior,
        CancellationToken cancellationToken);

    Task<ModelImportResult> ImportArchiveAsync(
        string archivePath,
        ModelImportConflictBehavior conflictBehavior,
        CancellationToken cancellationToken);
}

public sealed class ModelImporter : IModelImporter
{
    private const int MaxDepth = 32;
    private const int MaxFiles = 20_000;
    private const long MaxFileBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxTotalBytes = 8L * 1024 * 1024 * 1024;
    private readonly string modelsRoot;
    private readonly string stagingRoot;
    private readonly ILogger<ModelImporter> logger;

    public ModelImporter(string modelsRoot, string stagingRoot)
        : this(modelsRoot, stagingRoot, NullLogger<ModelImporter>.Instance)
    {
    }

    public ModelImporter(
        string modelsRoot,
        string stagingRoot,
        ILogger<ModelImporter> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentNullException.ThrowIfNull(logger);
        this.modelsRoot = Path.GetFullPath(modelsRoot);
        this.stagingRoot = Path.GetFullPath(stagingRoot);
        this.logger = logger;
    }

    public Task<ModelImportResult> ImportDescriptorAsync(
        string descriptorPath,
        ModelImportConflictBehavior conflictBehavior,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorPath);
        if (!Enum.IsDefined(conflictBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(conflictBehavior));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => ImportDescriptorCore(descriptorPath, conflictBehavior, cancellationToken),
            cancellationToken);
    }

    public Task<ModelImportResult> ImportArchiveAsync(
        string archivePath,
        ModelImportConflictBehavior conflictBehavior,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        if (!Enum.IsDefined(conflictBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(conflictBehavior));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => ImportArchiveCore(archivePath, conflictBehavior, cancellationToken),
            cancellationToken);
    }

    private ModelImportResult ImportArchiveCore(
        string archivePath,
        ModelImportConflictBehavior conflictBehavior,
        CancellationToken cancellationToken)
    {
        ModelImporterLog.ImportStarted(logger, "Archive");
        string? transactionRoot = null;
        try
        {
            string normalizedArchivePath = Path.GetFullPath(archivePath);
            string extension = Path.GetExtension(normalizedArchivePath);
            if (!File.Exists(normalizedArchivePath)
                || !(extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".rar", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)))
            {
                return Fail(ModelErrorCode.UnsupportedArchive);
            }

            var archiveFile = new FileInfo(normalizedArchivePath);
            if (archiveFile.Length > MaxFileBytes)
            {
                return Fail(ModelErrorCode.SizeLimitExceeded);
            }

            Directory.CreateDirectory(modelsRoot);
            Directory.CreateDirectory(stagingRoot);
            transactionRoot = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(transactionRoot);
            ExtractArchive(normalizedArchivePath, transactionRoot, cancellationToken);

            string[] descriptors = Directory
                .EnumerateFiles(transactionRoot, "*", SearchOption.AllDirectories)
                .Where(path => ModelIdentity.IsDescriptorFilename(Path.GetFileName(path)))
                .ToArray();
            if (descriptors.Length != 1)
            {
                return Fail(ModelErrorCode.ArchiveModelCount);
            }

            string stagedDescriptorPath = descriptors[0];
            ModelIdentity identity = ModelIdentity.FromDescriptorFilename(Path.GetFileName(stagedDescriptorPath));
            _ = ModelDescriptorReader
                .ReadAsync(stagedDescriptorPath, 16 * 1024 * 1024, cancellationToken)
                .GetAwaiter()
                .GetResult();
            string modelRoot = Path.GetDirectoryName(stagedDescriptorPath)
                ?? throw new IOException("Descriptor has no parent directory.");
            if (!CommitDirectory(modelRoot, identity, conflictBehavior, cancellationToken))
            {
                return Fail(ModelErrorCode.NameConflict);
            }

            TryDeleteDirectory(transactionRoot);
            transactionRoot = null;
            ModelImporterLog.ImportCompleted(logger, "Archive", conflictBehavior.ToString());
            return ModelImportResult.Success(identity.Id);
        }
        catch (OperationCanceledException)
        {
            ModelImporterLog.ImportCancelled(logger, "Archive");
            throw;
        }
        catch (ModelDescriptorException exception)
        {
            return Fail(exception.Code);
        }
        catch (ImportValidationException exception)
        {
            return Fail(exception.Code);
        }
        catch (ImportLimitException)
        {
            return Fail(ModelErrorCode.SizeLimitExceeded);
        }
        catch (InvalidOperationException)
        {
            return Fail(ModelErrorCode.UnsupportedArchive);
        }
        catch (InvalidDataException)
        {
            return Fail(ModelErrorCode.UnsupportedArchive);
        }
        catch (IOException)
        {
            return Fail(ModelErrorCode.IoFailure);
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(ModelErrorCode.IoFailure);
        }
        finally
        {
            if (transactionRoot is not null)
            {
                TryDeleteDirectory(transactionRoot);
            }
        }

        ModelImportResult Fail(ModelErrorCode errorCode)
        {
            ModelImporterLog.ImportFailed(logger, "Archive", errorCode);
            return ModelImportResult.Failure(errorCode);
        }
    }

    private ModelImportResult ImportDescriptorCore(
        string descriptorPath,
        ModelImportConflictBehavior conflictBehavior,
        CancellationToken cancellationToken)
    {
        ModelImporterLog.ImportStarted(logger, "Descriptor");
        string? transactionRoot = null;
        try
        {
            string normalizedDescriptorPath = Path.GetFullPath(descriptorPath);
            if (!File.Exists(normalizedDescriptorPath)
                || !ModelIdentity.IsDescriptorFilename(Path.GetFileName(normalizedDescriptorPath)))
            {
                return Fail(ModelErrorCode.InvalidDescriptor);
            }

            string sourceRoot = Path.GetDirectoryName(normalizedDescriptorPath)
                ?? throw new IOException("Descriptor has no parent directory.");
            Directory.CreateDirectory(modelsRoot);
            Directory.CreateDirectory(stagingRoot);
            transactionRoot = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(transactionRoot);
            CopyTree(sourceRoot, transactionRoot, cancellationToken);

            string selectedRelativePath = Path.GetRelativePath(sourceRoot, normalizedDescriptorPath);
            string stagedDescriptorPath = Path.GetFullPath(Path.Combine(transactionRoot, selectedRelativePath));
            string[] descriptors = Directory
                .EnumerateFiles(transactionRoot, "*", SearchOption.AllDirectories)
                .Where(path => ModelIdentity.IsDescriptorFilename(Path.GetFileName(path)))
                .ToArray();
            if (descriptors.Length != 1
                || !PathEquals(descriptors[0], stagedDescriptorPath))
            {
                return Fail(ModelErrorCode.ArchiveModelCount);
            }

            ModelIdentity identity = ModelIdentity.FromDescriptorFilename(Path.GetFileName(stagedDescriptorPath));
            _ = ModelDescriptorReader
                .ReadAsync(stagedDescriptorPath, 16 * 1024 * 1024, cancellationToken)
                .GetAwaiter()
                .GetResult();

            if (!CommitDirectory(transactionRoot, identity, conflictBehavior, cancellationToken))
            {
                return Fail(ModelErrorCode.NameConflict);
            }

            transactionRoot = null;
            ModelImporterLog.ImportCompleted(logger, "Descriptor", conflictBehavior.ToString());
            return ModelImportResult.Success(identity.Id);
        }
        catch (OperationCanceledException)
        {
            ModelImporterLog.ImportCancelled(logger, "Descriptor");
            throw;
        }
        catch (ModelDescriptorException exception)
        {
            return Fail(exception.Code);
        }
        catch (ImportLimitException)
        {
            return Fail(ModelErrorCode.SizeLimitExceeded);
        }
        catch (ArgumentException)
        {
            return Fail(ModelErrorCode.InvalidDescriptor);
        }
        catch (IOException)
        {
            return Fail(ModelErrorCode.IoFailure);
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(ModelErrorCode.IoFailure);
        }
        finally
        {
            if (transactionRoot is not null)
            {
                TryDeleteDirectory(transactionRoot);
            }
        }

        ModelImportResult Fail(ModelErrorCode errorCode)
        {
            ModelImporterLog.ImportFailed(logger, "Descriptor", errorCode);
            return ModelImportResult.Failure(errorCode);
        }
    }

    private static void CopyTree(string sourceRoot, string destinationRoot, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Source, string Destination, int Depth)>();
        pending.Push((sourceRoot, destinationRoot, 0));
        int fileCount = 0;
        long totalBytes = 0;
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (directory.Depth > MaxDepth)
            {
                throw new ImportLimitException();
            }

            foreach (string entryPath in Directory.EnumerateFileSystemEntries(directory.Source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(entryPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Linked import entries are not supported.");
                }

                string targetPath = Path.Combine(directory.Destination, Path.GetFileName(entryPath));
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.CreateDirectory(targetPath);
                    pending.Push((entryPath, targetPath, directory.Depth + 1));
                    continue;
                }

                var file = new FileInfo(entryPath);
                fileCount++;
                totalBytes = checked(totalBytes + file.Length);
                if (fileCount > MaxFiles || file.Length > MaxFileBytes || totalBytes > MaxTotalBytes)
                {
                    throw new ImportLimitException();
                }

                File.Copy(entryPath, targetPath, overwrite: false);
            }
        }
    }

    private static void ExtractArchive(
        string archivePath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        if (Path.GetExtension(archivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ExtractZip(archivePath, destinationRoot, cancellationToken);
            return;
        }

        ExtractSharpArchive(archivePath, destinationRoot, cancellationToken);
    }

    private static void ExtractZip(
        string archivePath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        var state = new ArchiveExtractionState(destinationRoot);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isDirectory = entry.FullName.EndsWith('/');
            bool isLink = ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;
            string targetPath = state.Validate(
                entry.FullName,
                isDirectory,
                isLink,
                entry.Length,
                entry.CompressedLength);
            if (isDirectory)
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using Stream input = entry.Open();
            using FileStream output = new(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private static void ExtractSharpArchive(
        string archivePath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        using IArchive archive = ArchiveFactory.OpenArchive(archivePath);
        var state = new ArchiveExtractionState(destinationRoot);
        foreach (IArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string targetPath = state.Validate(
                entry.Key ?? string.Empty,
                entry.IsDirectory,
                entry.LinkTarget is not null,
                entry.Size,
                entry.CompressedSize);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using Stream input = entry.OpenEntryStream();
            using FileStream output = new(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private sealed class ArchiveExtractionState
    {
        private readonly string destinationRoot;
        private readonly string rootPrefix;
        private readonly HashSet<string> paths = new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        private int fileCount;
        private long totalBytes;

        public ArchiveExtractionState(string destinationRoot)
        {
            this.destinationRoot = Path.TrimEndingDirectorySeparator(destinationRoot);
            rootPrefix = this.destinationRoot + Path.DirectorySeparatorChar;
        }

        public string Validate(
            string key,
            bool isDirectory,
            bool isLink,
            long size,
            long compressedSize)
        {
            key = key.Replace((char)92, '/');
            if (isLink
                || string.IsNullOrWhiteSpace(key)
                || key.StartsWith('/')
                || key.Contains(':', StringComparison.Ordinal))
            {
                throw new ImportValidationException(ModelErrorCode.PathEscape);
            }

            string targetPath = Path.GetFullPath(Path.Combine(destinationRoot, key));
            if (!targetPath.StartsWith(
                    rootPrefix,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !paths.Add(targetPath))
            {
                throw new ImportValidationException(ModelErrorCode.PathEscape);
            }

            int depth = key.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
            if (depth > MaxDepth)
            {
                throw new ImportLimitException();
            }

            if (!isDirectory)
            {
                fileCount++;
                totalBytes = checked(totalBytes + size);
                if (fileCount > MaxFiles
                    || size > MaxFileBytes
                    || totalBytes > MaxTotalBytes
                    || (compressedSize > 0 && size / compressedSize > 200))
                {
                    throw new ImportLimitException();
                }
            }

            return targetPath;
        }
    }

    private bool CommitDirectory(
        string stagedModelRoot,
        ModelIdentity identity,
        ModelImportConflictBehavior conflictBehavior,
        CancellationToken cancellationToken)
    {
        string destinationRoot = Path.Combine(modelsRoot, identity.DisplayName);
        if (Directory.Exists(destinationRoot))
        {
            if (conflictBehavior == ModelImportConflictBehavior.Cancel)
            {
                return false;
            }

            ReplaceDirectory(stagedModelRoot, destinationRoot, cancellationToken);
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagedModelRoot, destinationRoot);
        }

        return true;
    }

    private static void ReplaceDirectory(
        string transactionRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        PreserveExistingMotaraDirectory(transactionRoot, destinationRoot, cancellationToken);
        string backupRoot = destinationRoot + ".backup-" + Guid.NewGuid().ToString("N");
        Directory.Move(destinationRoot, backupRoot);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(transactionRoot, destinationRoot);
        }
        catch
        {
            if (!Directory.Exists(destinationRoot) && Directory.Exists(backupRoot))
            {
                Directory.Move(backupRoot, destinationRoot);
            }

            throw;
        }

        TryDeleteDirectory(backupRoot);
    }

    private static void PreserveExistingMotaraDirectory(
        string transactionRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        string existingMotara = Path.Combine(destinationRoot, "motara");
        if (!Directory.Exists(existingMotara))
        {
            return;
        }

        string stagedMotara = Path.Combine(transactionRoot, "motara");
        TryDeleteDirectory(stagedMotara);
        CopyDirectory(existingMotara, stagedMotara, cancellationToken);
    }

    private static void CopyDirectory(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (string directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class ImportLimitException : Exception;

    private sealed class ImportValidationException(ModelErrorCode code) : Exception
    {
        public ModelErrorCode Code { get; } = code;
    }
}

internal static partial class ModelImporterLog
{
    [LoggerMessage(5010, LogLevel.Information, "Model import started for {InputKind}")]
    internal static partial void ImportStarted(ILogger logger, string inputKind);

    [LoggerMessage(5011, LogLevel.Information,
        "Model import completed for {InputKind} with conflict behavior {ConflictBehavior}")]
    internal static partial void ImportCompleted(ILogger logger, string inputKind, string conflictBehavior);

    [LoggerMessage(5012, LogLevel.Warning, "Model import failed for {InputKind} with {ErrorCode}")]
    internal static partial void ImportFailed(ILogger logger, string inputKind, ModelErrorCode errorCode);

    [LoggerMessage(5013, LogLevel.Information, "Model import cancelled for {InputKind}")]
    internal static partial void ImportCancelled(ILogger logger, string inputKind);
}
