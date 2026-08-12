using System.Collections.Immutable;
using System.Text.Json;
using Motara.ModelRuntime.Abstractions;

namespace Motara.ModelLibrary;

public sealed record ModelDescriptor(
    string DescriptorPath,
    string RootPath,
    string NativeModelPath,
    ImmutableArray<string> TexturePaths,
    string? ThumbnailPath = null,
    Moc3FormatVersion FormatVersion = Moc3FormatVersion.Unknown)
{
    public ImmutableDictionary<string, string> ParameterNames { get; init; } =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    public ImmutableArray<ModelRuntimeAsset> RuntimeAssets { get; init; } = [];

    public ImmutableArray<ModelAuxiliaryAsset> Pose { get; init; } = [];

    public ImmutableArray<ModelAuxiliaryAsset> Motions { get; init; } = [];

    public ImmutableArray<ModelAuxiliaryAsset> Expressions { get; init; } = [];

    public int MissingOptionalAssetCount { get; init; }

    public ModelFileLayoutStatus FileLayoutStatus { get; init; } = ModelFileLayoutStatus.Unknown;

    public string? Nickname { get; init; }

    public ImmutableArray<ModelAuxiliaryAsset> AuxiliaryAssets => [.. Pose, .. Motions, .. Expressions];
}

public enum ModelFileLayoutStatus
{
    Unknown = 0,
    Canonical = 1,
    Stale = 2,
}

public enum ModelRuntimeAssetKind
{
    Descriptor,
    NativeModel,
    Texture,
    Physics,
    Motion,
    Expression,
    Pose,
}

public sealed record ModelRuntimeAsset(string Path, ModelRuntimeAssetKind Kind);

public enum Moc3FormatVersion
{
    Unknown = 0,
    Version30 = 1,
    Version33 = 2,
    Version40 = 3,
    Version42 = 4,
    Version50 = 5,
    Version53 = 6,
}

internal sealed class ModelDescriptorException(ModelErrorCode code) : Exception
{
    public ModelErrorCode Code { get; } = code;
}

internal static class ModelDescriptorReader
{
    public static async Task<ModelDescriptor> ReadAsync(
        string descriptorPath,
        long maxDescriptorBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedDescriptorPath = Path.GetFullPath(descriptorPath);
        var descriptorFile = new FileInfo(normalizedDescriptorPath);
        if (!descriptorFile.Exists)
        {
            throw new ModelDescriptorException(ModelErrorCode.MissingReference);
        }

        if (descriptorFile.Length > maxDescriptorBytes)
        {
            throw new ModelDescriptorException(ModelErrorCode.SizeLimitExceeded);
        }

        string rootPath = descriptorFile.Directory?.FullName
            ?? throw new ModelDescriptorException(ModelErrorCode.InvalidDescriptor);
        try
        {
            await using FileStream stream = new(
                normalizedDescriptorPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using JsonDocument document = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                },
                cancellationToken);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("Version", out JsonElement version)
                || !version.TryGetInt32(out int versionNumber)
                || versionNumber != 3
                || !root.TryGetProperty("FileReferences", out JsonElement references)
                || references.ValueKind != JsonValueKind.Object
                || !references.TryGetProperty("Moc", out JsonElement nativeModel)
                || nativeModel.ValueKind != JsonValueKind.String
                || !references.TryGetProperty("Textures", out JsonElement textures)
                || textures.ValueKind != JsonValueKind.Array)
            {
                throw new ModelDescriptorException(ModelErrorCode.InvalidDescriptor);
            }

            string nativeModelPath = ResolveReference(rootPath, nativeModel.GetString());
            ImmutableDictionary<string, string> parameterNames = await ReadParameterNamesAsync(
                rootPath,
                references,
                maxDescriptorBytes,
                cancellationToken).ConfigureAwait(false);
            ImmutableArray<string>.Builder texturePaths = ImmutableArray.CreateBuilder<string>();
            foreach (JsonElement texture in textures.EnumerateArray())
            {
                if (texture.ValueKind != JsonValueKind.String)
                {
                    throw new ModelDescriptorException(ModelErrorCode.InvalidDescriptor);
                }

                texturePaths.Add(ResolveReference(rootPath, texture.GetString()));
            }

