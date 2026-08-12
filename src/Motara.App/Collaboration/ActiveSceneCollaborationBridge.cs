using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.App.Scenes;
using Motara.Collaboration.Models;
using Motara.Collaboration.Sessions;
using Motara.ModelLibrary;

namespace Motara.App.Collaboration;

internal sealed class ActiveSceneCollaborationBridge : IAsyncDisposable
{
    private readonly MainModelAssignmentCoordinator assignments;
    private readonly CollaborationSessionCoordinator session;
    private readonly Func<ModelId, CancellationToken, Task<ModelInstanceId?>> resolveModelInstance;
    private readonly ILogger<ActiveSceneCollaborationBridge> logger;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Channel<ModelId?> publications = Channel.CreateUnbounded<ModelId?>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task publicationWorker;
    private int disposed;

    internal ActiveSceneCollaborationBridge(
        MainModelAssignmentCoordinator assignments,
        CollaborationSessionCoordinator session,
        Func<ModelId, CancellationToken, Task<ModelInstanceId?>> resolveModelInstance,
        ILogger<ActiveSceneCollaborationBridge>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(resolveModelInstance);
        this.assignments = assignments;
        this.session = session;
        this.resolveModelInstance = resolveModelInstance;
        this.logger = logger ?? NullLogger<ActiveSceneCollaborationBridge>.Instance;
        assignments.StateChanged += OnAssignmentStateChanged;
        publicationWorker = Task.Run(PublishAsync);
        QueuePublication(new MainModelAssignmentStateChangedEventArgs(
            assignments.CurrentWorkspace,
            assignments.PresentedSceneId,
            assignments.PendingModelId,
            assignments.IsRuntimeReady));
    }

    private void OnAssignmentStateChanged(
        object? sender,
        MainModelAssignmentStateChangedEventArgs args) => QueuePublication(args);

    private void QueuePublication(MainModelAssignmentStateChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        ModelId? modelId;
        if (args.PresentedScene is null)
        {
            modelId = null;
        }
        else if (!args.IsRuntimeReady)
        {
            return;
        }
        else
        {
            modelId = args.PresentedScene.MainModel is { ModelAssetId: string assetId }
                ? ModelId.Create(assetId)
                : null;
        }
        if (!publications.Writer.TryWrite(modelId))
        {
            ActiveSceneCollaborationBridgeLog.PublicationDropped(logger);
        }
    }

    private async Task PublishAsync()
    {
        try
        {
            await foreach (ModelId? desired in publications.Reader.ReadAllAsync(shutdown.Token)
                .ConfigureAwait(false))
            {
                ModelId? latest = desired;
                while (publications.Reader.TryRead(out ModelId? queued))
                {
                    latest = queued;
                }

                if (session.Snapshot.Phase != CollaborationSessionPhase.Active)
                {
                    ActiveSceneCollaborationBridgeLog.PublicationSkipped(logger);
                    continue;
                }

                try
                {
                    ModelInstanceId? instance = latest is ModelId modelId
                        ? await resolveModelInstance(modelId, shutdown.Token).ConfigureAwait(false)
                        : null;
                    session.SetLocalModel(instance);
                    ActiveSceneCollaborationBridgeLog.PublicationApplied(logger, instance.HasValue);
                }
                catch (InvalidOperationException)
                {
                    ActiveSceneCollaborationBridgeLog.PublicationSkipped(logger);
                }
                catch (Exception exception) when (!shutdown.IsCancellationRequested)
                {
                    ActiveSceneCollaborationBridgeLog.PublicationResolutionFailed(
                        logger,
                        exception.GetType().Name);
                    session.SetLocalModel(null);
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Expected on application shutdown.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        assignments.StateChanged -= OnAssignmentStateChanged;
        publications.Writer.TryComplete();
        shutdown.Cancel();
        await publicationWorker.ConfigureAwait(false);
        shutdown.Dispose();
    }
}

internal static partial class ActiveSceneCollaborationBridgeLog
{
    [LoggerMessage(8150, LogLevel.Debug,
        "Collaboration main-model publication applied; modelPresent={ModelPresent}")]
    internal static partial void PublicationApplied(ILogger logger, bool modelPresent);

    [LoggerMessage(8151, LogLevel.Debug,
        "Collaboration main-model publication skipped because the session is inactive")]
    internal static partial void PublicationSkipped(ILogger logger);

    [LoggerMessage(8152, LogLevel.Warning,
        "Collaboration main-model publication could not be queued")]
    internal static partial void PublicationDropped(ILogger logger);

    [LoggerMessage(8153, LogLevel.Warning,
        "Collaboration main-model publication identity resolution failed; error={ErrorType}")]
    internal static partial void PublicationResolutionFailed(ILogger logger, string errorType);
}
