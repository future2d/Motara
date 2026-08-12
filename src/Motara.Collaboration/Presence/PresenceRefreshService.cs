using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Motara.Collaboration.Presence;

public enum PresenceRefreshState
{
    Stopped,
    Starting,
    Online,
    Unavailable,
}

public sealed record PresenceRefreshSnapshot(PresenceRefreshState State)
{
    public static PresenceRefreshSnapshot Stopped { get; } = new(PresenceRefreshState.Stopped);
}

/// <summary>
/// Keeps an encrypted presence record alive without exposing its contents to UI or logs.
/// </summary>
public sealed class PresenceRefreshService : IAsyncDisposable
{
    private const int MaximumPublishAttempts = 3;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly IPresenceClient client;
    private readonly Func<CancellationToken, Task<EncryptedPresenceRecord>> recordFactory;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly ILogger<PresenceRefreshService> logger;
    private readonly object gate = new();
    private CancellationTokenSource? runnerCancellation;
    private Task? runner;

    public PresenceRefreshService(
        IPresenceClient client,
        Func<CancellationToken, Task<EncryptedPresenceRecord>> recordFactory,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger<PresenceRefreshService>? logger = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.recordFactory = recordFactory ?? throw new ArgumentNullException(nameof(recordFactory));
        this.delay = delay ?? (static (duration, token) => Task.Delay(duration, token));
        this.logger = logger ?? NullLogger<PresenceRefreshService>.Instance;
    }

    public PresenceRefreshSnapshot Snapshot { get; private set; } = PresenceRefreshSnapshot.Stopped;

    public event EventHandler<PresenceRefreshSnapshot>? SnapshotChanged;

    public void Start()
    {
        lock (gate)
        {
            if (runner is not null)
            {
                return;
            }

            runnerCancellation = new CancellationTokenSource();
            PublishSnapshot(new(PresenceRefreshState.Starting));
            runner = RunAsync(runnerCancellation);
        }
    }

    public async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaximumPublishAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                EncryptedPresenceRecord record = await recordFactory(cancellationToken).ConfigureAwait(false);
                await client.PublishAsync(record, cancellationToken).ConfigureAwait(false);
                PublishSnapshot(new(PresenceRefreshState.Online));
                PresenceRefreshEvents.Published(logger);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (attempt < MaximumPublishAttempts)
            {
                PresenceRefreshEvents.Retrying(logger, attempt, exception.GetType().Name);
                await delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                PublishSnapshot(new(PresenceRefreshState.Unavailable));
                PresenceRefreshEvents.Unavailable(logger, exception.GetType().Name);
                throw;
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? active;
        lock (gate)
        {
            runnerCancellation?.Cancel();
            active = runner;
        }

        if (active is null)
        {
            return;
        }

        try
        {
            await active.WaitAsync(ShutdownTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            PresenceRefreshEvents.ShutdownTimedOut(logger);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationTokenSource cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    await RefreshOnceAsync(cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    // RefreshOnceAsync logged the bounded retry exhaustion and published an unavailable state.
                }

                await delay(RefreshInterval, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(runnerCancellation, cancellation))
                {
                    runner = null;
                    runnerCancellation = null;
                    cancellation.Dispose();
                    PublishSnapshot(PresenceRefreshSnapshot.Stopped);
                    PresenceRefreshEvents.Stopped(logger);
                }
            }
        }
    }

    private void PublishSnapshot(PresenceRefreshSnapshot value)
    {
        Snapshot = value;
        SnapshotChanged?.Invoke(this, value);
    }
}
