using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.ModelLibrary;
using Motara.Persistence;

namespace Motara.App.Models;

internal enum ModelOrganizationFileKind
{
    Motion,
    Expression,
    Preview,
}

internal sealed record ModelFileOrganizationRequest(
    string ModelId,
    string DisplayName,
    string RootPath,
    string DescriptorPath,
    ModelDescriptor Descriptor);

internal sealed record ModelFileOrganizationMove(
    string SourcePath,
    string TargetPath,
    ModelOrganizationFileKind Kind);

internal sealed record ModelFileOrganizationAnalysis(
    bool CanOrganize,
    bool NeedsOrganization,
    ImmutableArray<ModelFileOrganizationMove> Moves,
    string? PreviewRelativePath,
    string? ErrorCode);

internal sealed record ModelFileOrganizationResult(
    bool Succeeded,
    int MovedFileCount,
    string? ErrorCode);

internal interface IModelFileOrganizationService
{
    Task<ModelFileOrganizationAnalysis> AnalyzeAsync(
        ModelFileOrganizationRequest request,
        CancellationToken cancellationToken);

    Task<ModelFileOrganizationResult> OrganizeAsync(
        ModelFileOrganizationRequest request,
        CancellationToken cancellationToken);
}

internal sealed class ModelFileOrganizationService : IModelFileOrganizationService
{
    private const int MaximumDepth = 32;
    private const int MaximumFiles = 20_000;
    private const long MaximumJsonBytes = 16 * 1024 * 1024;
    private static readonly string[] PreviewExtensions = [".png", ".webp", ".jpg", ".jpeg", ".bmp"];
    private static readonly JsonSerializerOptions WriteJsonOptions = new() { WriteIndented = true };
    private readonly ILogger<ModelFileOrganizationService> logger;
    private readonly ModelPreviewNormalizer previewNormalizer;

    internal ModelFileOrganizationService(
        ILogger<ModelFileOrganizationService>? logger = null,
        ModelPreviewNormalizer? previewNormalizer = null)
    {
        this.logger = logger ?? NullLogger<ModelFileOrganizationService>.Instance;
        this.previewNormalizer = previewNormalizer ?? new ModelPreviewNormalizer();
    }

