using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Sessions;

namespace Motara.Collaboration.Network;

/// <summary>Bridges consented session phases to the isolated network-host lifecycle.</summary>
public sealed class CollaborationNetworkSessionBridge : IAsyncDisposable
{
    private readonly CollaborationSessionCoordinator session;
    private readonly IEasyTierNetworkHost host;
    private readonly Func<CollaborationSessionSnapshot, EasyTierLaunchRequest> requestFactory;
    private readonly ILogger<CollaborationNetworkSessionBridge> logger;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Channel<CollaborationSessionSnapshot> snapshots = Channel.CreateUnbounded<CollaborationSessionSnapshot>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task worker;
    private int disposed;

    public CollaborationNetworkSessionBridge(
        CollaborationSessionCoordinator session,
        IEasyTierNetworkHost host,
        Func<CollaborationSessionSnapshot, EasyTierLaunchRequest> requestFactory,
        ILogger<CollaborationNetworkSessionBridge>? logger = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.requestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
        this.logger = logger ?? NullLogger<CollaborationNetworkSessionBridge>.Instance;
        session.SnapshotChanged += OnSnapshotChanged;
        worker = RunAsync(shutdown.Token);
    }

    private void OnSnapshotChanged(object? sender, CollaborationSessionSnapshot snapshot)
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            snapshots.Writer.TryWrite(snapshot);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        bool networkActive = false;
        try
        {
            await foreach (CollaborationSessionSnapshot snapshot in snapshots.Reader.ReadAllAsync(cancellationToken))
            {
                if (snapshot.Phase == CollaborationSessionPhase.Active && !networkActive)
                {
                    try
                    {
                        await host.StartAsync(requestFactory(snapshot), cancellationToken).ConfigureAwait(false);
                        networkActive = true;
                        CollaborationNetworkSessionEvents.Started(logger);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        CollaborationNetworkSessionEvents.Failed(logger, exception.GetType().Name);
                    }
                }
                else if (snapshot.Phase == CollaborationSessionPhase.Idle && networkActive)
                {
                    await host.StopAsync(cancellationToken).ConfigureAwait(false);
                    networkActive = false;
                    CollaborationNetworkSessionEvents.Stopped(logger);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (networkActive)
            {
                try
                {
                    await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    CollaborationNetworkSessionEvents.Failed(logger, exception.GetType().Name);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        session.SnapshotChanged -= OnSnapshotChanged;
        shutdown.Cancel();
        snapshots.Writer.TryComplete();
        await worker.ConfigureAwait(false);
        shutdown.Dispose();
    }
}

internal static partial class CollaborationNetworkSessionEvents
{
    [LoggerMessage(8165, LogLevel.Information, "Collaboration network host started after session consent")]
    internal static partial void Started(ILogger logger);

    [LoggerMessage(8166, LogLevel.Information, "Collaboration network host stopped with session")]
    internal static partial void Stopped(ILogger logger);

    [LoggerMessage(8167, LogLevel.Warning, "Collaboration network host lifecycle failed with {ErrorType}")]
    internal static partial void Failed(ILogger logger, string errorType);
}
