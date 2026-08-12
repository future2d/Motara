using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Tracking.Abstractions;

namespace Motara.Core.Tracking;

/// <summary>Shares compatible provider resources until their final channel lease is released.</summary>
public sealed class TrackingResourceCoordinator : ITrackingResourceCoordinator
{
    private readonly ConcurrentDictionary<ResourceIdentity, ResourceEntry> entries = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly ILogger<TrackingResourceCoordinator> logger;
    private int disposed;

    public TrackingResourceCoordinator(ILogger<TrackingResourceCoordinator>? logger = null)
    {
        this.logger = logger ?? NullLogger<TrackingResourceCoordinator>.Instance;
    }

    public async ValueTask<ITrackingResourceLease<TResource>> AcquireAsync<TResource>(
        TrackingChannel channel,
        TrackingResourceRequest request,
        Func<CancellationToken, ValueTask<TResource>> createAsync,
        Func<TResource, ValueTask> disposeAsync,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(createAsync);
        ArgumentNullException.ThrowIfNull(disposeAsync);
        cancellationToken.ThrowIfCancellationRequested();

        var identity = new ResourceIdentity(request.ProviderId, request.ResourceKind);
        ResourceEntry entry;
        while (true)
        {
            entry = entries.GetOrAdd(
                identity,
                _ => ResourceEntry.Create(
                    request.CompatibilityKey,
                    typeof(TResource),
                    async token => (object?)await createAsync(token).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("A shared tracking resource cannot be null."),
                    resource => disposeAsync((TResource)resource),
                    logger,
                    lifetimeCancellation.Token));

            ResourceAcquireResult acquireResult = entry.TryAcquire(
                request.CompatibilityKey,
                typeof(TResource));
            if (acquireResult == ResourceAcquireResult.Conflict)
            {
                TrackingResourceCoordinatorLog.Conflict(logger);
                throw new TrackingResourceConflictException();
            }

            if (acquireResult == ResourceAcquireResult.Acquired)
            {
                break;
            }

            await entry.RemovedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        }

        if (Volatile.Read(ref disposed) != 0)
        {
            await ReleaseAsync(identity, entry).ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(TrackingResourceCoordinator));
        }

        TrackingResourceCoordinatorLog.LeaseCountChanged(logger, entry.LeaseCount);
        try
        {
            object resource = await entry.ResourceTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ResourceLease<TResource>(
                (TResource)resource,
                () => ReleaseAsync(identity, entry));
        }
        catch
        {
            await ReleaseAsync(identity, entry).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetimeCancellation.Cancel();
        ResourceEntry[] snapshot = entries.Values.ToArray();
        entries.Clear();
        foreach (ResourceEntry entry in snapshot)
        {
            await entry.ForceDisposeAsync().ConfigureAwait(false);
            entry.MarkRemoved();
            TrackingResourceCoordinatorLog.Released(logger);
        }

        lifetimeCancellation.Dispose();
    }

    private async ValueTask ReleaseAsync(ResourceIdentity identity, ResourceEntry entry)
    {
        int remaining = entry.Release();
        if (remaining < 0)
        {
            return;
        }

        TrackingResourceCoordinatorLog.LeaseCountChanged(logger, remaining);
        if (remaining != 0)
        {
            return;
        }

        await entry.DisposeResourceAsync().ConfigureAwait(false);
        entries.TryRemove(new KeyValuePair<ResourceIdentity, ResourceEntry>(identity, entry));
        entry.MarkRemoved();
        TrackingResourceCoordinatorLog.Released(logger);
    }

    private readonly record struct ResourceIdentity(string ProviderId, string ResourceKind);

    private enum ResourceAcquireResult
    {
        Acquired,
        Closing,
        Conflict,
    }

    private sealed class ResourceEntry
    {
        private readonly Func<object, ValueTask> disposeAsync;
        private readonly CancellationTokenSource creationCancellation;
        private readonly object stateGate = new();
        private readonly TaskCompletionSource removed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int leaseCount;
        private bool finalReleaseStarted;
        private Task? resourceDisposalTask;

