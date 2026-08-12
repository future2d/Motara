using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Core.Sessions;
using Motara.Output.Abstractions;
using Motara.Output.CubismEditor;

namespace Motara.App.Models;

/// <summary>Publishes canonical session parameters to Cubism Editor independently of local model loading.</summary>
internal sealed class CubismEditorOutputController : IAsyncDisposable
{
    private readonly ISessionController sessionController;
    private readonly IOutputParameterPublisher publisher;
    private CubismEditorParameterMapping mapping;
    private readonly ILogger<CubismEditorOutputController> logger;
    private readonly Channel<SessionSnapshot> snapshots;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task watchTask;
    private readonly Task publishTask;
    private readonly object disposalGate = new();
    private Task? disposalTask;

    internal CubismEditorOutputController(
        ISessionController sessionController,
        IOutputParameterPublisher publisher,
        CubismEditorParameterMapping? mapping = null,
        ILogger<CubismEditorOutputController>? logger = null)
    {
        this.sessionController = sessionController ?? throw new ArgumentNullException(nameof(sessionController));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.mapping = mapping ?? CubismEditorParameterMapping.Default;
        this.logger = logger ?? NullLogger<CubismEditorOutputController>.Instance;
        snapshots = Channel.CreateBounded<SessionSnapshot>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        publisher.ActivityChanged += OnPublisherActivityChanged;
        watchTask = WatchSnapshotsAsync(cancellation.Token);
        publishTask = PublishAsync(cancellation.Token);
        QueueLatestSnapshot();
        CubismEditorOutputControllerLog.Started(this.logger, this.mapping.Bindings.Length);
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            disposalTask ??= DisposeCoreAsync();
            return new ValueTask(disposalTask);
        }
    }

    /// <summary>Replaces the independent output mapping without reloading a local model.</summary>
    internal void UpdateMapping(CubismEditorParameterMapping value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Volatile.Write(ref mapping, value);
        QueueLatestSnapshot();
        CubismEditorOutputControllerLog.MappingUpdated(logger, value.Bindings.Length);
    }

    private void OnPublisherActivityChanged(object? sender, EventArgs args) => QueueLatestSnapshot();

    private void QueueLatestSnapshot() => snapshots.Writer.TryWrite(sessionController.Current);

    private async Task WatchSnapshotsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (SessionSnapshot snapshot in sessionController.WatchSnapshotsAsync(cancellationToken).ConfigureAwait(false))
            {
                snapshots.Writer.TryWrite(snapshot);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CubismEditorOutputControllerLog.SessionWatchFaulted(logger, exception);
        }
    }

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (SessionSnapshot snapshot in snapshots.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!publisher.IsActive)
                {
                    continue;
                }

                OutputParameterFrame? frame = Volatile.Read(ref mapping).CreateFrame(snapshot);
                if (frame is not null)
                {
                    publisher.PublishFrame(frame);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CubismEditorOutputControllerLog.PublishFaulted(logger, exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        publisher.ActivityChanged -= OnPublisherActivityChanged;
        cancellation.Cancel();
        snapshots.Writer.TryComplete();
        try
        {
            await Task.WhenAll(watchTask, publishTask).ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
            CubismEditorOutputControllerLog.Stopped(logger);
        }
    }
}

internal static partial class CubismEditorOutputControllerLog
{
    [LoggerMessage(6720, LogLevel.Information, "Cubism Editor session output controller started with {BindingCount} bindings")]
    internal static partial void Started(ILogger logger, int bindingCount);

    [LoggerMessage(6721, LogLevel.Warning, "Cubism Editor session snapshot watch faulted")]
    internal static partial void SessionWatchFaulted(ILogger logger, Exception exception);

    [LoggerMessage(6722, LogLevel.Warning, "Cubism Editor session output publishing faulted")]
    internal static partial void PublishFaulted(ILogger logger, Exception exception);

    [LoggerMessage(6723, LogLevel.Information, "Cubism Editor session output controller stopped")]
    internal static partial void Stopped(ILogger logger);

    [LoggerMessage(6724, LogLevel.Information, "Cubism Editor session output mapping updated with {BindingCount} bindings")]
    internal static partial void MappingUpdated(ILogger logger, int bindingCount);
}
