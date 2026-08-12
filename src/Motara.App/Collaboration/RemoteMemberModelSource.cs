using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Identity;
using Motara.Collaboration.Models;
using System.Collections.Immutable;

namespace Motara.App.Collaboration;

internal interface IRemoteMemberModelRuntime : IAsyncDisposable
{
}

/// <summary>Runtime-only scene source for one collaborating member's in-memory model.</summary>
internal sealed class RemoteMemberModelSource : IAsyncDisposable
{
    private readonly SemaphoreSlim replacementGate = new(1, 1);
    private readonly ILogger<RemoteMemberModelSource> logger;
    private IRemoteModelPackage? package;
    private IRemoteMemberModelRuntime? runtime;
    private int disposed;

    internal RemoteMemberModelSource(
        DeviceId memberId,
        ILogger<RemoteMemberModelSource>? logger = null)
    {
        MemberId = memberId;
        this.logger = logger ?? NullLogger<RemoteMemberModelSource>.Instance;
    }

    internal DeviceId MemberId { get; }

    internal bool IsVisible { get; private set; } = true;

    internal bool IsLocked { get; private set; }

    internal IRemoteMemberModelRuntime? Runtime => Volatile.Read(ref runtime);

    internal IRemoteRenderableModelRuntime? RenderableRuntime => Runtime as IRemoteRenderableModelRuntime;

    internal void SetVisibility(bool isVisible) => IsVisible = isVisible;

    internal void SetLock(bool isLocked) => IsLocked = isLocked;

    internal async Task ReplaceAsync(
        IRemoteModelPackage nextPackage,
        Func<IRemoteModelPackage, CancellationToken, Task<IRemoteMemberModelRuntime>> runtimeFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nextPackage);
        ArgumentNullException.ThrowIfNull(runtimeFactory);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        IRemoteMemberModelRuntime? candidate = null;
        try
        {
            candidate = await runtimeFactory(nextPackage, cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(candidate);
        }
        catch
        {
            await nextPackage.DisposeAsync().ConfigureAwait(false);
            RemoteMemberModelSourceLog.ReplacementFailed(logger);
            throw;
        }

        IRemoteModelPackage? previousPackage;
        IRemoteMemberModelRuntime? previousRuntime;
        await replacementGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            previousPackage = package;
            previousRuntime = runtime;
            package = nextPackage;
            runtime = candidate;
            candidate = null;
        }
        finally
        {
            replacementGate.Release();
        }

        if (previousRuntime is not null)
        {
            await previousRuntime.DisposeAsync().ConfigureAwait(false);
        }

        if (previousPackage is not null)
        {
            await previousPackage.DisposeAsync().ConfigureAwait(false);
        }

        RemoteMemberModelSourceLog.ReplacementCommitted(logger);
    }

    internal async Task ReleaseAsync()
    {
        IRemoteModelPackage? previousPackage;
        IRemoteMemberModelRuntime? previousRuntime;
        await replacementGate.WaitAsync().ConfigureAwait(false);
        try
        {
            previousPackage = Interlocked.Exchange(ref package, null);
            previousRuntime = Interlocked.Exchange(ref runtime, null);
        }
        finally
        {
            replacementGate.Release();
        }

        if (previousRuntime is not null)
        {
            await previousRuntime.DisposeAsync().ConfigureAwait(false);
        }

        if (previousPackage is not null)
        {
            await previousPackage.DisposeAsync().ConfigureAwait(false);
        }

        RemoteMemberModelSourceLog.Released(logger);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await ReleaseAsync().ConfigureAwait(false);
        replacementGate.Dispose();
    }
}

internal static partial class RemoteMemberModelSourceLog
{
    [LoggerMessage(8154, LogLevel.Information, "Remote member model source replacement committed")]
    internal static partial void ReplacementCommitted(ILogger logger);

    [LoggerMessage(8155, LogLevel.Warning, "Remote member model source replacement failed")]
    internal static partial void ReplacementFailed(ILogger logger);

    [LoggerMessage(8156, LogLevel.Information, "Remote member model source released")]
    internal static partial void Released(ILogger logger);
}

internal sealed class RemoteMemberModelSourceRegistry : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Func<DeviceId, IRemoteModelPackage, CancellationToken, Task<IRemoteMemberModelRuntime>> runtimeFactory;
    private readonly Dictionary<DeviceId, RemoteMemberModelSource> sources = [];
    private int disposed;

    internal RemoteMemberModelSourceRegistry(
        Func<DeviceId, IRemoteModelPackage, CancellationToken, Task<IRemoteMemberModelRuntime>> runtimeFactory) =>
        this.runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));

    internal ImmutableArray<RemoteMemberModelSource> Sources
    {
        get
        {
            lock (gate)
            {
                return [.. sources.Values];
            }
        }
    }

    internal async Task ApplyReadyPackageAsync(
        DeviceId member,
        IRemoteModelPackage package,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        RemoteMemberModelSource source;
        lock (gate)
        {
            source = sources.GetValueOrDefault(member) ?? new RemoteMemberModelSource(member);
            sources[member] = source;
        }

        await source.ReplaceAsync(
            package,
            (next, token) => runtimeFactory(member, next, token),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task ReleaseMemberAsync(DeviceId member)
    {
        RemoteMemberModelSource? source;
        lock (gate)
        {
            sources.Remove(member, out source);
        }

        if (source is not null)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        RemoteMemberModelSource[] owned;
        lock (gate)
        {
            owned = [.. sources.Values];
            sources.Clear();
        }

        foreach (RemoteMemberModelSource source in owned)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }
}
