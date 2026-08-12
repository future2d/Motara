using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Collaboration.Models;

namespace Motara.Collaboration.Drive;

public sealed class ModelDriveCoordinator
{
    public const int MinimumSamplingRateHz = 15;

    private readonly object gate = new();
    private readonly ILogger<ModelDriveCoordinator> logger;
    private ModelDriveInbox? inbox;
    private int samplingRateHz = 60;

    public ModelDriveCoordinator(ILogger<ModelDriveCoordinator>? logger = null) =>
        this.logger = logger ?? NullLogger<ModelDriveCoordinator>.Instance;

    public int SamplingRateHz => Volatile.Read(ref samplingRateHz);

    public void Activate(
        ModelGeneration generation,
        int eventCapacity = 64,
        int samplingRateHz = 60)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(samplingRateHz, MinimumSamplingRateHz);
        ModelDriveInbox next = new(generation, eventCapacity);
        ModelDriveInbox? previous = Interlocked.Exchange(ref inbox, next);
        previous?.Complete();
        Volatile.Write(ref this.samplingRateHz, samplingRateHz);
        ModelDriveEvents.Activated(logger, generation.Value, samplingRateHz);
    }

    public void Release()
    {
        ModelDriveInbox? previous = Interlocked.Exchange(ref inbox, null);
        previous?.Complete();
        ModelDriveEvents.Released(logger);
    }

    public bool PublishSnapshot(ModelDriveSnapshot snapshot) =>
        Volatile.Read(ref inbox)?.PublishSnapshot(snapshot) ?? false;

    public bool PublishEvent(ModelDriveEvent value) =>
        Volatile.Read(ref inbox)?.PublishEvent(value) ?? false;

    public bool RetryEvent(ulong sequence) =>
        Volatile.Read(ref inbox)?.RetryEvent(sequence) ?? false;

    public bool AcknowledgeEvent(ulong sequence) =>
        Volatile.Read(ref inbox)?.AcknowledgeEvent(sequence) ?? false;

    public ModelDriveSnapshot? TakeLatestSnapshot() =>
        Volatile.Read(ref inbox)?.TakeLatestSnapshot();

    public IAsyncEnumerable<ModelDriveEvent> ReadEventsAsync(CancellationToken cancellationToken) =>
        Volatile.Read(ref inbox)?.ReadEventsAsync(cancellationToken) ?? EmptyEventsAsync(cancellationToken);

    public bool TryDowngradeSamplingRate()
    {
        lock (gate)
        {
            int current = samplingRateHz;
            if (current <= MinimumSamplingRateHz)
            {
                return false;
            }

            samplingRateHz = Math.Max(MinimumSamplingRateHz, current / 2);
            ModelDriveEvents.SamplingRateDowngraded(logger, samplingRateHz);
            return true;
        }
    }

    private static async IAsyncEnumerable<ModelDriveEvent> EmptyEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }
}
