using Avalonia;
using Avalonia.Rendering.SceneGraph;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Motara.ModelRuntime.PurismCore;

internal interface IGpuRetirementResource
{
    object ContextIdentity { get; }

    bool IsContextAbandoned { get; }

    int ResourceCount { get; }

    long EstimatedBytes { get; }

    void DisposeOnGpuThread();
}

internal interface IGpuRetirementContext
{
    object ContextIdentity { get; }

    bool IsAbandoned { get; }

    void Flush(bool submit, bool synchronous);
}

internal sealed class GpuRetirementTicket
{
    internal GpuRetirementTicket(Task completion)
    {
        Completion = completion;
    }

    internal Task Completion { get; }
}

internal readonly record struct GpuRetirementDrainResult(
    int RetiredSetCount,
    int RetiredResourceCount,
    long RetiredBytes,
    bool ContextWasAbandoned);

internal sealed class GpuResourceRetirementQueue
{
    private sealed class PendingItem(IGpuRetirementResource resource)
    {
        internal IGpuRetirementResource Resource { get; } = resource;

        internal TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object gate = new();
    private readonly ILogger logger;
    private readonly List<PendingItem> pending = [];
    private readonly Dictionary<IGpuRetirementResource, PendingItem> tracked =
        new(ReferenceEqualityComparer.Instance);
    private int contextMismatchLogged;

    internal GpuResourceRetirementQueue()
        : this(NullLogger.Instance)
    {
    }

