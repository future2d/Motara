using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Motara.Media;

public interface IVideoSignalProtocolAdapter
{
    VideoSignalProtocol Protocol { get; }
    Task<IReadOnlyList<VideoSignalSourceDescriptor>> DiscoverAsync(CancellationToken cancellationToken);
    IVideoSignalReceiver CreateReceiver();
    IVideoSignalSender CreateSender();
}

public sealed record VideoSignalDiscoveryFailure(
    VideoSignalProtocol Protocol,
    string ErrorType);

public sealed record VideoSignalDiscoveryResult(
    IReadOnlyList<VideoSignalSourceDescriptor> Sources,
    IReadOnlyList<VideoSignalDiscoveryFailure> Failures);

public sealed class VideoSignalRegistry : IDisposable
{
    private readonly Dictionary<VideoSignalProtocol, IVideoSignalProtocolAdapter> adapters;
    private readonly ILogger logger;

    public VideoSignalRegistry(
        IEnumerable<IVideoSignalProtocolAdapter> adapters,
        ILogger<VideoSignalRegistry>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        this.adapters = adapters.ToDictionary(static adapter => adapter.Protocol);
        this.logger = logger ?? NullLogger<VideoSignalRegistry>.Instance;
    }

    public async Task<VideoSignalDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        var sources = new List<VideoSignalSourceDescriptor>();
        var failures = new List<VideoSignalDiscoveryFailure>();
        foreach ((VideoSignalProtocol protocol, IVideoSignalProtocolAdapter adapter) in adapters)
        {
            VideoSignalLog.SourceDiscoveryStarted(logger, protocol);
            try
            {
                IReadOnlyList<VideoSignalSourceDescriptor> discovered = await adapter.DiscoverAsync(cancellationToken)
                    .ConfigureAwait(false);
                foreach (VideoSignalSourceDescriptor source in discovered)
                {
                    if (source.Protocol == protocol)
                    {
                        sources.Add(source);
                    }
                }

                VideoSignalLog.SourceDiscoveryCompleted(logger, protocol, discovered.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add(new VideoSignalDiscoveryFailure(protocol, exception.GetType().Name));
                VideoSignalLog.SourceDiscoveryFailed(logger, protocol, exception.GetType().Name);
            }
        }

        return new VideoSignalDiscoveryResult(sources, failures);
    }

    public IVideoSignalProtocolAdapter GetRequiredAdapter(VideoSignalProtocol protocol) =>
        adapters.TryGetValue(protocol, out IVideoSignalProtocolAdapter? adapter)
            ? adapter
            : throw new InvalidOperationException($"No video signal adapter is registered for {protocol}.");

    public void Dispose()
    {
        foreach (IVideoSignalProtocolAdapter adapter in adapters.Values)
        {
            if (adapter is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
