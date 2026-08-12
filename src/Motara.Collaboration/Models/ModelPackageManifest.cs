using System.Collections.Immutable;

namespace Motara.Collaboration.Models;

public sealed record ModelPackageInput
{
    public ModelPackageInput(
        ModelInstanceId modelInstanceId,
        string displayName,
        ModelGeneration generation,
        ImmutableArray<ModelPackageAsset> assets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (Path.IsPathFullyQualified(displayName))
        {
            throw new ArgumentException(
                "Model package display metadata cannot contain an absolute path.",
                nameof(displayName));
        }

        if (assets.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A model package must declare at least one asset.", nameof(assets));
        }

        ModelInstanceId = modelInstanceId;
        DisplayName = displayName;
        Generation = generation;
        Assets = assets;
    }

    public ModelInstanceId ModelInstanceId { get; }

    public string DisplayName { get; }

    public ModelGeneration Generation { get; }

    public ImmutableArray<ModelPackageAsset> Assets { get; }
}

public sealed record ModelPackageManifest(
    ModelInstanceId ModelInstanceId,
    ModelContentId ModelContentId,
    PackageContentId PackageContentId,
    ModelGeneration Generation,
    string DisplayName,
    ImmutableArray<ModelPackageFile> Files)
{
    public const int SchemaVersion = 1;
}

public enum ModelPackageErrorCode
{
    DuplicateAssetId,
    FileCountLimitExceeded,
    FileSizeLimitExceeded,
    PackageSizeLimitExceeded,
    AssetLengthChanged,
    PackageIdMismatch,
    GenerationMismatch,
    AssetNotDeclared,
    ChunkOutOfRange,
    ChunkHashMismatch,
    ConflictingChunk,
    PackageIncomplete,
    ManifestHashMismatch,
    ReceiverUnavailable,
}

public sealed class ModelPackageException : Exception
{
    public ModelPackageException(ModelPackageErrorCode errorCode, string message)
        : base(message) => ErrorCode = errorCode;

    public ModelPackageErrorCode ErrorCode { get; }
}
