using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Motara.Collaboration.Models;

namespace Motara.Collaboration.Drive;

/// <summary>
/// Holds the latest continuous drive snapshot and a bounded, explicitly
/// acknowledged action-event window for one active model generation.
/// </summary>
public sealed class ModelDriveInbox
{
    private readonly object gate = new();
    private readonly Channel<ModelDriveEvent> events;
    private readonly ModelGeneration generation;
    private readonly int eventCapacity;
    private readonly int maximumEventRetryAttempts;
    private readonly Dictionary<ulong, PendingEvent> pendingEvents = [];
    private ModelDriveSnapshot? latestSnapshot;
    private ulong latestSnapshotSequence;

    public ModelDriveInbox(
        ModelGeneration generation,
        int eventCapacity,
        int maximumEventRetryAttempts = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(eventCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumEventRetryAttempts);

        this.generation = generation;
        this.eventCapacity = eventCapacity;
        this.maximumEventRetryAttempts = maximumEventRetryAttempts;
        events = Channel.CreateBounded<ModelDriveEvent>(new BoundedChannelOptions(eventCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public bool PublishSnapshot(ModelDriveSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            if (snapshot.Generation != generation || snapshot.Sequence <= latestSnapshotSequence)
            {
                return false;
            }

            latestSnapshot = snapshot;
            latestSnapshotSequence = snapshot.Sequence;
            return true;
        }
    }

    public ModelDriveSnapshot? TakeLatestSnapshot()
    {
        lock (gate)
        {
            ModelDriveSnapshot? snapshot = latestSnapshot;
            latestSnapshot = null;
            return snapshot;
        }
    }

    public bool PublishEvent(ModelDriveEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
        {
            if (value.Generation != generation
                || pendingEvents.Count >= eventCapacity
                || pendingEvents.ContainsKey(value.Sequence)
                || !events.Writer.TryWrite(value))
            {
                return false;
            }

            pendingEvents.Add(value.Sequence, new PendingEvent(value));
            return true;
        }
    }

    public bool RetryEvent(ulong sequence)
    {
        lock (gate)
        {
            if (!pendingEvents.TryGetValue(sequence, out PendingEvent? pending)
                || pending.RetryAttempts >= maximumEventRetryAttempts
                || !events.Writer.TryWrite(pending.Event))
            {
                return false;
            }

            pending.RetryAttempts++;
            return true;
        }
    }

    public bool AcknowledgeEvent(ulong sequence)
    {
        lock (gate)
        {
            return pendingEvents.Remove(sequence);
        }
    }

    public void Complete() => events.Writer.TryComplete();

    public async IAsyncEnumerable<ModelDriveEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (ModelDriveEvent value in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return value;
        }
    }

    private sealed class PendingEvent(ModelDriveEvent value)
    {
        public ModelDriveEvent Event { get; } = value;

        public int RetryAttempts { get; set; }
    }
}
