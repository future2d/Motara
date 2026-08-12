using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Motara.Persistence;

public enum ScopedMotaraFileKind
{
    Manifest,
    Mapping,
}

public enum ScopedMotaraManifestStatus
{
    Missing = 0,
    Canonical = 1,
    NonCanonical = 2,
    Conflict = 3,
}

public sealed record ScopedMotaraFile(
    string Path,
    ScopedMotaraFileKind Kind,
    bool IsCanonical,
    string? MappingIdentity);

public sealed record ScopedMotaraScanResult(ImmutableArray<ScopedMotaraFile> Files)
{
    public ImmutableArray<ScopedMotaraFile> ManifestCandidates => Files
        .Where(static file => file.Kind == ScopedMotaraFileKind.Manifest)
        .ToImmutableArray();

    public ScopedMotaraManifestStatus ManifestStatus => ManifestCandidates.Length switch
    {
        0 => ScopedMotaraManifestStatus.Missing,
        1 when ManifestCandidates[0].IsCanonical => ScopedMotaraManifestStatus.Canonical,
        1 => ScopedMotaraManifestStatus.NonCanonical,
        _ => ScopedMotaraManifestStatus.Conflict,
    };
}

public sealed record ScopedMotaraOrganizationConflict(string Identity, int FileCount);

public sealed record ScopedMotaraOrganizationResult(
    bool Succeeded,
    int MovedFileCount,
    int MergedFileCount,
    ImmutableArray<ScopedMotaraOrganizationConflict> Conflicts);

public sealed class ScopedMotaraStorage
{
    private const int MaximumDepth = 32;
    private const int MaximumFiles = 20_000;
    private const long MaximumConfigurationBytes = 16 * 1024 * 1024;
    private readonly string scopeRoot;
    private readonly string manifestFileName;
    private readonly string? modelName;
    private readonly ILogger<ScopedMotaraStorage> logger;

    public ScopedMotaraStorage(
        string scopeRoot,
        string manifestFileName,
        ILogger<ScopedMotaraStorage>? logger = null,
        string? modelName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestFileName);
        if (!StringComparer.Ordinal.Equals(manifestFileName, Path.GetFileName(manifestFileName))
            || !manifestFileName.EndsWith(".motara.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Manifest must be a Motara JSON file name.", nameof(manifestFileName));
        }

