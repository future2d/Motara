using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.ModelLibrary;
using Motara.ModelRuntime.Abstractions;

namespace Motara.App.Models;

internal sealed record ActiveModelParameterBindingSnapshot(
    ModelId ModelId,
    long Version,
    ImmutableArray<ModelParameterSettingConfiguration> Settings);

internal sealed class ActiveModelParameterBindingSource
{
    private readonly Func<ActiveModel, CancellationToken, Task<ImmutableArray<ModelParameterSettingConfiguration>>> loader;
    private readonly ILogger<ActiveModelParameterBindingSource> logger;
    private readonly object gate = new();
    private ActiveModelParameterBindingSnapshot? current;
    private long version;

    internal ActiveModelParameterBindingSource(
        Func<ActiveModel, CancellationToken, Task<ImmutableArray<ModelParameterSettingConfiguration>>> loader,
        ILogger<ActiveModelParameterBindingSource>? logger = null)
    {
        this.loader = loader ?? throw new ArgumentNullException(nameof(loader));
        this.logger = logger ?? NullLogger<ActiveModelParameterBindingSource>.Instance;
    }

    internal event EventHandler? Changed;

    internal ActiveModelParameterBindingSnapshot? Current => Volatile.Read(ref current);

    internal async Task<bool> ReloadAsync(ActiveModel active, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(active);
        try
        {
            ImmutableArray<ModelParameterSettingConfiguration> settings = await loader(active, cancellationToken)
                .ConfigureAwait(false);
            ActiveModelParameterBindingSnapshot next;
            lock (gate)
            {
                next = new(active.Id, ++version, settings);
                Volatile.Write(ref current, next);
            }

            ActiveModelParameterBindingSourceLog.Reloaded(logger, active.Id.Value, settings.Length, next.Version);
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ActiveModelParameterBindingSourceLog.ReloadFailed(logger, exception, active.Id.Value);
            return false;
        }
    }

    internal bool TryGet(ModelId modelId, out ActiveModelParameterBindingSnapshot snapshot)
    {
        ActiveModelParameterBindingSnapshot? candidate = Current;
        if (candidate is not null && candidate.ModelId == modelId)
        {
            snapshot = candidate;
            return true;
        }

        snapshot = null!;
        return false;
    }
}

internal static partial class ActiveModelParameterBindingSourceLog
{
    [LoggerMessage(6510, LogLevel.Debug,
        "Active model parameter mapping reloaded for {ModelId}: {BindingCount} bindings, version {Version}")]
    internal static partial void Reloaded(ILogger logger, string modelId, int bindingCount, long version);

    [LoggerMessage(6511, LogLevel.Warning,
        "Active model parameter mapping reload failed for {ModelId}; keeping last valid mapping")]
    internal static partial void ReloadFailed(ILogger logger, Exception exception, string modelId);
}
