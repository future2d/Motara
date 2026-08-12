using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Motara.Media.Spout2;

public sealed class Spout2ProtocolAdapter : IVideoSignalProtocolAdapter, IDisposable
{
    private readonly ISpout2Interop interop;
    private readonly ILogger logger;
    private int disposed;

    public Spout2ProtocolAdapter(ILogger<Spout2ProtocolAdapter>? logger = null)
        : this(new Spout2NativeInterop(), logger)
    {
    }

    internal Spout2ProtocolAdapter(
        ISpout2Interop interop,
        ILogger<Spout2ProtocolAdapter>? logger = null)
    {
        this.interop = interop ?? new Spout2NativeInterop();
        this.logger = logger ?? NullLogger<Spout2ProtocolAdapter>.Instance;
    }

    public VideoSignalProtocol Protocol => VideoSignalProtocol.Spout2;

    public Task<IReadOnlyList<VideoSignalSourceDescriptor>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Spout2 is supported only on Windows.");
        }

        if (!interop.IsAvailable)
        {
            Spout2Log.RuntimeMissing(logger);
            throw new InvalidOperationException("Spout2 native runtime is unavailable.");
        }

        return Task.Run(interop.EnumerateSenders, cancellationToken);
    }

    public IVideoSignalReceiver CreateReceiver()
    {
        ThrowIfDisposed();
        return new Spout2Receiver(interop, logger);
    }

    public IVideoSignalSender CreateSender()
    {
        ThrowIfDisposed();
        return new Spout2Sender(interop, logger);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            interop.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
}

internal static partial class Spout2Log
{
    [LoggerMessage(6810, LogLevel.Warning, "Spout2 native runtime is unavailable")]
    internal static partial void RuntimeMissing(ILogger logger);

    [LoggerMessage(6811, LogLevel.Warning, "Spout2 receiver failed with {ErrorType}")]
    internal static partial void ReceiverFailed(ILogger logger, string errorType);

    [LoggerMessage(6812, LogLevel.Warning, "Spout2 sender failed with {ErrorType}")]
    internal static partial void SenderFailed(ILogger logger, string errorType);
}
