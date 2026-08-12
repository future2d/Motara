using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Identity;
using Motara.Collaboration.Models;

namespace Motara.App.Collaboration;

/// <summary>Moves received package notifications onto runtime-only member sources.</summary>
internal sealed class RemoteModelPublicationPresenter : IAsyncDisposable
{
    private readonly ModelPublicationReceiver receiver;
    private readonly RemoteMemberModelSourceRegistry sources;
    private readonly CancellationTokenSource shutdown = new();
    private readonly ILogger<RemoteModelPublicationPresenter> logger;
    private int disposed;

    internal RemoteModelPublicationPresenter(
        ModelPublicationReceiver receiver,
        RemoteMemberModelSourceRegistry sources,
        ILogger<RemoteModelPublicationPresenter>? logger = null)
    {
        this.receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
        this.logger = logger ?? NullLogger<RemoteModelPublicationPresenter>.Instance;
        receiver.PackageChanged += OnPackageChanged;
    }

    private void OnPackageChanged(DeviceId member, RemoteModelPackage? package)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        _ = Task.Run(() => ApplyAsync(member, package, shutdown.Token));
    }

    private async Task ApplyAsync(
        DeviceId member,
        RemoteModelPackage? package,
        CancellationToken cancellationToken)
    {
        try
        {
            if (package is null)
            {
                await sources.ReleaseMemberAsync(member).ConfigureAwait(false);
                RemoteModelPublicationPresenterLog.Released(logger);
                return;
            }

            await sources.ApplyReadyPackageAsync(member, package, cancellationToken)
                .ConfigureAwait(false);
            RemoteModelPublicationPresenterLog.Applied(logger);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RemoteModelPublicationPresenterLog.Failed(logger, exception.GetType().Name);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            receiver.PackageChanged -= OnPackageChanged;
            shutdown.Cancel();
            shutdown.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}

internal static partial class RemoteModelPublicationPresenterLog
{
    [LoggerMessage(8157, LogLevel.Information, "Remote member model package applied to runtime source")]
    internal static partial void Applied(ILogger logger);

    [LoggerMessage(8158, LogLevel.Information, "Remote member model source released after withdrawal")]
    internal static partial void Released(ILogger logger);

    [LoggerMessage(8159, LogLevel.Warning,
        "Remote member model package could not be applied; error={ErrorType}")]
    internal static partial void Failed(ILogger logger, string errorType);
}
