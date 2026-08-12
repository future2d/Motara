using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Motara.Media.Ndi;

public sealed class NdiProtocolAdapter : IVideoSignalProtocolAdapter, IDisposable
{
    private readonly INdiInterop interop;
    private readonly ILogger logger;
    private int disposed;

    public NdiProtocolAdapter(ILogger<NdiProtocolAdapter>? logger = null)
        : this(new NdiNativeInterop(), logger)
    {
    }

    internal NdiProtocolAdapter(INdiInterop interop, ILogger<NdiProtocolAdapter>? logger = null)
    {
        this.interop = interop ?? throw new ArgumentNullException(nameof(interop));
        this.logger = logger ?? NullLogger<NdiProtocolAdapter>.Instance;
    }

    public VideoSignalProtocol Protocol => VideoSignalProtocol.Ndi;

    public Task<IReadOnlyList<VideoSignalSourceDescriptor>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NdiRuntimeProbeResult probe = NdiRuntimeProbe.Probe();
        if (!probe.IsAvailable || !interop.IsAvailable)
        {
            NdiLog.DependencyMissing(logger, probe.ErrorType ?? "RuntimeMissing");
            throw new InvalidOperationException(probe.ErrorType ?? "RuntimeMissing");
        }

        return Task.Run(interop.EnumerateSources, cancellationToken);
    }

    public IVideoSignalReceiver CreateReceiver()
    {
        ThrowIfDisposed();
        return new NdiReceiver(interop, logger);
    }

    public IVideoSignalSender CreateSender()
    {
        ThrowIfDisposed();
        return new NdiSender(interop, logger);
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

internal static partial class NdiLog
{
    [LoggerMessage(6820, LogLevel.Warning, "NDI runtime dependency is missing: {ErrorType}")]
    internal static partial void DependencyMissing(ILogger logger, string errorType);

    [LoggerMessage(6821, LogLevel.Warning, "NDI receiver failed with {ErrorType}")]
    internal static partial void ReceiverFailed(ILogger logger, string errorType);

    [LoggerMessage(6822, LogLevel.Warning, "NDI sender failed with {ErrorType}")]
    internal static partial void SenderFailed(ILogger logger, string errorType);
}
