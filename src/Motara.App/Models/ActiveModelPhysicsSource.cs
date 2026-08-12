using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Motara.App.Physics;
using Motara.ModelLibrary;

namespace Motara.App.Models;

internal sealed record ActiveModelPhysicsSnapshot(
    ModelId ModelId,
    long Version,
    CubismPhysicsDefinition? Definition,
    ModelPhysicsConfiguration Configuration);

internal sealed class ActiveModelPhysicsSource
{
    private readonly ILogger<ActiveModelPhysicsSource> logger;
    private readonly object gate = new();
    private ActiveModelPhysicsSnapshot? current;
    private long version;

    internal ActiveModelPhysicsSource(ILogger<ActiveModelPhysicsSource>? logger = null) =>
        this.logger = logger ?? NullLogger<ActiveModelPhysicsSource>.Instance;

    internal event EventHandler? Changed;

    internal async Task ReloadAsync(ActiveModel active, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(active);
        CubismPhysicsDefinition? definition = null;
        ModelPhysicsConfiguration configuration = ModelPhysicsConfiguration.Disabled;
        try
        {
            ModelDescriptor? descriptor = active.Descriptor;
            ModelRuntimeAsset? asset = descriptor?.RuntimeAssets.FirstOrDefault(
                static candidate => candidate.Kind == ModelRuntimeAssetKind.Physics);
            if (descriptor is not null)
            {
                string modelName = ModelIdentity.FromDescriptorFilename(
                    Path.GetFileName(descriptor.DescriptorPath)).DisplayName;
                var store = new MotaraModelConfigurationStore(descriptor.RootPath, modelName);
                try
                {
                    MotaraModelConfiguration? modelConfiguration = await store.LoadAsync(cancellationToken)
                        .ConfigureAwait(false);
                    configuration = modelConfiguration?.Physics ?? ModelPhysicsConfiguration.Default;
                }
                catch (Exception exception) when (exception is JsonException or ArgumentException)
                {
                    ActiveModelPhysicsLog.ConfigurationReset(logger, exception.GetType().Name);
                    configuration = ModelPhysicsConfiguration.Default;
                }
                if (asset is not null)
                {
                    await using var stream = new FileStream(
                        asset.Path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete,
                        4096,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    definition = await CubismPhysicsDefinitionReader.ReadAsync(stream, cancellationToken)
                        .ConfigureAwait(false);
                    ActiveModelPhysicsLog.Loaded(logger, definition.Settings.Length);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            definition = null;
            configuration = ModelPhysicsConfiguration.Disabled;
            ActiveModelPhysicsLog.LoadFailed(logger, exception.GetType().Name);
        }

        lock (gate)
        {
            current = new ActiveModelPhysicsSnapshot(active.Id, ++version, definition, configuration);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal bool TryGet(ModelId modelId, out ActiveModelPhysicsSnapshot snapshot)
    {
        ActiveModelPhysicsSnapshot? candidate = Volatile.Read(ref current);
        if (candidate is not null && candidate.ModelId == modelId)
        {
            snapshot = candidate;
            return true;
        }

        snapshot = null!;
        return false;
    }
}

internal static partial class ActiveModelPhysicsLog
{
    [LoggerMessage(6530, LogLevel.Information, "Active model physics loaded with {SettingCount} settings")]
    internal static partial void Loaded(ILogger logger, int settingCount);

    [LoggerMessage(6531, LogLevel.Warning, "Active model physics disabled after load failure: {ErrorType}")]
    internal static partial void LoadFailed(ILogger logger, string errorType);

    [LoggerMessage(6532, LogLevel.Information, "Invalid development physics configuration reset to defaults: {ErrorType}")]
    internal static partial void ConfigurationReset(ILogger logger, string errorType);
}