        private ResourceEntry(
            string compatibilityKey,
            Type resourceType,
            Func<object, ValueTask> disposeAsync,
            CancellationTokenSource creationCancellation,
            Lazy<Task<object>> resourceTask)
        {
            CompatibilityKey = compatibilityKey;
            ResourceType = resourceType;
            this.disposeAsync = disposeAsync;
            this.creationCancellation = creationCancellation;
            ResourceTaskSource = resourceTask;
        }

        internal string CompatibilityKey { get; }

        internal Type ResourceType { get; }

        internal int LeaseCount
        {
            get
            {
                lock (stateGate)
                {
                    return leaseCount;
                }
            }
        }

        internal Task<object> ResourceTask => ResourceTaskSource.Value;

        internal Task RemovedTask => removed.Task;

        private Lazy<Task<object>> ResourceTaskSource { get; }

        internal static ResourceEntry Create(
            string compatibilityKey,
            Type resourceType,
            Func<CancellationToken, ValueTask<object>> createAsync,
            Func<object, ValueTask> disposeAsync,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var creationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            Lazy<Task<object>>? lazy = null;
            lazy = new Lazy<Task<object>>(
                async () =>
                {
                    object resource = await createAsync(creationCancellation.Token)
                        .ConfigureAwait(false);
                    TrackingResourceCoordinatorLog.Created(logger);
                    return resource;
                },
                LazyThreadSafetyMode.ExecutionAndPublication);
            return new ResourceEntry(
                compatibilityKey,
                resourceType,
                disposeAsync,
                creationCancellation,
                lazy);
        }

        internal ResourceAcquireResult TryAcquire(string compatibilityKey, Type resourceType)
        {
            lock (stateGate)
            {
                if (finalReleaseStarted)
                {
                    return ResourceAcquireResult.Closing;
                }

                if (!StringComparer.Ordinal.Equals(CompatibilityKey, compatibilityKey)
                    || ResourceType != resourceType)
                {
                    return ResourceAcquireResult.Conflict;
                }

                leaseCount++;
                return ResourceAcquireResult.Acquired;
            }
        }

        internal int Release()
        {
            lock (stateGate)
            {
                if (leaseCount == 0)
                {
                    return -1;
                }

                leaseCount--;
                if (leaseCount == 0)
                {
                    finalReleaseStarted = true;
                }

                return leaseCount;
            }
        }

        internal ValueTask ForceDisposeAsync()
        {
            lock (stateGate)
            {
                finalReleaseStarted = true;
                leaseCount = 0;
            }

            return DisposeResourceAsync();
        }

        internal ValueTask DisposeResourceAsync()
        {
            lock (stateGate)
            {
                resourceDisposalTask ??= DisposeResourceCoreAsync();
                return new ValueTask(resourceDisposalTask);
            }
        }

        internal void MarkRemoved() => removed.TrySetResult();

        private async Task DisposeResourceCoreAsync()
        {
            creationCancellation.Cancel();
            try
            {
                object resource = await ResourceTask.ConfigureAwait(false);
                await disposeAsync(resource).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch when (ResourceTask.IsFaulted)
            {
            }
            finally
            {
                creationCancellation.Dispose();
            }
        }
    }

    private sealed class ResourceLease<TResource>(
        TResource resource,
        Func<ValueTask> releaseAsync) : ITrackingResourceLease<TResource>
    {
        private int disposed;

        public TResource Resource { get; } = resource;

        public ValueTask DisposeAsync() => Interlocked.Exchange(ref disposed, 1) == 0
            ? releaseAsync()
            : ValueTask.CompletedTask;
    }
}

internal static partial class TrackingResourceCoordinatorLog
{
    [LoggerMessage(6620, LogLevel.Information, "Tracking shared resource created")]
    internal static partial void Created(ILogger logger);

    [LoggerMessage(6621, LogLevel.Debug, "Tracking shared resource lease count changed to {LeaseCount}")]
    internal static partial void LeaseCountChanged(ILogger logger, int leaseCount);

    [LoggerMessage(6622, LogLevel.Warning, "Tracking shared resource settings conflict")]
    internal static partial void Conflict(ILogger logger);

    [LoggerMessage(6623, LogLevel.Information, "Tracking shared resource released")]
    internal static partial void Released(ILogger logger);
}