    internal GpuResourceRetirementQueue(ILogger logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal event EventHandler? WorkPending;

    internal bool HasPendingWork
    {
        get
        {
            lock (gate)
            {
                return pending.Count > 0;
            }
        }
    }

    internal int PendingCount
    {
        get
        {
            lock (gate)
            {
                return pending.Count;
            }
        }
    }

    internal GpuRetirementTicket Enqueue(IGpuRetirementResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        PendingItem item;
        bool notify = false;
        int pendingCount;
        lock (gate)
        {
            if (tracked.TryGetValue(resource, out PendingItem? existing))
            {
                return new GpuRetirementTicket(existing.Completion.Task);
            }

            notify = pending.Count == 0;
            item = new PendingItem(resource);
            pending.Add(item);
            tracked.Add(resource, item);
            pendingCount = pending.Count;
        }

        if (notify)
        {
            WorkPending?.Invoke(this, EventArgs.Empty);
        }

        GpuRetirementLog.Queued(
            logger,
            pendingCount,
            resource.ResourceCount,
            resource.EstimatedBytes);

        return new GpuRetirementTicket(item.Completion.Task);
    }

    internal GpuRetirementDrainResult Drain(IGpuRetirementContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return DrainCore(context);
    }

    internal GpuRetirementDrainResult DrainAbandoned() => DrainCore(context: null);

    private GpuRetirementDrainResult DrainCore(IGpuRetirementContext? context)
    {
        List<(PendingItem Item, bool Abandoned)> selected;
        lock (gate)
        {
            selected = pending
                .Select(item => (
                    Item: item,
                    Abandoned: item.Resource.IsContextAbandoned
                        || (context?.IsAbandoned == true
                            && ReferenceEquals(
                                item.Resource.ContextIdentity,
                                context.ContextIdentity))))
                .Where(candidate => candidate.Abandoned
                    || (context is not null
                        && ReferenceEquals(
                            candidate.Item.Resource.ContextIdentity,
                            context.ContextIdentity)))
                .ToList();
            if (selected.Count == 0)
            {
                if (context is not null
                    && pending.Count > 0
                    && Interlocked.Exchange(ref contextMismatchLogged, 1) == 0)
                {
                    GpuRetirementLog.ContextMismatch(logger, pending.Count);
                }

                return default;
            }

            foreach ((PendingItem item, _) in selected)
            {
                pending.Remove(item);
            }
        }

        Interlocked.Exchange(ref contextMismatchLogged, 0);

        List<PendingItem> normal = selected
            .Where(candidate => !candidate.Abandoned)
            .Select(candidate => candidate.Item)
            .ToList();
        if (normal.Count > 0)
        {
            try
            {
                context!.Flush(submit: true, synchronous: true);
            }
            catch
            {
                Requeue(normal);
                selected.RemoveAll(candidate => !candidate.Abandoned);
                GpuRetirementLog.FlushFailed(logger, PendingCount);
            }
        }

        int abandonedCount = selected.Count(candidate => candidate.Abandoned);
        if (abandonedCount > 0)
        {
            GpuRetirementLog.ContextAbandoned(logger, abandonedCount);
        }

        int retiredResourceCount = 0;
        long retiredBytes = 0;
        foreach ((PendingItem item, _) in selected)
        {
            try
            {
                item.Resource.DisposeOnGpuThread();
                retiredResourceCount += item.Resource.ResourceCount;
                retiredBytes = checked(retiredBytes + item.Resource.EstimatedBytes);
                item.Completion.TrySetResult();
            }
            catch (Exception exception)
            {
                GpuRetirementLog.DisposalFailed(
                    logger,
                    exception,
                    item.Resource.ResourceCount,
                    item.Resource.EstimatedBytes);
                item.Completion.TrySetException(exception);
            }
            finally
            {
                lock (gate)
                {
                    tracked.Remove(item.Resource);
                }
            }
        }

        var result = new GpuRetirementDrainResult(
            selected.Count,
            retiredResourceCount,
            retiredBytes,
            abandonedCount > 0);
        GpuRetirementLog.Completed(
            logger,
            result.RetiredSetCount,
            result.RetiredResourceCount,
            result.RetiredBytes,
            PendingCount);
        return result;
    }

    internal GpuRetirementDrainResult Drain(GRContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Drain(new SkiaGpuRetirementContext(context));
    }

    internal ICustomDrawOperation CreateMaintenanceOperation(Rect bounds) =>
        new SkiaGpuMaintenanceDrawOperation(this, bounds);

    private void Requeue(List<PendingItem> items)
    {
        bool notify;
        lock (gate)
        {
            notify = pending.Count == 0;
            pending.InsertRange(0, items);
        }

        if (notify)
        {
            WorkPending?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed class SkiaModelRenderMaintenance
{
    private readonly GpuResourceRetirementQueue retirementQueue;

    public SkiaModelRenderMaintenance()
        : this(NullLogger.Instance)
    {
    }

    public SkiaModelRenderMaintenance(ILogger logger)
    {
        retirementQueue = new GpuResourceRetirementQueue(
            logger ?? throw new ArgumentNullException(nameof(logger)));
    }

    public event EventHandler? WorkPending
    {
        add => retirementQueue.WorkPending += value;
        remove => retirementQueue.WorkPending -= value;
    }

    public bool HasPendingWork => retirementQueue.HasPendingWork;

    public ICustomDrawOperation CreateMaintenanceOperation(Rect bounds) =>
        retirementQueue.CreateMaintenanceOperation(bounds);

    internal GpuResourceRetirementQueue RetirementQueue => retirementQueue;
}

internal static partial class GpuRetirementLog
{
    [LoggerMessage(7030, LogLevel.Debug,
        "GPU resource retirement queued: {PendingSetCount} sets pending, {ResourceCount} resources, {EstimatedBytes} bytes")]
    internal static partial void Queued(
        ILogger logger,
        int pendingSetCount,
        int resourceCount,
        long estimatedBytes);

    [LoggerMessage(7031, LogLevel.Debug,
        "GPU resource retirement context mismatch with {PendingSetCount} sets pending")]
    internal static partial void ContextMismatch(ILogger logger, int pendingSetCount);

    [LoggerMessage(7032, LogLevel.Warning,
        "GPU context was abandoned while retiring {RetiredSetCount} resource sets")]
    internal static partial void ContextAbandoned(ILogger logger, int retiredSetCount);

    [LoggerMessage(7033, LogLevel.Information,
        "GPU resource retirement completed: {RetiredSetCount} sets, {ResourceCount} resources, {RetiredBytes} bytes, {PendingSetCount} sets pending")]
    internal static partial void Completed(
        ILogger logger,
        int retiredSetCount,
        int resourceCount,
        long retiredBytes,
        int pendingSetCount);

    [LoggerMessage(7034, LogLevel.Warning,
        "GPU resource retirement flush failed; {PendingSetCount} sets remain queued")]
    internal static partial void FlushFailed(ILogger logger, int pendingSetCount);

    [LoggerMessage(7035, LogLevel.Error,
        "GPU resource retirement disposal failed for {ResourceCount} resources and {EstimatedBytes} bytes")]
    internal static partial void DisposalFailed(
        ILogger logger,
        Exception exception,
        int resourceCount,
        long estimatedBytes);
}

internal sealed class SkiaGpuRetirementContext(GRContext context) : IGpuRetirementContext
{
    private readonly GRContext context = context ?? throw new ArgumentNullException(nameof(context));

    public object ContextIdentity => context;

    public bool IsAbandoned => context.IsAbandoned;

    public void Flush(bool submit, bool synchronous) => context.Flush(submit, synchronous);
}
