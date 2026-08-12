using System.Collections.Immutable;
using Motara.Core.Parameters;

namespace Motara.Core.Configuration;

/// <summary>Contains a fully validated, immutable real-time processing configuration.</summary>
public sealed class PipelineConfiguration
{
    private PipelineConfiguration(
        ParameterRegistry targetRegistry,
        int sourceSlotCount,
        ImmutableArray<ParameterSlotConfiguration> slots)
    {
        TargetRegistry = targetRegistry;
        SourceSlotCount = sourceSlotCount;
        Slots = slots;
    }

    /// <summary>Gets the canonical target registry.</summary>
    public ParameterRegistry TargetRegistry { get; }

    /// <summary>Gets the exact number of slots expected from the source layout.</summary>
    public int SourceSlotCount { get; }

    /// <summary>Gets prevalidated mappings in processing order.</summary>
    public ImmutableArray<ParameterSlotConfiguration> Slots { get; }

    /// <summary>Builds a configuration after validating every mapping as one unit.</summary>
    public static PipelineConfiguration Create(
        ParameterRegistry targetRegistry,
        int sourceSlotCount,
        IEnumerable<ParameterSlotConfiguration> slots)
    {
        ArgumentNullException.ThrowIfNull(targetRegistry);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceSlotCount);
        ArgumentNullException.ThrowIfNull(slots);

        var ordered = ImmutableArray.CreateBuilder<ParameterSlotConfiguration>();
        var targetSlots = new HashSet<int>();

        foreach (ParameterSlotConfiguration slot in slots)
        {
            ArgumentNullException.ThrowIfNull(slot);
            ValidateSlot(slot, sourceSlotCount, targetRegistry.Count);

            if (!targetSlots.Add(slot.TargetSlot))
            {
                throw new ArgumentException($"Duplicate target slot: {slot.TargetSlot}", nameof(slots));
            }

            ordered.Add(slot);
        }

        return new PipelineConfiguration(targetRegistry, sourceSlotCount, ordered.ToImmutable());
    }

    private static void ValidateSlot(ParameterSlotConfiguration slot, int sourceCount, int targetCount)
    {
        if ((uint)slot.SourceSlot >= (uint)sourceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), "Source slot is outside the source layout.");
        }

        if ((uint)slot.TargetSlot >= (uint)targetCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), "Target slot is outside the target registry.");
        }

        bool invalidRange = slot.PreserveInputScale
            ? slot.InputMinimum >= slot.InputMaximum
                || slot.InputMinimum > slot.NeutralOffset
                || slot.NeutralOffset > slot.InputMaximum
            : slot.InputMinimum >= slot.NeutralOffset
                || slot.NeutralOffset >= slot.InputMaximum;
        if (!double.IsFinite(slot.NeutralOffset)
            || !double.IsFinite(slot.CalibrationOffset)
            || !double.IsFinite(slot.InputMinimum)
            || !double.IsFinite(slot.InputMaximum)
            || invalidRange)
        {
            throw new ArgumentException("Input range must be finite and contain the neutral offset.", nameof(slot));
        }

        if (slot.Direction is not (-1d or 1d))
        {
            throw new ArgumentException("Direction must be either -1 or 1.", nameof(slot));
        }

        if (!double.IsFinite(slot.DeadZone) || slot.DeadZone < 0 || slot.DeadZone >= 1)
        {
            throw new ArgumentException("Dead zone must be finite and in the range [0, 1).", nameof(slot));
        }

        if (slot.EmaTimeConstant < TimeSpan.Zero)
        {
            throw new ArgumentException("EMA time constant cannot be negative.", nameof(slot));
        }

        if (!double.IsFinite(slot.MaximumRatePerSecond) || slot.MaximumRatePerSecond < 0)
        {
            throw new ArgumentException("Maximum rate must be finite and non-negative.", nameof(slot));
        }
    }
}
