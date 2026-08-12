using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Motara.App.Models;
using Motara.Core.Formulas;

namespace Motara.App.Tracking;

internal static class SourceMappingReferenceUpdater
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    internal static async Task<SourceMappingMutationTransaction> PrepareRenameAsync(
        string oldId,
        string newId,
        string sourceMappingsRoot,
        string modelsRoot,
        string scenesRoot,
        CancellationToken cancellationToken,
        Func<string, int, Exception?>? replaceFailureInjector = null,
        ILogger? logger = null)
        => await PrepareUpdateAsync(
            [(oldId, newId)],
            null,
            null,
            sourceMappingsRoot,
            modelsRoot,
            scenesRoot,
            cancellationToken,
            replaceFailureInjector,
            logger).ConfigureAwait(false);

    internal static async Task<SourceMappingMutationTransaction> PrepareUpdateAsync(
        IEnumerable<(string OldId, string NewId)> renames,
        SourceMappingProfileDocument? finalProfileDocument,
        string? profilePath,
        string sourceMappingsRoot,
        string modelsRoot,
        string scenesRoot,
        CancellationToken cancellationToken,
        Func<string, int, Exception?>? replaceFailureInjector = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(renames);
        ImmutableArray<(string OldId, string NewId)> renameList = renames.ToImmutableArray();
        foreach ((string oldId, string newId) in renameList)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(oldId);
            ArgumentException.ThrowIfNullOrWhiteSpace(newId);
        }

        finalProfileDocument?.Validate();
        string? fullProfilePath = profilePath is null ? null : Path.GetFullPath(profilePath);
        string fullSourceRoot = Path.GetFullPath(sourceMappingsRoot);
        string fullModelsRoot = Path.GetFullPath(modelsRoot);
        string fullScenesRoot = Path.GetFullPath(scenesRoot);
        var mutations = new List<(string Path, byte[] Content)>();

        var mappingPaths = new HashSet<string>(PathComparer);
        foreach (string root in new[] { fullSourceRoot, fullModelsRoot, fullScenesRoot })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(root, "*.mapping.motara.json", SearchOption.AllDirectories))
            {
                mappingPaths.Add(Path.GetFullPath(path));
            }
        }

        foreach (string path in mappingPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceMappingProfileDocument document = await ReadAsync<SourceMappingProfileDocument>(
                path,
                cancellationToken).ConfigureAwait(false);
            SourceMappingProfileDocument? replacement = fullProfilePath is not null
                && PathEquals(path, fullProfilePath)
                    ? finalProfileDocument
                    : null;
            ImmutableArray<SourceMappingOutputDocument> outputs = replacement?.Outputs
                ?? document.Outputs.Select(output => RenameOutput(output, renameList)).ToImmutableArray();
            if (!outputs.SequenceEqual(document.Outputs))
            {
                SourceMappingProfileDocument updated = replacement ?? document with { Outputs = outputs };
                updated.Validate();
                mutations.Add((path, JsonSerializer.SerializeToUtf8Bytes(updated, JsonOptions)));
            }
        }

        if (finalProfileDocument is not null
            && fullProfilePath is not null
            && !mutations.Any(mutation => PathEquals(mutation.Path, fullProfilePath)))
        {
            mutations.Add((
                fullProfilePath,
                JsonSerializer.SerializeToUtf8Bytes(finalProfileDocument, JsonOptions)));
        }

        if (Directory.Exists(fullModelsRoot))
        {
            foreach (string path in Directory.EnumerateFiles(
                fullModelsRoot,
                "model.motara.json",
                SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                MotaraModelConfiguration configuration = await ReadAsync<MotaraModelConfiguration>(
                    path,
                    cancellationToken).ConfigureAwait(false);
                ImmutableArray<ModelParameterSettingConfiguration> settings = configuration.ParameterSettings
                    .Select(setting => setting with
                    {
                        GlobalParameterId = setting.GlobalParameterId is null
                            ? null
                            : RenameIdentifier(setting.GlobalParameterId, renameList),
                    })
                    .ToImmutableArray();
                if (!settings.SequenceEqual(configuration.ParameterSettings))
                {
                    MotaraModelConfiguration updated = configuration with { ParameterSettings = settings };
                    updated.Validate();
                    mutations.Add((path, JsonSerializer.SerializeToUtf8Bytes(updated, JsonOptions)));
                }
            }
        }

        string transactionsRoot = Path.Combine(
            FindCommonAncestor(FindCommonAncestor(fullSourceRoot, fullModelsRoot), fullScenesRoot),
            "Transactions");
        return new SourceMappingMutationTransaction(
            mutations,
            transactionsRoot,
            replaceFailureInjector,
            logger);
    }

    private static SourceMappingOutputDocument RenameOutput(
        SourceMappingOutputDocument output,
        ImmutableArray<(string OldId, string NewId)> renames)
    {
        string parameterId = RenameIdentifier(output.ParameterId, renames);
        string formula = output.Formula;
        foreach ((string oldId, string newId) in renames)
        {
            formula = SourceFormulaIdentifierRewriter.Rename(formula, oldId, newId);
        }

        return output with { ParameterId = parameterId, Formula = formula };
    }

    private static string RenameIdentifier(
        string identifier,
        ImmutableArray<(string OldId, string NewId)> renames)
    {
        foreach ((string oldId, string newId) in renames)
        {
            if (StringComparer.Ordinal.Equals(identifier, oldId))
            {
                return newId;
            }
        }

        return identifier;
    }

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new JsonException($"Configuration file is empty: {Path.GetFileName(path)}");
    }

    private static string FindCommonAncestor(string first, string second)
    {
        var firstDirectory = new DirectoryInfo(first);
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var secondAncestors = new HashSet<string>(comparer);
        for (DirectoryInfo? current = new DirectoryInfo(second); current is not null; current = current.Parent)
        {
            secondAncestors.Add(current.FullName);
        }

        for (DirectoryInfo? current = firstDirectory; current is not null; current = current.Parent)
        {
            if (secondAncestors.Contains(current.FullName))
            {
                return current.FullName;
            }
        }

        throw new ArgumentException("Mapping and model roots must share a filesystem root.");
    }

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
