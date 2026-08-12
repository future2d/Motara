using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Animation;
using Motara.ModelLibrary;
using System.Text.Json;

namespace Motara.App.Models;

internal sealed record ActiveModelAnimationSnapshot(
    ModelId ModelId,
    long DefinitionVersion,
    long CommandVersion,
    CubismAnimationSet Definitions,
    ModelIdleMotionSelection IdleMotion,
    ModelLostTrackingIdleMotionSelection LostTrackingIdleMotion,
    ActiveModelAnimationCommand? Command);

internal enum ActiveModelAnimationCommandKind { Play, SetIdle, ClearIdle, ToggleExpression, ClearExpressions }
internal sealed record ActiveModelAnimationCommand(ActiveModelAnimationCommandKind Kind, string? AssetId);

internal sealed class ActiveModelAnimationSource
{
    private readonly ILogger<ActiveModelAnimationSource> logger;
    private readonly object gate = new();
    private ActiveModelAnimationSnapshot? current;
    private long version;

    internal ActiveModelAnimationSource(ILogger<ActiveModelAnimationSource>? logger = null) =>
        this.logger = logger ?? NullLogger<ActiveModelAnimationSource>.Instance;

    internal event EventHandler? Changed;

    internal async Task ReloadAsync(ActiveModel active, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(active);
        CubismAnimationSet definitions = Empty;
        ModelIdleMotionSelection idleMotion = ModelIdleMotionSelection.Automatic;
        ModelLostTrackingIdleMotionSelection lostTrackingIdleMotion =
            ModelLostTrackingIdleMotionSelection.UseRegularIdle;
        try
        {
            if (active.Descriptor is { } descriptor)
            {
                string modelName = ModelIdentity.FromDescriptorFilename(
                    Path.GetFileName(descriptor.DescriptorPath)).DisplayName;
                try
                {
                    MotaraModelConfiguration? configuration = await new MotaraModelConfigurationStore(
                        descriptor.RootPath,
                        modelName).LoadAsync(cancellationToken).ConfigureAwait(false);
                    if (configuration is not null)
                    {
                        idleMotion = configuration.IdleMotion;
                        lostTrackingIdleMotion = configuration.LostTrackingIdleMotion;
                    }
                }
                catch (Exception exception) when (exception is JsonException or ArgumentException)
                {
                    ActiveModelAnimationLog.ConfigurationReset(logger, exception.GetType().Name);
                }

                if (active.Runtime.Capabilities is { } capabilities)
                {
                    await using FileModelAssetSource assets = FileModelAssetSource.Create(descriptor);
                    definitions = await CubismAnimationParser.LoadAsync(
                        assets,
                        descriptor.AuxiliaryAssets,
                        capabilities,
                        cancellationToken,
                        logger).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ActiveModelAnimationLog.ReloadFailed(logger, exception, active.Id.Value);
        }

        ActiveModelAnimationSnapshot snapshot;
        lock (gate)
        {
            long nextVersion = ++version;
            snapshot = new ActiveModelAnimationSnapshot(
                active.Id,
                nextVersion,
                nextVersion,
                definitions,
                idleMotion,
                lostTrackingIdleMotion,
                null);
            Volatile.Write(ref current, snapshot);
        }

        ActiveModelAnimationLog.Reloaded(
            logger,
            active.Id.Value,
            definitions.Clips.Length,
            definitions.Expressions.Length,
            definitions.PoseGroups.Length,
            definitions.Diagnostics.Length,
            snapshot.DefinitionVersion);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal bool TryGet(ModelId modelId, out ActiveModelAnimationSnapshot snapshot)
    {
        ActiveModelAnimationSnapshot? candidate = Volatile.Read(ref current);
        if (candidate is not null && candidate.ModelId == modelId)
        {
            snapshot = candidate;
            return true;
        }

        snapshot = null!;
        return false;
    }

    internal bool TryPlay(string assetId) => Update(assetId, static (snapshot, id) =>
        snapshot.Definitions.Clips.Any(clip => StringComparer.Ordinal.Equals(clip.Asset.AssetId, id))
            ? snapshot with { Command = new(ActiveModelAnimationCommandKind.Play, id) }
            : null);

    internal bool TrySetIdle(string assetId) => Update(assetId, static (snapshot, id) =>
        snapshot.Definitions.Clips.Any(clip => StringComparer.Ordinal.Equals(clip.Asset.AssetId, id))
            ? snapshot with { Command = new(ActiveModelAnimationCommandKind.SetIdle, id) }
            : null);

    internal bool ClearIdle()
    {
        lock (gate)
        {
            if (current is null) return false;
            Volatile.Write(ref current, current with
            {
                CommandVersion = ++version,
                Command = new(ActiveModelAnimationCommandKind.ClearIdle, null),
            });
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal bool TryToggleExpression(string assetId) => Update(assetId, static (snapshot, id) =>
        snapshot.Definitions.Expressions.Any(expression => StringComparer.Ordinal.Equals(expression.Asset.AssetId, id))
            ? snapshot with { Command = new(ActiveModelAnimationCommandKind.ToggleExpression, id) }
            : null);

    internal bool ClearExpressions()
    {
        lock (gate)
        {
            if (current is null) return false;
            Volatile.Write(ref current, current with
            {
                CommandVersion = ++version,
                Command = new(ActiveModelAnimationCommandKind.ClearExpressions, null),
            });
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool Update(string assetId, Func<ActiveModelAnimationSnapshot, string, ActiveModelAnimationSnapshot?> update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        lock (gate)
        {
            if (current is null || update(current, assetId) is not { } changed) return false;
            Volatile.Write(ref current, changed with { CommandVersion = ++version });
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static CubismAnimationSet Empty { get; } = new([], [], [], []);
}

internal static partial class ActiveModelAnimationLog
{
    [LoggerMessage(6545, LogLevel.Information,
        "Active model animation reloaded for {ModelId}: {ClipCount} clips, {ExpressionCount} expressions, {PoseGroupCount} pose groups, {DiagnosticCount} diagnostics, version {Version}")]
    internal static partial void Reloaded(
        ILogger logger,
        string modelId,
        int clipCount,
        int expressionCount,
        int poseGroupCount,
        int diagnosticCount,
        long version);

    [LoggerMessage(6546, LogLevel.Warning,
        "Active model animation reload failed for {ModelId}; using an empty animation snapshot")]
    internal static partial void ReloadFailed(ILogger logger, Exception exception, string modelId);

    [LoggerMessage(6547, LogLevel.Information,
        "Invalid development animation configuration reset to defaults: {ErrorType}")]
    internal static partial void ConfigurationReset(ILogger logger, string errorType);
}
