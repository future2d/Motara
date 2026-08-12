using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Identity;
using Motara.Collaboration.Models;
using Motara.App.Models;
using Motara.ModelRuntime.Abstractions;

namespace Motara.App.Collaboration;

internal interface IRemoteRenderableModelRuntime : IRemoteMemberModelRuntime
{
    IModelRuntime ModelRuntime { get; }

    IModelFrameRenderer Renderer { get; }
}

/// <summary>
/// Loads a received package directly from its memory-backed asset source.
/// It never adds the package to the local model catalog or filesystem.
/// </summary>
internal sealed class RemoteMemberModelRuntimeFactory
{
    private readonly IModelRuntimeFactory runtimeFactory;
    private readonly IModelFrameRendererFactory rendererFactory;
    private readonly ILogger<RemoteMemberModelRuntimeFactory> logger;

    internal RemoteMemberModelRuntimeFactory(
        IModelRuntimeFactory runtimeFactory,
        IModelFrameRendererFactory rendererFactory,
        ILogger<RemoteMemberModelRuntimeFactory>? logger = null)
    {
        this.runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        this.rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        this.logger = logger ?? NullLogger<RemoteMemberModelRuntimeFactory>.Instance;
    }

    internal async Task<IRemoteMemberModelRuntime> CreateAsync(
        DeviceId member,
        IRemoteModelPackage package,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package is not IRemoteModelPackageSource source)
        {
            throw new InvalidOperationException("The received package does not expose an in-memory model manifest.");
        }

        ModelPackageManifest manifest = source.Manifest;
        ModelPackageFile descriptor = SingleFile(manifest, ModelPackageAssetKind.Descriptor);
        ModelPackageFile native = SingleFile(manifest, ModelPackageAssetKind.NativeModel);
        ImmutableArray<string> textures = manifest.Files
            .Where(static file => file.Kind == ModelPackageAssetKind.Texture)
            .Select(static file => file.AssetId)
            .ToImmutableArray();
        ImmutableArray<ModelAuxiliaryAsset> auxiliaryAssets = manifest.Files
            .Where(static file => file.Kind is ModelPackageAssetKind.Pose
                or ModelPackageAssetKind.Motion
                or ModelPackageAssetKind.Expression)
            .Select(ToAuxiliaryAsset)
            .ToImmutableArray();
        IModelRuntime runtime = runtimeFactory.Create();
        IModelFrameRenderer? renderer = null;
        RemoteMemberAnimationDriver? animationDriver = null;
        try
        {
            ModelLoadResult result = await runtime.LoadAsync(
                new ModelLoadRequest(source.Assets, descriptor.AssetId, native.AssetId, textures)
                {
                    AuxiliaryAssets = auxiliaryAssets,
                },
                cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"The received model could not be loaded: {result.Error?.Code}.");
            }

            renderer = await rendererFactory.CreateAsync(
                source.Assets,
                textures,
                cancellationToken).ConfigureAwait(false);
            animationDriver = await RemoteMemberAnimationDriver.CreateAsync(
                runtime,
                source.Assets,
                auxiliaryAssets,
                cancellationToken,
                logger).ConfigureAwait(false);
            RemoteMemberModelRuntime created = new(runtime, renderer, animationDriver);
            renderer = null;
            animationDriver = null;
            RemoteModelRuntimeEvents.Loaded(logger, member);
            return created;
        }
        catch
        {
            if (animationDriver is not null)
            {
                await animationDriver.DisposeAsync().ConfigureAwait(false);
            }
            if (renderer is not null)
            {
                await renderer.DisposeAsync().ConfigureAwait(false);
            }
            await runtime.DisposeAsync().ConfigureAwait(false);
            RemoteModelRuntimeEvents.Failed(logger, member);
            throw;
        }
    }

    private static ModelPackageFile SingleFile(ModelPackageManifest manifest, ModelPackageAssetKind kind) =>
        manifest.Files.Count(file => file.Kind == kind) switch
        {
            1 => manifest.Files.Single(file => file.Kind == kind),
            0 => throw new InvalidOperationException($"The received model has no {kind} asset."),
            _ => throw new InvalidOperationException($"The received model has multiple {kind} assets."),
        };

    private static ModelAuxiliaryAsset ToAuxiliaryAsset(ModelPackageFile file) => new(
        file.AssetId,
        file.Kind switch
        {
            ModelPackageAssetKind.Pose => ModelAuxiliaryAssetKind.Pose,
            ModelPackageAssetKind.Motion => ModelAuxiliaryAssetKind.Motion,
            ModelPackageAssetKind.Expression => ModelAuxiliaryAssetKind.Expression,
            _ => throw new ArgumentOutOfRangeException(nameof(file)),
        },
        file.Name ?? throw new InvalidOperationException(
            $"The received {file.Kind} asset has no name metadata."),
        file.Group);

    private sealed class RemoteMemberModelRuntime(
        IModelRuntime modelRuntime,
        IModelFrameRenderer renderer,
        RemoteMemberAnimationDriver? animationDriver) : IRemoteRenderableModelRuntime
    {
        public IModelRuntime ModelRuntime { get; } = modelRuntime;

        public IModelFrameRenderer Renderer { get; } = renderer;

        public async ValueTask DisposeAsync()
        {
            if (animationDriver is not null)
            {
                await animationDriver.DisposeAsync().ConfigureAwait(false);
            }
            await Renderer.DisposeAsync().ConfigureAwait(false);
            await ModelRuntime.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal static partial class RemoteModelRuntimeEvents
{
    [LoggerMessage(8158, LogLevel.Information, "Remote member model runtime loaded for {Member}")]
    internal static partial void Loaded(ILogger logger, DeviceId member);

    [LoggerMessage(8159, LogLevel.Warning, "Remote member model runtime load failed for {Member}")]
    internal static partial void Failed(ILogger logger, DeviceId member);
}
