using System.Collections.Immutable;
using Motara.Tracking.Abstractions;

namespace Motara.Core.Frames;

/// <summary>Owns one immutable frame in Motara's canonical parameter layout.</summary>
public sealed class MotaraParameterFrame
{
    /// <summary>Creates a processed frame by copying the supplied slot buffers.</summary>
    public MotaraParameterFrame(
        string sourceId,
        long sequence,
        TimeSpan monotonicTimestamp,
        DateTimeOffset receivedAtUtc,
        ReadOnlySpan<double> values,
        ReadOnlySpan<ParameterValidity> validity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(monotonicTimestamp, TimeSpan.Zero);

        if (values.Length != validity.Length)
        {
            throw new ArgumentException("Value and validity buffers must have equal lengths.", nameof(validity));
        }

        SourceId = sourceId;
        Sequence = sequence;
        MonotonicTimestamp = monotonicTimestamp;
        ReceivedAtUtc = receivedAtUtc;
        Values = ImmutableArray.CreateRange(values.ToArray());
        Validity = ImmutableArray.CreateRange(validity.ToArray());
    }

    /// <summary>Gets the stable identity of the source that produced the frame.</summary>
    public string SourceId { get; }

    /// <summary>Gets the source-local sequence.</summary>
    public long Sequence { get; }

    /// <summary>Gets the monotonic time associated with the frame.</summary>
    public TimeSpan MonotonicTimestamp { get; }

    /// <summary>Gets wall-clock receive metadata.</summary>
    public DateTimeOffset ReceivedAtUtc { get; }

    /// <summary>Gets canonical values in registry slot order.</summary>
    public ImmutableArray<double> Values { get; }

    /// <summary>Gets validity values aligned with <see cref="Values"/>.</summary>
    public ImmutableArray<ParameterValidity> Validity { get; }
}