        this.scopeRoot = Path.GetFullPath(scopeRoot);
        this.manifestFileName = manifestFileName;
        this.modelName = string.IsNullOrWhiteSpace(modelName) ? null : modelName;
        this.logger = logger ?? NullLogger<ScopedMotaraStorage>.Instance;
    }

    public ScopedMotaraStorage(
        string scopeRoot,
        string manifestFileName,
        string modelName)
        : this(scopeRoot, manifestFileName, null, modelName)
    {
    }

    public string MotaraDirectory => Path.Combine(scopeRoot, "motara");

    public string ManifestPath => Path.Combine(MotaraDirectory, manifestFileName);

    public string MappingsDirectory => Path.Combine(MotaraDirectory, "mappings");

    public string AssetsDirectory => Path.Combine(MotaraDirectory, "assets");

    public string EffectsDirectory => Path.Combine(MotaraDirectory, "effects");

    public Task<ScopedMotaraScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => ScanCoreAsync(cancellationToken), cancellationToken);
    }

    public async Task<string?> ResolveMappingPathAsync(
        string adapterId,
        string profileId,
        string preferredFileName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(preferredFileName);
        if (!StringComparer.Ordinal.Equals(preferredFileName, Path.GetFileName(preferredFileName)))
        {
            throw new ArgumentException("Preferred mapping must use a file name only.", nameof(preferredFileName));
        }

        string identity = $"{adapterId}/{profileId}";
        ScopedMotaraFile[] candidates = (await ScanAsync(cancellationToken).ConfigureAwait(false))
            .Files
            .Where(file => file.Kind == ScopedMotaraFileKind.Mapping
                && StringComparer.Ordinal.Equals(file.MappingIdentity, identity))
            .ToArray();
        return candidates.FirstOrDefault(file => StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFileName(file.Path),
                preferredFileName))?.Path
            ?? candidates.FirstOrDefault(static file => file.IsCanonical)?.Path
            ?? (candidates.Length == 1 ? candidates[0].Path : null);
    }

    public async Task<ScopedMotaraOrganizationResult> OrganizeAsync(
        CancellationToken cancellationToken)
    {
        ScopedMotaraScanResult scan = await ScanAsync(cancellationToken).ConfigureAwait(false);
        return await OrganizeAsync(scan, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScopedMotaraOrganizationResult> OrganizeAsync(
        ScopedMotaraScanResult scan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scan);
        OrganizationPlan plan = await CreatePlanAsync(scan, cancellationToken).ConfigureAwait(false);
        if (!plan.Conflicts.IsEmpty)
        {
            ScopedMotaraStorageLog.OrganizationBlocked(logger, plan.Conflicts.Length);
            return new(false, 0, 0, plan.Conflicts);
        }

        if (plan.Moves.IsEmpty && plan.Duplicates.IsEmpty)
        {
            EnsureCanonicalDirectories();
            ScopedMotaraStorageLog.OrganizationCompleted(logger, 0, 0);
            return new(true, 0, 0, []);
        }

        string staging = Path.Combine(scopeRoot, $".motara-organize-{Guid.NewGuid():N}");
        var completed = new Stack<(string From, string To)>();
        try
        {
            Directory.CreateDirectory(staging);
            int duplicateIndex = 0;
            foreach (string duplicate in plan.Duplicates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string staged = Path.Combine(staging, $"duplicate-{duplicateIndex++:D4}.bak");
                File.Move(duplicate, staged);
                completed.Push((staged, duplicate));
            }

            foreach ((string source, string target) in plan.Moves)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(source, target);
                completed.Push((target, source));
            }

            EnsureCanonicalDirectories();
            Directory.Delete(staging, recursive: true);
            ScopedMotaraStorageLog.OrganizationCompleted(
                logger,
                plan.Moves.Length,
                plan.Duplicates.Length);
            return new(true, plan.Moves.Length, plan.Duplicates.Length, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            while (completed.TryPop(out (string From, string To) move))
            {
                if (File.Exists(move.From))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(move.To)!);
                    File.Move(move.From, move.To, overwrite: true);
                }
            }

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            ScopedMotaraStorageLog.OrganizationFailed(logger, exception.GetType().Name);
            throw;
        }
    }

    private void EnsureCanonicalDirectories()
    {
        Directory.CreateDirectory(MotaraDirectory);
        Directory.CreateDirectory(MappingsDirectory);
        Directory.CreateDirectory(AssetsDirectory);
        Directory.CreateDirectory(EffectsDirectory);
    }

    private async Task<ScopedMotaraScanResult> ScanCoreAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(scopeRoot))
        {
            return new([]);
        }

        var files = ImmutableArray.CreateBuilder<ScopedMotaraFile>();
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((scopeRoot, 0));
        int visited = 0;
        while (pending.TryPop(out (string Directory, int Depth) current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.Depth > MaximumDepth)
            {
                throw new InvalidDataException("Motara scope exceeds the scan depth limit.");
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(current.Directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!PathEquals(entry, Path.Combine(scopeRoot, ".git")))
                    {
                        pending.Push((entry, current.Depth + 1));
                    }

                    continue;
                }

                if (++visited > MaximumFiles)
                {
                    throw new InvalidDataException("Motara scope exceeds the file count limit.");
                }

                string name = Path.GetFileName(entry);
                if (IsManifestName(name))
                {
                    files.Add(new(
                        Path.GetFullPath(entry),
                        ScopedMotaraFileKind.Manifest,
                        PathEquals(entry, ManifestPath),
                        null));
                    continue;
                }

                if (!name.EndsWith(".mapping.motara.json", StringComparison.OrdinalIgnoreCase)
                    || new FileInfo(entry).Length > MaximumConfigurationBytes)
                {
                    continue;
                }

                string? identity = await ReadMappingIdentityAsync(entry, cancellationToken)
                    .ConfigureAwait(false);
                if (identity is not null)
                {
                    files.Add(new(
                        Path.GetFullPath(entry),
                        ScopedMotaraFileKind.Mapping,
                        PathEquals(Path.GetDirectoryName(entry)!, MappingsDirectory),
                        identity));
                }
            }
        }

        ScopedMotaraStorageLog.ScanCompleted(
            logger,
            files.Count,
            files.Count(static file => !file.IsCanonical));
        return new(files.ToImmutable());
    }

    private bool IsManifestName(string name) =>
        StringComparer.OrdinalIgnoreCase.Equals(name, manifestFileName)
        || (modelName is not null
            && StringComparer.OrdinalIgnoreCase.Equals(name, modelName + ".motara.json"));

    private async Task<OrganizationPlan> CreatePlanAsync(
        ScopedMotaraScanResult scan,
        CancellationToken cancellationToken)
    {
        var moves = ImmutableArray.CreateBuilder<(string Source, string Target)>();
        var duplicates = ImmutableArray.CreateBuilder<string>();
        var conflicts = ImmutableArray.CreateBuilder<ScopedMotaraOrganizationConflict>();
        ScopedMotaraFile[] manifests = scan.Files
            .Where(static file => file.Kind == ScopedMotaraFileKind.Manifest)
            .ToArray();
        if (manifests.Length > 1)
        {
            conflicts.Add(new("manifest", manifests.Length));
        }
        else if (manifests is [ScopedMotaraFile manifest] && !manifest.IsCanonical)
        {
            moves.Add((manifest.Path, ManifestPath));
        }

        foreach (IGrouping<string, ScopedMotaraFile> group in scan.Files
            .Where(static file => file.Kind == ScopedMotaraFileKind.Mapping)
            .GroupBy(static file => file.MappingIdentity!, StringComparer.Ordinal))
        {
            ScopedMotaraFile[] candidates = group
                .OrderByDescending(static file => file.IsCanonical)
                .ThenBy(static file => file.Path, PathComparer)
                .ToArray();
            byte[] baseline = await File.ReadAllBytesAsync(candidates[0].Path, cancellationToken)
                .ConfigureAwait(false);
            bool same = true;
            for (int index = 1; index < candidates.Length; index++)
            {
                byte[] content = await File.ReadAllBytesAsync(candidates[index].Path, cancellationToken)
                    .ConfigureAwait(false);
                if (!baseline.AsSpan().SequenceEqual(content))
                {
                    same = false;
                    break;
                }
            }

            if (!same)
            {
                conflicts.Add(new(group.Key, candidates.Length));
                continue;
            }

            ScopedMotaraFile primary = candidates[0];
            string target = Path.Combine(MappingsDirectory, Path.GetFileName(primary.Path));
            if (!PathEquals(primary.Path, target))
            {
                moves.Add((primary.Path, target));
            }

            foreach (ScopedMotaraFile duplicate in candidates.Skip(1))
            {
                duplicates.Add(duplicate.Path);
            }
        }

        string[] duplicateTargets = moves
            .GroupBy(static move => move.Target, PathComparer)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();
        foreach (string target in duplicateTargets)
        {
            conflicts.Add(new(Path.GetFileName(target), moves.Count(move => PathEquals(move.Target, target))));
        }

        return new(moves.ToImmutable(), duplicates.ToImmutable(), conflicts.ToImmutable());
    }

    private static async Task<string?> ReadMappingIdentityAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using JsonDocument document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out JsonElement schema)
                || !schema.TryGetInt32(out int schemaVersion)
                || schemaVersion != 1
                || !TryReadId(root, "adapterId", out string adapterId)
                || !TryReadId(root, "profileId", out string profileId))
            {
                return null;
            }

            return $"{adapterId}/{profileId}";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadId(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            return false;
        }

        value = property.GetString()!;
        return true;
    }

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record OrganizationPlan(
        ImmutableArray<(string Source, string Target)> Moves,
        ImmutableArray<string> Duplicates,
        ImmutableArray<ScopedMotaraOrganizationConflict> Conflicts);
}

internal static partial class ScopedMotaraStorageLog
{
    [LoggerMessage(2020, LogLevel.Debug,
        "Motara scope scan completed with {RecognizedFileCount} recognized files and {NonCanonicalFileCount} non-canonical files")]
    internal static partial void ScanCompleted(
        ILogger logger,
        int recognizedFileCount,
        int nonCanonicalFileCount);

    [LoggerMessage(2021, LogLevel.Information,
        "Motara scope organization completed with {MovedFileCount} moved files and {MergedFileCount} merged duplicates")]
    internal static partial void OrganizationCompleted(
        ILogger logger,
        int movedFileCount,
        int mergedFileCount);

    [LoggerMessage(2022, LogLevel.Warning,
        "Motara scope organization blocked by {ConflictCount} conflicts")]
    internal static partial void OrganizationBlocked(ILogger logger, int conflictCount);

    [LoggerMessage(2023, LogLevel.Error,
        "Motara scope organization failed with {ErrorType}")]
    internal static partial void OrganizationFailed(ILogger logger, string errorType);
}
