using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.ExceptionServices;

namespace Motara.Collaboration.Network;

public enum EasyTierHostState { Stopped, Starting, Running, Failed }

public sealed record EasyTierHostSnapshot(EasyTierHostState State)
{
    public static EasyTierHostSnapshot Stopped { get; } = new(EasyTierHostState.Stopped);
}

public sealed record EasyTierLaunchRequest
{
    public EasyTierLaunchRequest(string networkName, string joinSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(networkName);
        ArgumentException.ThrowIfNullOrWhiteSpace(joinSecret);
        NetworkName = networkName;
        JoinSecret = joinSecret;
    }

    public string NetworkName { get; }
    public string JoinSecret { get; }
}

public interface IEasyTierProcess : IAsyncDisposable
{
    Task Completion { get; }

    ValueTask StopAsync(CancellationToken cancellationToken);
}

public interface IEasyTierNetworkHost
{
    Task StartAsync(EasyTierLaunchRequest request, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public sealed class EasyTierNetworkHost : IAsyncDisposable, IEasyTierNetworkHost
{
    private readonly Func<EasyTierLaunchRequest, CancellationToken, Task<IEasyTierProcess>> start;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ILogger<EasyTierNetworkHost> logger;
    private IEasyTierProcess? process;

    public EasyTierNetworkHost(
        Func<EasyTierLaunchRequest, CancellationToken, Task<IEasyTierProcess>> start,
        ILogger<EasyTierNetworkHost>? logger = null)
    {
        this.start = start ?? throw new ArgumentNullException(nameof(start));
        this.logger = logger ?? NullLogger<EasyTierNetworkHost>.Instance;
    }

    public EasyTierHostSnapshot Snapshot { get; private set; } = EasyTierHostSnapshot.Stopped;

    public event EventHandler<EasyTierHostSnapshot>? SnapshotChanged;

    public async Task StartAsync(EasyTierLaunchRequest request, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task<IEasyTierProcess>? launch = null;
        try
        {
            if (process is not null) return;
            PublishSnapshot(new(EasyTierHostState.Starting));
            launch = start(request, cancellationToken);
            process = await launch.WaitAsync(cancellationToken).ConfigureAwait(false);
            PublishSnapshot(new(EasyTierHostState.Running));
            _ = ObserveProcessExitAsync(process);
            EasyTierNetworkHostEvents.Started(logger);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PublishSnapshot(EasyTierHostSnapshot.Stopped);
            if (launch is not null)
            {
                _ = ReleaseLateProcessAsync(launch);
            }
            EasyTierNetworkHostEvents.StartCancelled(logger);
            throw;
        }
        catch
        {
            PublishSnapshot(new(EasyTierHostState.Failed));
            EasyTierNetworkHostEvents.StartFailed(logger);
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IEasyTierProcess? current = Interlocked.Exchange(ref process, null);
            Exception? failure = null;
            if (current is not null)
            {
                try
                {
                    await current.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure = exception;
                    EasyTierNetworkHostEvents.StopFailed(logger, exception.GetType().Name);
                }

                try
                {
                    await current.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (failure is null)
                {
                    failure = exception;
                    EasyTierNetworkHostEvents.StopFailed(logger, exception.GetType().Name);
                }
            }
            PublishSnapshot(EasyTierHostSnapshot.Stopped);
            EasyTierNetworkHostEvents.Stopped(logger);
            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        gate.Dispose();
    }

    private static async Task ReleaseLateProcessAsync(Task<IEasyTierProcess> launch)
    {
        try
        {
            IEasyTierProcess lateProcess = await launch.ConfigureAwait(false);
            await lateProcess.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await lateProcess.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // The cancelled launch has no active owner. Its failure is already observable by the launcher.
        }
    }

    private async Task ObserveProcessExitAsync(IEasyTierProcess expected)
    {
        try
        {
            await expected.Completion.ConfigureAwait(false);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(process, expected))
                {
                    return;
                }

                process = null;
                PublishSnapshot(new(EasyTierHostState.Failed));
                EasyTierNetworkHostEvents.Exited(logger);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // Shutdown completed before the operating-system process reported its final exit.
        }
        catch (Exception exception)
        {
            EasyTierNetworkHostEvents.ExitObservationFailed(logger, exception.GetType().Name);
        }
    }

    private void PublishSnapshot(EasyTierHostSnapshot value)
    {
        Snapshot = value;
        SnapshotChanged?.Invoke(this, value);
    }
}
