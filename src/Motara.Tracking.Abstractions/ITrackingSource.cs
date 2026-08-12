namespace Motara.Tracking.Abstractions;

/// <summary>Streams immutable frames from one tracking source.</summary>
public interface ITrackingSource : IAsyncDisposable
{
    /// <summary>Gets the stable identity of this source instance.</summary>
    string SourceId { get; }

    /// <summary>Reads frames until cancellation or normal source completion.</summary>
    IAsyncEnumerable<RawTrackingFrame> ReadFramesAsync(CancellationToken cancellationToken);
}

/// <summary>Optionally exposes the canonical output order emitted in each frame.</summary>
public interface ITrackingSourceOutputLayout
{
    IReadOnlyList<TrackingOutputDefinition> OutputDefinitions { get; }
}

/// <summary>Optionally exposes a source-native neutral-pose calibration operation.</summary>
public interface ITrackingSourceCalibration
{
    Task<TrackingCalibrationResult> CalibrateAsync(CancellationToken cancellationToken);
}

public sealed record TrackingCalibrationResult(bool Succeeded, string? ReasonCode = null)
{
    public static TrackingCalibrationResult Success { get; } = new(true);

    public static TrackingCalibrationResult Failure(string reasonCode) =>
        new(false, string.IsNullOrWhiteSpace(reasonCode)
            ? throw new ArgumentException("A calibration failure reason is required.", nameof(reasonCode))
            : reasonCode);
}

public sealed record TrackingOutputDefinition(
    string Id,
    double NeutralValue,
    double SuggestedMinimum,
    double SuggestedMaximum,
    double Smoothing);
