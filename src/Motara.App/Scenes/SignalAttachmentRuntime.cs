using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Media;

namespace Motara.App.Scenes;

internal sealed class SignalAttachmentRuntime : IAsyncDisposable
{
    private readonly VideoSignalReceiverLifecycle lifecycle;
    private readonly ILogger logger;
    private int disposed;

    internal SignalAttachmentRuntime(
        VideoSignalReceiverLifecycle lifecycle,
        ILogger<SignalAttachmentRuntime>? logger = null)
    {
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.logger = logger ?? NullLogger<SignalAttachmentRuntime>.Instance;
    }

    internal VideoSignalConnectionSnapshot Snapshot => lifecycle.Snapshot;

    internal SignalFrame? ReadLatest() => lifecycle.ReadLatest();

    internal async Task<VideoSignalConnectionSnapshot> StartAsync(
        string sourceTypeId,
        VideoSignalSourceDescriptor source,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTypeId);
        VideoSignalProtocol expected = sourceTypeId switch
        {
            "attachment.spout2" => VideoSignalProtocol.Spout2,
            "attachment.ndi" => VideoSignalProtocol.Ndi,
            _ => throw new ArgumentException("Signal attachment source type is unsupported.", nameof(sourceTypeId)),
        };
        if (source.Protocol != expected)
        {
            throw new ArgumentException("Signal attachment source protocol does not match its source type.", nameof(source));
        }

        VideoSignalConnectionSnapshot snapshot = await lifecycle.StartAsync(source, cancellationToken).ConfigureAwait(false);
        SignalAttachmentRuntimeLog.Started(logger, sourceTypeId, source.Id, snapshot.State);
        return snapshot;
    }

    internal async ValueTask StopAsync(CancellationToken cancellationToken) =>
        await lifecycle.StopAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            await lifecycle.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal static partial class SignalAttachmentRuntimeLog
{
    [LoggerMessage(6840, LogLevel.Information, "Signal attachment started for {SourceTypeId}:{SourceId}; state={State}")]
    internal static partial void Started(ILogger logger, string sourceTypeId, string sourceId, VideoSignalState state);
}