            if (texturePaths.Count == 0)
            {
                throw new ModelDescriptorException(ModelErrorCode.InvalidDescriptor);
            }

            string modelName = ModelIdentity
                .FromDescriptorFilename(descriptorFile.Name)
                .DisplayName;
            (string? thumbnailPath, ModelFileLayoutStatus fileLayoutStatus, string? nickname) =
                await FindThumbnailAsync(rootPath, modelName, cancellationToken).ConfigureAwait(false);
            Moc3FormatVersion formatVersion = await ReadFormatVersionAsync(
                nativeModelPath,
                cancellationToken).ConfigureAwait(false);
            int missingOptionalAssetCount = 0;
            ImmutableArray<ModelRuntimeAsset> runtimeAssets = ReadRuntimeAssets(
                normalizedDescriptorPath,
                rootPath,
                nativeModelPath,
                texturePaths,
                references,
                ref missingOptionalAssetCount);
            ImmutableArray<ModelAuxiliaryAsset> auxiliaryAssets = ReadAuxiliaryAssets(rootPath, references);

            return new ModelDescriptor(
                normalizedDescriptorPath,
                rootPath,
                nativeModelPath,
                texturePaths.ToImmutable(),
                thumbnailPath,
                formatVersion)
            {
                ParameterNames = parameterNames,
                RuntimeAssets = runtimeAssets,
                Pose = auxiliaryAssets.Where(static asset => asset.Kind == ModelAuxiliaryAssetKind.Pose)
                    .ToImmutableArray(),
                Motions = auxiliaryAssets.Where(static asset => asset.Kind == ModelAuxiliaryAssetKind.Motion)
                    .ToImmutableArray(),
                Expressions = auxiliaryAssets.Where(static asset => asset.Kind == ModelAuxiliaryAssetKind.Expression)
                    .ToImmutableArray(),
                MissingOptionalAssetCount = missingOptionalAssetCount,
                FileLayoutStatus = fileLayoutStatus,
                Nickname = nickname,
            };
        }
        catch (ModelDescriptorException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new ModelDescriptorException(ModelErrorCode.InvalidDescriptor);
        }
        catch (IOException)
        {
            throw new ModelDescriptorException(ModelErrorCode.IoFailure);
        }
        catch (UnauthorizedAccessException)
        {
            throw new ModelDescriptorException(ModelErrorCode.IoFailure);
        }
    }

    private static ImmutableArray<ModelRuntimeAsset> ReadRuntimeAssets(
        string descriptorPath,
        string rootPath,
        string nativeModelPath,
        ImmutableArray<string>.Builder texturePaths,
        JsonElement references,
        ref int missingOptionalAssetCount)
    {
        var assets = new List<ModelRuntimeAsset>
        {
            new(descriptorPath, ModelRuntimeAssetKind.Descriptor),
            new(nativeModelPath, ModelRuntimeAssetKind.NativeModel),
        };
        assets.AddRange(texturePaths.Select(static path =>
            new ModelRuntimeAsset(path, ModelRuntimeAssetKind.Texture)));

        AddOptionalFileReference(
            assets, rootPath, references, "Physics", ModelRuntimeAssetKind.Physics, ref missingOptionalAssetCount);
        AddObjectArrayReferences(
            assets, rootPath, references, "Motions", ModelRuntimeAssetKind.Motion, ref missingOptionalAssetCount);
        AddArrayReferences(
            assets, rootPath, references, "Expressions", ModelRuntimeAssetKind.Expression, ref missingOptionalAssetCount);
        AddOptionalFileReference(
            assets, rootPath, references, "Pose", ModelRuntimeAssetKind.Pose, ref missingOptionalAssetCount);

        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        return assets
            .GroupBy(static asset => asset.Path, pathComparer)
            .Select(static group => group.First())
            .OrderBy(static asset => asset.Kind)
            .ThenBy(static asset => asset.Path, pathComparer)
            .ToImmutableArray();
    }

    private static ImmutableArray<ModelAuxiliaryAsset> ReadAuxiliaryAssets(
        string rootPath,
        JsonElement references)
    {
        var assets = ImmutableArray.CreateBuilder<ModelAuxiliaryAsset>();
        AddOptionalAuxiliaryAsset(
            assets,
            rootPath,
            references,
            "Pose",
            ModelAuxiliaryAssetKind.Pose);
        AddMotionAuxiliaryAssets(assets, rootPath, references);
        AddExpressionAuxiliaryAssets(assets, rootPath, references);
        return assets.ToImmutable();
    }

    private static void AddOptionalAuxiliaryAsset(
        ImmutableArray<ModelAuxiliaryAsset>.Builder assets,
        string rootPath,
        JsonElement references,
        string propertyName,
        ModelAuxiliaryAssetKind kind)
    {
        if (!references.TryGetProperty(propertyName, out JsonElement reference))
        {
            return;
        }

        if (reference.ValueKind != JsonValueKind.String)
        {
            return;
        }

        if (!TryResolveOptionalReference(rootPath, reference.GetString(), out string path))
        {
            return;
        }
        assets.Add(new ModelAuxiliaryAsset(
            ToAssetId(rootPath, path),
            kind,
            GetAssetName(path)));
    }

    private static void AddMotionAuxiliaryAssets(
        ImmutableArray<ModelAuxiliaryAsset>.Builder assets,
        string rootPath,
        JsonElement references)
    {
        if (!references.TryGetProperty("Motions", out JsonElement groups))
        {
            return;
        }

        if (groups.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty group in groups.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(group.Name) || group.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement entry in group.Value.EnumerateArray())
            {
                AddEntryAuxiliaryAsset(
                    assets,
                    rootPath,
                    entry,
                    ModelAuxiliaryAssetKind.Motion,
                    group.Name);
            }
        }
    }

    private static void AddExpressionAuxiliaryAssets(
        ImmutableArray<ModelAuxiliaryAsset>.Builder assets,
        string rootPath,
        JsonElement references)
    {
        if (!references.TryGetProperty("Expressions", out JsonElement entries))
        {
            return;
        }

        if (entries.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement entry in entries.EnumerateArray())
        {
            AddEntryAuxiliaryAsset(
                assets,
                rootPath,
                entry,
                ModelAuxiliaryAssetKind.Expression,
                group: null);
        }
    }

    private static void AddEntryAuxiliaryAsset(
        ImmutableArray<ModelAuxiliaryAsset>.Builder assets,
        string rootPath,
        JsonElement entry,
        ModelAuxiliaryAssetKind kind,
        string? group)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("File", out JsonElement file)
            || file.ValueKind != JsonValueKind.String)
        {
            return;
        }

        if (!TryResolveOptionalReference(rootPath, file.GetString(), out string path))
        {
            return;
        }
        string name = ReadEntryName(entry, path);
        assets.Add(new ModelAuxiliaryAsset(ToAssetId(rootPath, path), kind, name, group));
    }

    private static string ReadEntryName(JsonElement entry, string path)
    {
        if (!entry.TryGetProperty("Name", out JsonElement name))
        {
            return GetAssetName(path);
        }

        if (name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
        {
            return GetAssetName(path);
        }

        return name.GetString()!;
    }

    private static string GetAssetName(string path) => Path.GetFileNameWithoutExtension(
        Path.GetFileNameWithoutExtension(path));

    private static string ToAssetId(string rootPath, string path) => ModelAssetId.Normalize(
        Path.GetRelativePath(rootPath, path).Replace('\\', '/'));

    private static void AddOptionalFileReference(
        List<ModelRuntimeAsset> assets,
        string rootPath,
        JsonElement references,
        string propertyName,
        ModelRuntimeAssetKind kind,
        ref int missingOptionalAssetCount)
    {
        if (!references.TryGetProperty(propertyName, out JsonElement reference))
        {
            return;
        }

        if (reference.ValueKind != JsonValueKind.String)
        {
            missingOptionalAssetCount++;
            return;
        }

        if (TryResolveOptionalReference(rootPath, reference.GetString(), out string path))
        {
            assets.Add(new ModelRuntimeAsset(path, kind));
            return;
        }

        missingOptionalAssetCount++;
    }

    private static void AddArrayReferences(
        List<ModelRuntimeAsset> assets,
        string rootPath,
        JsonElement references,
        string propertyName,
        ModelRuntimeAssetKind kind,
        ref int missingOptionalAssetCount)
    {
        if (!references.TryGetProperty(propertyName, out JsonElement entries))
        {
            return;
        }

        if (entries.ValueKind != JsonValueKind.Array)
        {
            missingOptionalAssetCount++;
            return;
        }

        foreach (JsonElement entry in entries.EnumerateArray())
        {
            AddEntryFileReference(assets, rootPath, entry, kind, ref missingOptionalAssetCount);
        }
    }

    private static void AddObjectArrayReferences(
        List<ModelRuntimeAsset> assets,
        string rootPath,
        JsonElement references,
        string propertyName,
        ModelRuntimeAssetKind kind,
        ref int missingOptionalAssetCount)
    {
        if (!references.TryGetProperty(propertyName, out JsonElement groups))
        {
            return;
        }

        if (groups.ValueKind != JsonValueKind.Object)
        {
            missingOptionalAssetCount++;
            return;
        }

        foreach (JsonProperty group in groups.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(group.Name) || group.Value.ValueKind != JsonValueKind.Array)
            {
                missingOptionalAssetCount++;
                continue;
            }

            foreach (JsonElement entry in group.Value.EnumerateArray())
            {
                AddEntryFileReference(assets, rootPath, entry, kind, ref missingOptionalAssetCount);
            }
        }
    }

    private static void AddEntryFileReference(
        List<ModelRuntimeAsset> assets,
        string rootPath,
        JsonElement entry,
        ModelRuntimeAssetKind kind,
        ref int missingOptionalAssetCount)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("File", out JsonElement file)
            || file.ValueKind != JsonValueKind.String)
        {
            missingOptionalAssetCount++;
            return;
        }

        if (TryResolveOptionalReference(rootPath, file.GetString(), out string path))
        {
            assets.Add(new ModelRuntimeAsset(path, kind));
            return;
        }

        missingOptionalAssetCount++;
    }

    private static async Task<ImmutableDictionary<string, string>> ReadParameterNamesAsync(
        string rootPath,
        JsonElement references,
        long maxDisplayInfoBytes,
        CancellationToken cancellationToken)
    {
        if (!references.TryGetProperty("DisplayInfo", out JsonElement displayInfo)
            || displayInfo.ValueKind != JsonValueKind.String)
        {
            return ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
        }

        string? relativePath = displayInfo.GetString();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
        }

        string path;
        try
        {
            path = ResolveReference(rootPath, relativePath);
            if (new FileInfo(path).Length > maxDisplayInfoBytes)
            {
                return ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
            }

            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using JsonDocument document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("Parameters", out JsonElement parameters)
                || parameters.ValueKind != JsonValueKind.Array)
            {
                return ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
            }

            var names = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (JsonElement parameter in parameters.EnumerateArray())
            {
                if (parameter.TryGetProperty("Id", out JsonElement id)
                    && parameter.TryGetProperty("Name", out JsonElement name)
                    && id.ValueKind == JsonValueKind.String
                    && name.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(id.GetString())
                    && !string.IsNullOrWhiteSpace(name.GetString()))
                {
                    names[id.GetString()!] = name.GetString()!.Trim();
                }
            }

            return names.ToImmutable();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ModelDescriptorException)
        {
            return ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
        }
    }

    private static async Task<(string? Path, ModelFileLayoutStatus Status, string? Nickname)> FindThumbnailAsync(
        string rootPath,
        string modelName,
        CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(rootPath, "motara", "model.motara.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                await using var stream = new FileStream(
                    manifestPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using JsonDocument document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                string? nickname = document.RootElement.TryGetProperty("nickname", out JsonElement nicknameElement)
                    && nicknameElement.ValueKind == JsonValueKind.String
                    && nicknameElement.GetString() is { } candidate
                    && candidate.Length is > 0 and <= 128
                    && StringComparer.Ordinal.Equals(candidate, candidate.Trim())
                        ? candidate
                        : null;
                if (document.RootElement.TryGetProperty("fileLayout", out JsonElement layout)
                    && layout.ValueKind == JsonValueKind.Object
                    && layout.TryGetProperty("preview", out JsonElement preview))
                {
                    if (preview.ValueKind == JsonValueKind.Null)
                    {
                        return (null, ModelFileLayoutStatus.Canonical, nickname);
                    }

                    if (preview.ValueKind == JsonValueKind.String
                        && TryResolveCanonicalPreview(rootPath, preview.GetString(), out string canonical))
                    {
                        return File.Exists(canonical)
                            ? (canonical, ModelFileLayoutStatus.Canonical, nickname)
                            : (FindLegacyThumbnail(rootPath, modelName), ModelFileLayoutStatus.Stale, nickname);
                    }

                    return (FindLegacyThumbnail(rootPath, modelName), ModelFileLayoutStatus.Stale, nickname);
                }

                return (FindLegacyThumbnail(rootPath, modelName), ModelFileLayoutStatus.Unknown, nickname);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ModelDescriptorException)
            {
                return (FindLegacyThumbnail(rootPath, modelName), ModelFileLayoutStatus.Stale, null);
            }
        }

        return (FindLegacyThumbnail(rootPath, modelName), ModelFileLayoutStatus.Unknown, null);
    }

    private static bool TryResolveCanonicalPreview(
        string rootPath,
        string? relativePath,
        out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.Contains('\\', StringComparison.Ordinal)
            || !StringComparer.Ordinal.Equals(relativePath, "motara/assets/preview.png"))
        {
            return false;
        }

        path = ResolveReference(rootPath, relativePath);
        return true;
    }

    private static string? FindLegacyThumbnail(string rootPath, string modelName)
    {
        string[] extensions = [".png", ".webp", ".jpg", ".jpeg", ".bmp"];
        return Directory.EnumerateFiles(rootPath)
            .Select(path => new
            {
                Path = path,
                NameRank = string.Equals(
                    Path.GetFileNameWithoutExtension(path),
                    modelName,
                    StringComparison.OrdinalIgnoreCase) ? 0
                    : string.Equals(
                        Path.GetFileNameWithoutExtension(path),
                        "icon",
                        StringComparison.OrdinalIgnoreCase) ? 1
                    : 2,
                Rank = Array.FindIndex(extensions, extension => string.Equals(
                    Path.GetExtension(path),
                    extension,
                    StringComparison.OrdinalIgnoreCase)),
            })
            .Where(static candidate => candidate.NameRank < 2 && candidate.Rank >= 0)
            .OrderBy(static candidate => candidate.NameRank)
            .ThenBy(static candidate => candidate.Rank)
            .Select(static candidate => Path.GetFullPath(candidate.Path))
            .FirstOrDefault();
    }

    private static async Task<Moc3FormatVersion> ReadFormatVersionAsync(
        string nativeModelPath,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[5];
        await using FileStream stream = new(
            nativeModelPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        int read = await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        if (read < header.Length
            || header[0] != (byte)'M'
            || header[1] != (byte)'O'
            || header[2] != (byte)'C'
            || header[3] != (byte)'3')
        {
            return Moc3FormatVersion.Unknown;
        }

        return Enum.IsDefined(typeof(Moc3FormatVersion), (int)header[4])
            ? (Moc3FormatVersion)header[4]
            : Moc3FormatVersion.Unknown;
    }

    private static string ResolveReference(string rootPath, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathFullyQualified(relativePath)
            || relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new ModelDescriptorException(ModelErrorCode.PathEscape);
        }

        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        string rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(rootPrefix, comparison))
        {
            throw new ModelDescriptorException(ModelErrorCode.PathEscape);
        }

        if (!File.Exists(candidate))
        {
            throw new ModelDescriptorException(ModelErrorCode.MissingReference);
        }

        EnsureNoReparsePoint(normalizedRoot, candidate);
        return candidate;
    }

    private static bool TryResolveOptionalReference(
        string rootPath,
        string? relativePath,
        out string path)
    {
        try
        {
            path = ResolveReference(rootPath, relativePath);
            return true;
        }
        catch (ModelDescriptorException exception) when (exception.Code is ModelErrorCode.MissingReference
            or ModelErrorCode.PathEscape)
        {
            path = string.Empty;
            return false;
        }
    }

    private static void EnsureNoReparsePoint(string rootPath, string filePath)
    {
        string relativePath = Path.GetRelativePath(rootPath, filePath);
        string currentPath = rootPath;
        foreach (string segment in relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ModelDescriptorException(ModelErrorCode.PathEscape);
            }
        }
    }
}
