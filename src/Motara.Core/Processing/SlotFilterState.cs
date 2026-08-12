using Motara.Core.Configuration;

namespace Motara.Core.Processing;

internal sealed class SlotFilterState
{
    private static readonly TimeSpan DiagnosticInterval = TimeSpan.FromSeconds(1);
    private bool initialized;
    private double emaValue;
    private TimeSpan? lastNonFiniteDiagnostic;
    private TimeSpan? lastOutOfRangeDiagnostic;
    private double outputValue;
    private TimeSpan timestamp;

    public bool ShouldEmitNonFiniteDiagnostic(TimeSpan currentTimestamp)
    {
        return ShouldEmitDiagnostic(ref lastNonFiniteDiagnostic, currentTimestamp);
    }

    public bool ShouldEmitOutOfRangeDiagnostic(TimeSpan currentTimestamp)
    {
        return ShouldEmitDiagnostic(ref lastOutOfRangeDiagnostic, currentTimestamp);
    }

    private static bool ShouldEmitDiagnostic(ref TimeSpan? lastTimestamp, TimeSpan currentTimestamp)
    {
        if (lastTimestamp.HasValue
            && currentTimestamp - lastTimestamp.Value < DiagnosticInterval)
        {
            return false;
        }

        lastTimestamp = currentTimestamp;
        return true;
    }

    public double Apply(double input, TimeSpan currentTimestamp, ParameterSlotConfiguration configuration)
    {
        if (!initialized)
        {
            initialized = true;
            emaValue = input;
            outputValue = input;
            timestamp = currentTimestamp;
            return input;
        }

        double elapsedSeconds = (currentTimestamp - timestamp).TotalSeconds;
        double filtered = input;

        if (configuration.EmaTimeConstant > TimeSpan.Zero)
        {
            double alpha = 1 - Math.Exp(-elapsedSeconds / configuration.EmaTimeConstant.TotalSeconds);
            filtered = emaValue + (alpha * (input - emaValue));
        }

        emaValue = filtered;

        if (configuration.MaximumRatePerSecond > 0)
        {
            double maximumDelta = configuration.MaximumRatePerSecond * elapsedSeconds;
            filtered = Math.Clamp(filtered, outputValue - maximumDelta, outputValue + maximumDelta);
        }

        outputValue = filtered;
        timestamp = currentTimestamp;
        return filtered;
    }
}
