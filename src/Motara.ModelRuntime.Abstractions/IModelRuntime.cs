using System.Collections.Immutable;

namespace Motara.ModelRuntime.Abstractions;

public enum ModelAuxiliaryAssetKind
{
    Pose = 0,
    Motion = 1,
    Expression = 2,
}

public sealed record ModelAuxiliaryAsset
{
    public ModelAuxiliaryAsset(
        string assetId,
        ModelAuxiliaryAssetKind kind,
        string name,
        string? group = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (group is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(group);
        }

        AssetId = ModelAssetId.Normalize(assetId);
        Kind = kind;
        Name = name;
        Group = group;
    }

    public string AssetId { get; }

    public ModelAuxiliaryAssetKind Kind { get; }

    public string Name { get; }

    public string? Group { get; }
}

public sealed record ModelLoadRequest
{
    public ModelLoadRequest(
        IModelAssetSource assets,
        string descriptorAssetId,
        string nativeModelAssetId,
        ImmutableArray<string> textureAssetIds)
    {
        Assets = assets ?? throw new ArgumentNullException(nameof(assets));
        DescriptorAssetId = ModelAssetId.Normalize(descriptorAssetId);
        NativeModelAssetId = ModelAssetId.Normalize(nativeModelAssetId);
        if (textureAssetIds.IsDefault)
        {
            throw new ArgumentException("Texture asset IDs must be initialized.", nameof(textureAssetIds));
        }

        TextureAssetIds = textureAssetIds
            .Select(static assetId => ModelAssetId.Normalize(assetId))
            .ToImmutableArray();
    }

    public IModelAssetSource Assets { get; }

    public string DescriptorAssetId { get; }

    public string NativeModelAssetId { get; }

    public ImmutableArray<string> TextureAssetIds { get; }

    public ImmutableDictionary<string, string> ParameterNames { get; init; } =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    private ImmutableArray<ModelAuxiliaryAsset> auxiliaryAssets = [];

    public ImmutableArray<ModelAuxiliaryAsset> AuxiliaryAssets
    {
        get => auxiliaryAssets;
        init
        {
            if (value.IsDefault || value.Any(static asset => asset is null))
            {
                throw new ArgumentException("Auxiliary assets must be initialized.", nameof(value));
            }

            auxiliaryAssets = value;
        }
    }
}

public sealed record ModelLoadResult
{
    private ModelLoadResult(
        ModelCapabilities? capabilities,
        ModelRenderFrame? frame,
        ModelError? error)
    {
        Capabilities = capabilities;
        Frame = frame;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public ModelCapabilities? Capabilities { get; }

    public ModelRenderFrame? Frame { get; }

    public ModelError? Error { get; }

    public static ModelLoadResult Loaded(ModelCapabilities capabilities, ModelRenderFrame frame)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(frame);
        if (!Equals(capabilities.Canvas, frame.Canvas)
            || capabilities.DrawableCount != frame.Drawables.Length)
        {
            throw new ArgumentException("Capabilities and frame must describe the same model.", nameof(frame));
        }

        return new ModelLoadResult(capabilities, frame, null);
    }

    public static ModelLoadResult Failed(ModelError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ModelLoadResult(null, null, error);
    }
}

public interface IModelRuntime : IAsyncDisposable
{
    ModelRuntimeState State { get; }

    ModelCapabilities? Capabilities { get; }

    ModelRenderFrame? CurrentFrame { get; }

    Task<ModelLoadResult> LoadAsync(
        ModelLoadRequest request,
        CancellationToken cancellationToken);

    ValueTask<bool> ApplyParametersAsync(
        ModelParameterUpdate update,
        CancellationToken cancellationToken);
}

public interface IModelRuntimeFactory
{
    IModelRuntime Create();
}
