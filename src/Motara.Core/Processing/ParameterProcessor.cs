using System.Collections.Immutable;
using Motara.Core.Configuration;
using Motara.Core.Diagnostics;
using Motara.Core.Frames;
using Motara.Tracking.Abstractions;

namespace Motara.Core.Processing;

/// <summary>Contains one processed frame and diagnostics raised while producing it.</summary>
public sealed record ProcessingResult(
    MotaraParameterFrame Frame,
    ImmutableArray<DiagnosticEvent> Diagnostics);

/// <summary>Transforms immutable source frames into the canonical parameter layout.</summary>
public sealed class ParameterProcessor
{
    private static readonly TimeSpan LongGapThreshold = TimeSpan.FromMilliseconds(500);
    private readonly object gate = new();
    private PipelineConfiguration configuration;
    private SlotFilterState[] filterStates;
    private string? lastSourceId;
    private TimeSpan? lastTimestamp;

    /// <summary>Creates a processor from a completely validated configuration.</summary>
    public ParameterProcessor(PipelineConfiguration configuration)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        filterStates = CreateFilterStates(configuration);
    }

    /// <summary>Atomically replaces the configuration and resets all filter state.</summary>
    public void ReplaceConfiguration(PipelineConfiguration replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        lock (gate)
        {
            configuration = replacement;
            filterStates = CreateFilterStates(replacement);
            lastSourceId = null;
            lastTimestamp = null;
        }
    }

    /// <summary>Processes one source frame using only precompiled integer slot mappings.</summary>
    public ProcessingResult Process(RawTrackingFrame sourceFrame)
    {
        ArgumentNullException.ThrowIfNull(sourceFrame);

        lock (gate)
        {
            if (sourceFrame.Values.Length != configuration.SourceSlotCount)
            {
                throw new ArgumentException("Frame slot count does not match the configured source layout.", nameof(sourceFrame));
            }

            ResetForDiscontinuity(sourceFrame);

            int targetCount = configuration.TargetRegistry.Count;
            var values = new double[targetCount];
            var validity = new ParameterValidity[targetCount];
            var diagnostics = ImmutableArray.CreateBuilder<DiagnosticEvent>();

            for (int targetSlot = 0; targetSlot < targetCount; targetSlot++)
            {
                values[targetSlot] = configuration.TargetRegistry.Definitions[targetSlot].NeutralValue;
                validity[targetSlot] = ParameterValidity.Missing;
            }

            for (int index = 0; index < configuration.Slots.Length; index++)
            {
                ParameterSlotConfiguration slot = configuration.Slots[index];
                ParameterValidity sourceValidity = sourceFrame.Validity[slot.SourceSlot];

                if (sourceValidity != ParameterValidity.Valid)
                {
                    validity[slot.TargetSlot] = sourceValidity;
                    continue;
                }

                double sourceValue = sourceFrame.Values[slot.SourceSlot];
                if (!double.IsFinite(sourceValue))
                {
                    validity[slot.TargetSlot] = ParameterValidity.Invalid;
                    if (filterStates[index].ShouldEmitNonFiniteDiagnostic(sourceFrame.MonotonicTimestamp))
                    {
                        diagnostics.Add(new DiagnosticEvent(
                            "core.input.non_finite",
                            sourceFrame.SourceId,
                            sourceFrame.Sequence,
                            slot.SourceSlot));
                    }

                    continue;
                }

                (double calibrated, bool outOfRange) = Calibrate(sourceValue, slot);
                if (outOfRange
                    && filterStates[index].ShouldEmitOutOfRangeDiagnostic(sourceFrame.MonotonicTimestamp))
                {
                    diagnostics.Add(new DiagnosticEvent(
                        "core.input.out_of_range",
                        sourceFrame.SourceId,
                        sourceFrame.Sequence,
                        slot.SourceSlot));
                }

                values[slot.TargetSlot] = filterStates[index].Apply(
                    calibrated,
                    sourceFrame.MonotonicTimestamp,
                    slot);
                validity[slot.TargetSlot] = ParameterValidity.Valid;
            }

            lastSourceId = sourceFrame.SourceId;
            lastTimestamp = sourceFrame.MonotonicTimestamp;

            return new ProcessingResult(
                new MotaraParameterFrame(
                    sourceFrame.SourceId,
                    sourceFrame.Sequence,
                    sourceFrame.MonotonicTimestamp,
                    sourceFrame.ReceivedAtUtc,
                    values,
                    validity),
                diagnostics.ToImmutable());
        }
    }

    private static SlotFilterState[] CreateFilterStates(PipelineConfiguration configuration)
    {
        return Enumerable.Range(0, configuration.Slots.Length)
            .Select(static _ => new SlotFilterState())
            .ToArray();
    }

    private static (double Value, bool OutOfRange) Calibrate(
        double sourceValue,
        ParameterSlotConfiguration slot)
    {
        sourceValue -= slot.CalibrationOffset;
        if (slot.PreserveInputScale)
        {
            bool outsideDeclaredRange = sourceValue < slot.InputMinimum
                || sourceValue > slot.InputMaximum;
            double directed = slot.NeutralOffset
                + ((sourceValue - slot.NeutralOffset) * slot.Direction);
            double preservedValue = slot.Clamp
                ? Math.Clamp(directed, slot.InputMinimum, slot.InputMaximum)
                : directed;
            return (preservedValue, outsideDeclaredRange);
        }

        double centered = sourceValue - slot.NeutralOffset;
        double denominator = centered >= 0
            ? slot.InputMaximum - slot.NeutralOffset
            : slot.NeutralOffset - slot.InputMinimum;
        double normalized = centered / denominator;
        bool outOfRange = Math.Abs(normalized) > 1;
        double value = normalized * slot.Direction;
        double magnitude = Math.Abs(value);

        value = magnitude <= slot.DeadZone
            ? 0
            : Math.CopySign((magnitude - slot.DeadZone) / (1 - slot.DeadZone), value);

        return (slot.Clamp ? Math.Clamp(value, -1, 1) : value, outOfRange);
    }

    private void ResetForDiscontinuity(RawTrackingFrame frame)
    {
        bool sourceChanged = lastSourceId is not null
            && !StringComparer.Ordinal.Equals(lastSourceId, frame.SourceId);
        bool timestampRegressed = lastTimestamp.HasValue
            && frame.MonotonicTimestamp < lastTimestamp.Value;
        bool longGap = lastTimestamp.HasValue
            && frame.MonotonicTimestamp - lastTimestamp.Value > LongGapThreshold;

        if (!sourceChanged && !timestampRegressed && !longGap)
        {
            return;
        }

        filterStates = CreateFilterStates(configuration);
    }
}
