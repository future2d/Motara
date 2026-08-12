namespace Motara.Core.Configuration;

/// <summary>Defines the precompiled processing rules for one source-to-target slot.</summary>
public sealed record ParameterSlotConfiguration(
    int SourceSlot,
    int TargetSlot,
    double NeutralOffset,
    double InputMinimum,
    double InputMaximum,
    double Direction,
    double DeadZone,
    bool Clamp,
    TimeSpan EmaTimeConstant,
    double MaximumRatePerSecond,
    bool PreserveInputScale = false,
    double CalibrationOffset = 0);
