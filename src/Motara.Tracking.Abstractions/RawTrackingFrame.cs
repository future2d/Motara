using System.Collections.Immutable;

namespace Motara.Tracking.Abstractions;

/// <summary>Owns one immutable source-native tracking sample.</summary>
public sealed class RawTrackingFrame
{
    /// <summary>Creates a frame by copying the supplied slot buffers.</summary>
    public RawTrackingFrame(
        string sourceId,
        long sequence,
        TimeSpan monotonicTimestamp,
        DateTimeOffset receivedAtUtc,
        ReadOnlySpan<double> values,
        ReadOnlySpan<ParameterValidity> validity,
        TrackingPresence trackingPresence = TrackingPresence.Unknown)
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
        TrackingPresence = trackingPresence;
    }

    /// <summary>Gets the stable identity of the producing source.</summary>
    public string SourceId { get; }

    /// <summary>Gets the source-local monotonically increasing sequence.</summary>
    public long Sequence { get; }

    /// <summary>Gets the monotonic time associated with the sample.</summary>
    public TimeSpan MonotonicTimestamp { get; }

    /// <summary>Gets wall-clock receive metadata for display and diagnostics.</summary>
    public DateTimeOffset ReceivedAtUtc { get; }

    /// <summary>Gets source-native values in stable slot order.</summary>
    public ImmutableArray<double> Values { get; }

    /// <summary>Gets validity values aligned with <see cref="Values"/>.</summary>
    public ImmutableArray<ParameterValidity> Validity { get; }

    /// <summary>Gets the source-native tracked-subject presence state.</summary>
    public TrackingPresence TrackingPresence { get; }
}