    public Task<ModelFileOrganizationAnalysis> AnalyzeAsync(
        ModelFileOrganizationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => AnalyzeCoreAsync(request, cancellationToken), cancellationToken);
    }

    public async Task<ModelFileOrganizationResult> OrganizeAsync(
        ModelFileOrganizationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Stopwatch stopwatch = Stopwatch.StartNew();
        ModelFileOrganizationLog.ExecutionStarted(logger, request.ModelId);
        ModelFileOrganizationAnalysis analysis = await AnalyzeAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!analysis.CanOrganize)
        {
            ModelFileOrganizationLog.ExecutionBlocked(logger, request.ModelId, analysis.ErrorCode ?? "Invalid");
            return new(false, 0, analysis.ErrorCode);
        }

        string root = NormalizeRoot(request.RootPath);
        string descriptorPath = NormalizeWithinRoot(root, request.DescriptorPath);
        var storage = new ScopedMotaraStorage(root, "model.motara.json", request.DisplayName);
        byte[] descriptorBackup = await File.ReadAllBytesAsync(descriptorPath, cancellationToken)
            .ConfigureAwait(false);
        byte[]? manifestBackup = File.Exists(storage.ManifestPath)
            ? await File.ReadAllBytesAsync(storage.ManifestPath, cancellationToken).ConfigureAwait(false)
            : null;
        var completedMoves = new Stack<ModelFileOrganizationMove>();
        ModelFileOrganizationMove? previewMove = analysis.Moves.FirstOrDefault(
            static move => move.Kind == ModelOrganizationFileKind.Preview);
        byte[]? previewBackup = previewMove is null
            ? null
            : await File.ReadAllBytesAsync(previewMove.SourcePath, cancellationToken).ConfigureAwait(false);
        try
        {
            ScopedMotaraOrganizationResult scopedResult = await storage.OrganizeAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!scopedResult.Succeeded)
            {
                return new(false, 0, "MotaraManifestConflict");
            }

            foreach (ModelFileOrganizationMove move in analysis.Moves)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(move.TargetPath)!);
                if (move.Kind == ModelOrganizationFileKind.Preview)
                {
                    await previewNormalizer.NormalizeAsync(
                        move.SourcePath,
                        move.TargetPath,
                        cancellationToken).ConfigureAwait(false);
                    completedMoves.Push(move);
                    if (!PathEquals(move.SourcePath, move.TargetPath))
                    {
                        File.Delete(move.SourcePath);
                    }
                    continue;
                }
                else
                {
                    File.Move(move.SourcePath, move.TargetPath);
                }
                completedMoves.Push(move);
            }

            await RewriteDescriptorAsync(
                descriptorPath,
                root,
                analysis.Moves,
                cancellationToken).ConfigureAwait(false);

            var configurationStore = new MotaraModelConfigurationStore(root, request.DisplayName);
            MotaraModelConfiguration configuration = await configurationStore.LoadAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? MotaraModelConfiguration.Create(request.ModelId);
            configuration = configuration with
            {
                FileLayout = new ModelFileLayoutConfiguration(analysis.PreviewRelativePath),
            };
            await configurationStore.SaveAsync(configuration, cancellationToken).ConfigureAwait(false);

            ModelFileOrganizationLog.ExecutionCompleted(
                logger,
                request.ModelId,
                completedMoves.Count,
                stopwatch.ElapsedMilliseconds);
            return new(true, completedMoves.Count, null);
        }
        catch (OperationCanceledException)
        {
            await RollbackAsync(
                descriptorPath,
                descriptorBackup,
                storage.ManifestPath,
                manifestBackup,
                completedMoves,
                previewBackup).ConfigureAwait(false);
            ModelFileOrganizationLog.ExecutionCancelled(logger, request.ModelId, stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException
            or InvalidDataException)
        {
            bool rolledBack = await RollbackAsync(
                descriptorPath,
                descriptorBackup,
                storage.ManifestPath,
                manifestBackup,
                completedMoves,
                previewBackup).ConfigureAwait(false);
            ModelFileOrganizationLog.ExecutionFailed(
                logger,
                request.ModelId,
                exception.GetType().Name,
                rolledBack,
                stopwatch.ElapsedMilliseconds);
            return new(false, 0, exception.GetType().Name);
        }
    }

    private async Task<ModelFileOrganizationAnalysis> AnalyzeCoreAsync(
        ModelFileOrganizationRequest request,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        ModelFileOrganizationLog.AnalysisStarted(logger, request.ModelId);
        try
        {
            string root = NormalizeRoot(request.RootPath);
            string descriptorPath = NormalizeWithinRoot(root, request.DescriptorPath);
            JsonObject descriptor = await ReadJsonObjectAsync(descriptorPath, cancellationToken)
                .ConfigureAwait(false);
            ValidateDescriptorReferences(descriptor, root);
            await ValidateOptionalJsonReferencesAsync(descriptor, root, cancellationToken)
                .ConfigureAwait(false);
            ImmutableArray<string> files = EnumerateFiles(root, cancellationToken);
            var moves = ImmutableArray.CreateBuilder<ModelFileOrganizationMove>();
            AddFlatMoves(files, root, "motions", ".motion3.json", ModelOrganizationFileKind.Motion, moves);
            AddFlatMoves(files, root, "exps", ".exp3.json", ModelOrganizationFileKind.Expression, moves);

            foreach (string path in files.Where(static path =>
                path.EndsWith(".motion3.json", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".exp3.json", StringComparison.OrdinalIgnoreCase)))
            {
                _ = await ReadJsonObjectAsync(path, cancellationToken).ConfigureAwait(false);
            }

            string? previewSource = SelectPreview(files, root, request.DisplayName);
            string? previewRelative = null;
            if (previewSource is not null)
            {
                string previewTarget = Path.Combine(root, "motara", "assets", "preview.png");
                previewRelative = Relative(root, previewTarget);
                if (!PathEquals(previewSource, previewTarget)
                    || !ModelPreviewNormalizer.IsNormalized(previewTarget))
                {
                    if (!PathEquals(previewSource, previewTarget) && File.Exists(previewTarget))
                    {
                        throw new InvalidDataException("The canonical preview target is occupied.");
                    }

                    moves.Add(new(previewSource, previewTarget, ModelOrganizationFileKind.Preview));
                }
            }

            var storage = new ScopedMotaraStorage(root, "model.motara.json", request.DisplayName);
            ScopedMotaraScanResult motaraScan = await storage.ScanAsync(cancellationToken).ConfigureAwait(false);
            if (motaraScan.ManifestStatus == ScopedMotaraManifestStatus.Conflict)
            {
                return CompleteAnalysis(request.ModelId, false, true, moves, previewRelative,
                    "MotaraManifestConflict", stopwatch);
            }

            bool layoutCurrent = false;
            if (File.Exists(storage.ManifestPath))
            {
                try
                {
                    MotaraModelConfiguration? configuration = await new MotaraModelConfigurationStore(
                        root,
                        request.DisplayName).LoadAsync(cancellationToken).ConfigureAwait(false);
                    layoutCurrent = configuration?.FileLayout is not null
                        && StringComparer.Ordinal.Equals(
                            configuration.FileLayout.Preview,
                            previewRelative);
                }
                catch (Exception exception) when (exception is JsonException or ArgumentException)
                {
                    return CompleteAnalysis(request.ModelId, false, true, moves, previewRelative,
                        "InvalidMotaraManifest", stopwatch);
                }
            }

            bool needsOrganization = moves.Count > 0
                || motaraScan.ManifestStatus != ScopedMotaraManifestStatus.Canonical
                || !layoutCurrent
                || DescriptorNeedsRewrite(descriptor, root, moves);
            return CompleteAnalysis(request.ModelId, true, needsOrganization, moves, previewRelative, null, stopwatch);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException
            or InvalidDataException)
        {
            ModelFileOrganizationLog.AnalysisFailed(
                logger,
                request.ModelId,
                exception.GetType().Name,
                stopwatch.ElapsedMilliseconds);
            return new(false, true, [], null, exception.GetType().Name);
        }
    }

    private ModelFileOrganizationAnalysis CompleteAnalysis(
        string modelId,
        bool canOrganize,
        bool needsOrganization,
        ImmutableArray<ModelFileOrganizationMove>.Builder moves,
        string? previewRelative,
        string? errorCode,
        Stopwatch stopwatch)
    {
        ImmutableArray<ModelFileOrganizationMove> resultMoves = moves.ToImmutable();
        ModelFileOrganizationLog.AnalysisCompleted(
            logger,
            modelId,
            resultMoves.Count(static move => move.Kind == ModelOrganizationFileKind.Motion),
            resultMoves.Count(static move => move.Kind == ModelOrganizationFileKind.Expression),
            resultMoves.Any(static move => move.Kind == ModelOrganizationFileKind.Preview),
            needsOrganization,
            stopwatch.ElapsedMilliseconds);
        return new(canOrganize, needsOrganization, resultMoves, previewRelative, errorCode);
    }

    private static void AddFlatMoves(
        ImmutableArray<string> files,
        string root,
        string targetDirectoryName,
        string fullSuffix,
        ModelOrganizationFileKind kind,
        ImmutableArray<ModelFileOrganizationMove>.Builder moves)
    {
        string targetDirectory = Path.Combine(root, targetDirectoryName);
        string[] candidates = files
            .Where(path => path.EndsWith(fullSuffix, StringComparison.OrdinalIgnoreCase))
            .Order(PathComparer)
            .ToArray();
        var occupied = new HashSet<string>(PathComparer);
        foreach (string canonical in candidates.Where(path => PathEquals(
            Path.GetDirectoryName(path)!, targetDirectory)))
        {
            occupied.Add(Path.GetFileName(canonical));
        }

        foreach (string source in candidates.Where(path => !PathEquals(
            Path.GetDirectoryName(path)!, targetDirectory)))
        {
            string filename = Path.GetFileName(source);
            string stem = filename[..^fullSuffix.Length];
            string targetName = filename;
            for (int suffix = 2; !occupied.Add(targetName); suffix++)
            {
                targetName = stem + suffix + fullSuffix;
            }

            moves.Add(new(source, Path.Combine(targetDirectory, targetName), kind));
        }
    }

    private static string? SelectPreview(
        ImmutableArray<string> files,
        string root,
        string modelName)
    {
        string canonicalDirectory = Path.Combine(root, "motara", "assets");
        return files
            .Select(path => new
            {
                Path = path,
                Name = Path.GetFileNameWithoutExtension(path),
                ExtensionRank = Array.FindIndex(PreviewExtensions, extension =>
                    extension.Equals(Path.GetExtension(path), StringComparison.OrdinalIgnoreCase)),
            })
            .Where(static candidate => candidate.ExtensionRank >= 0)
            .Select(candidate => new
            {
                candidate.Path,
                candidate.ExtensionRank,
                NameRank = PathEquals(Path.GetDirectoryName(candidate.Path)!, canonicalDirectory)
                    && StringComparer.OrdinalIgnoreCase.Equals(candidate.Name, "preview") ? 0
                    : StringComparer.OrdinalIgnoreCase.Equals(candidate.Name, modelName) ? 1
                    : StringComparer.OrdinalIgnoreCase.Equals(candidate.Name, "icon") ? 2
                    : 3,
            })
            .Where(static candidate => candidate.NameRank < 3)
            .OrderBy(static candidate => candidate.NameRank)
            .ThenBy(static candidate => candidate.ExtensionRank)
            .ThenBy(static candidate => candidate.Path, PathComparer)
            .Select(static candidate => candidate.Path)
            .FirstOrDefault();
    }

    private static void ValidateDescriptorReferences(JsonObject descriptor, string root)
    {
        if (descriptor["Version"]?.GetValue<int>() != 3
            || descriptor["FileReferences"] is not JsonObject references
            || references["Moc"] is not JsonValue moc
            || references["Textures"] is not JsonArray textures
            || textures.Count == 0)
        {
            throw new InvalidDataException("The model descriptor is invalid.");
        }

        RequireFile(root, moc.GetValue<string>());
        foreach (JsonNode? texture in textures)
        {
            RequireFile(root, texture?.GetValue<string>());
        }

        foreach (string name in new[] { "Physics", "DisplayInfo", "Pose", "UserData" })
        {
            if (references[name] is JsonValue optional)
            {
                RequireFile(root, optional.GetValue<string>());
            }
        }

        if (references["Motions"] is JsonObject motionGroups)
        {
            foreach (KeyValuePair<string, JsonNode?> group in motionGroups)
            {
                if (group.Value is not JsonArray entries)
                {
                    throw new InvalidDataException("A motion group must contain an array.");
                }
                ValidateEntryReferences(entries, root);
            }
        }
        else if (references["Motions"] is not null)
        {
            throw new InvalidDataException("Motions must contain an object.");
        }

        if (references["Expressions"] is JsonArray expressions)
        {
            ValidateEntryReferences(expressions, root);
        }
        else if (references["Expressions"] is not null)
        {
            throw new InvalidDataException("Expressions must contain an array.");
        }
    }

    private static async Task ValidateOptionalJsonReferencesAsync(
        JsonObject descriptor,
        string root,
        CancellationToken cancellationToken)
    {
        var references = (JsonObject)descriptor["FileReferences"]!;
        foreach (string name in new[] { "Physics", "DisplayInfo", "Pose", "UserData" })
        {
            if (references[name] is JsonValue value)
            {
                string path = NormalizeWithinRoot(root, Path.Combine(root, value.GetValue<string>()));
                _ = await ReadJsonObjectAsync(path, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void ValidateEntryReferences(JsonArray entries, string root)
    {
        foreach (JsonNode? node in entries)
        {
            if (node is not JsonObject entry || entry["File"] is not JsonValue file)
            {
                throw new InvalidDataException("A model asset entry requires a file reference.");
            }
            RequireFile(root, file.GetValue<string>());
        }
    }

    private static void RequireFile(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)
            || !File.Exists(NormalizeWithinRoot(root, Path.Combine(root, relative))))
        {
            throw new InvalidDataException("A declared model file is missing.");
        }
    }

    private static bool DescriptorNeedsRewrite(
        JsonObject descriptor,
        string root,
        ImmutableArray<ModelFileOrganizationMove>.Builder moves)
    {
        if (moves.Any(static move => move.Kind is ModelOrganizationFileKind.Motion
            or ModelOrganizationFileKind.Expression))
        {
            return true;
        }

        JsonObject references = (JsonObject)descriptor["FileReferences"]!;
        return ReferencesOutsideDirectory(references["Motions"], root, "motions", ".motion3.json")
            || ReferencesOutsideDirectory(references["Expressions"], root, "exps", ".exp3.json");
    }

    private static bool ReferencesOutsideDirectory(
        JsonNode? node,
        string root,
        string directory,
        string suffix)
    {
        if (node is null)
        {
            return false;
        }

        IEnumerable<JsonObject> entries = node switch
        {
            JsonArray array => array.OfType<JsonObject>(),
            JsonObject groups => groups.SelectMany(static property =>
                property.Value is JsonArray group ? group.OfType<JsonObject>() : []),
            _ => [],
        };
        return entries.Any(entry => entry["File"] is JsonValue file
            && !StringComparer.Ordinal.Equals(
                Relative(root, NormalizeWithinRoot(root, Path.Combine(root, file.GetValue<string>())))
                    .Split('/')[0],
                directory)
            && file.GetValue<string>().EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task RewriteDescriptorAsync(
        string descriptorPath,
        string root,
        ImmutableArray<ModelFileOrganizationMove> moves,
        CancellationToken cancellationToken)
    {
        var mappings = moves
            .Where(static move => move.Kind is ModelOrganizationFileKind.Motion
                or ModelOrganizationFileKind.Expression)
            .ToDictionary(
                static move => Path.GetFullPath(move.SourcePath),
                move => Relative(root, move.TargetPath),
                PathComparer);
        if (mappings.Count == 0)
        {
            return;
        }

        JsonObject descriptor = await ReadJsonObjectAsync(descriptorPath, cancellationToken)
            .ConfigureAwait(false);
        JsonObject references = (JsonObject)descriptor["FileReferences"]!;
        var referenced = new HashSet<string>(PathComparer);
        if (references["Motions"] is not JsonObject motionGroups)
        {
            motionGroups = new JsonObject();
            references["Motions"] = motionGroups;
        }

        RewriteEntries(motionGroups.SelectMany(static property =>
            property.Value is JsonArray array ? array.OfType<JsonObject>() : []), root, mappings, referenced);
        if (references["Expressions"] is not JsonArray expressions)
        {
            expressions = new JsonArray();
            references["Expressions"] = expressions;
        }

        RewriteEntries(expressions.OfType<JsonObject>(), root, mappings, referenced);
        foreach ((string source, string target) in mappings.OrderBy(static pair => pair.Value, StringComparer.Ordinal))
        {
            if (!referenced.Add(source))
            {
                continue;
            }

            if (target.EndsWith(".motion3.json", StringComparison.OrdinalIgnoreCase))
            {
                if (motionGroups["Imported"] is not JsonArray imported)
                {
                    imported = new JsonArray();
                    motionGroups["Imported"] = imported;
                }

                imported.Add(new JsonObject { ["File"] = target });
            }
            else
            {
                string name = Path.GetFileName(target)[..^".exp3.json".Length];
                expressions.Add(new JsonObject { ["Name"] = name, ["File"] = target });
            }
        }

        await WriteJsonAtomicallyAsync(descriptorPath, descriptor, cancellationToken).ConfigureAwait(false);
    }

    private static void RewriteEntries(
        IEnumerable<JsonObject> entries,
        string root,
        Dictionary<string, string> mappings,
        HashSet<string> referenced)
    {
        foreach (JsonObject entry in entries)
        {
            if (entry["File"] is not JsonValue file)
            {
                continue;
            }

            string source = NormalizeWithinRoot(root, Path.Combine(root, file.GetValue<string>()));
            if (mappings.TryGetValue(source, out string? target))
            {
                entry["File"] = target;
                referenced.Add(source);
            }
        }
    }

    private static ImmutableArray<string> EnumerateFiles(string root, CancellationToken cancellationToken)
    {
        var files = ImmutableArray.CreateBuilder<string>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        while (pending.TryPop(out (string Path, int Depth) current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.Depth > MaximumDepth)
            {
                throw new InvalidDataException("The model directory exceeds the scan depth limit.");
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(current.Path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!Path.GetFileName(entry).StartsWith(".motara-organize-", StringComparison.Ordinal))
                    {
                        pending.Push((entry, current.Depth + 1));
                    }
                    continue;
                }

                if (files.Count >= MaximumFiles)
                {
                    throw new InvalidDataException("The model directory exceeds the file count limit.");
                }
                files.Add(Path.GetFullPath(entry));
            }
        }

        return files.ToImmutable();
    }

    private static async Task<JsonObject> ReadJsonObjectAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaximumJsonBytes)
        {
            throw new InvalidDataException("A model JSON file is missing or too large.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        JsonNode? node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return node as JsonObject ?? throw new InvalidDataException("A model JSON file must contain an object.");
    }

    private static async Task WriteJsonAtomicallyAsync(
        string path,
        JsonObject json,
        CancellationToken cancellationToken)
    {
        string temporary = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}-{Guid.NewGuid():N}.tmp");
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
                await JsonSerializer.SerializeAsync(
                    stream,
                    json,
                    WriteJsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
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

    private static async Task<bool> RollbackAsync(
        string descriptorPath,
        byte[] descriptorBackup,
        string manifestPath,
        byte[]? manifestBackup,
        Stack<ModelFileOrganizationMove> completedMoves,
        byte[]? previewBackup)
    {
        try
        {
            await File.WriteAllBytesAsync(descriptorPath, descriptorBackup).ConfigureAwait(false);
            if (manifestBackup is null)
            {
                if (File.Exists(manifestPath))
                {
                    File.Delete(manifestPath);
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
                await File.WriteAllBytesAsync(manifestPath, manifestBackup).ConfigureAwait(false);
            }

            while (completedMoves.TryPop(out ModelFileOrganizationMove? move))
            {
                if (move.Kind == ModelOrganizationFileKind.Preview)
                {
                    if (File.Exists(move.TargetPath))
                    {
                        File.Delete(move.TargetPath);
                    }
                    if (previewBackup is not null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(move.SourcePath)!);
                        await File.WriteAllBytesAsync(move.SourcePath, previewBackup).ConfigureAwait(false);
                    }
                    continue;
                }

                if (File.Exists(move.TargetPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(move.SourcePath)!);
                    File.Move(move.TargetPath, move.SourcePath, overwrite: true);
                }
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string NormalizeRoot(string root) => Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(root));

    private static string NormalizeWithinRoot(string root, string path)
    {
        string full = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(root, full);
        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("A model path escapes the model root.");
        }
        return full;
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static bool PathEquals(string left, string right) => PathComparer.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right));

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
