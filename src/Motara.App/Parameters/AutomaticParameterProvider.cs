using Motara.App.Models;

namespace Motara.App.Parameters;

internal static class AutomaticParameterProvider
{
    private const double BlinkPeriodSeconds = 4;
    private const double BlinkDurationSeconds = 0.2;
    private const double BreathPeriodSeconds = 4;

    internal static bool TryGetBlinkValue(
        ModelParameterSettingConfiguration setting,
        TimeSpan elapsed,
        out double value)
    {
        double phase = PositiveModulo(elapsed.TotalSeconds, BlinkPeriodSeconds);
        double start = BlinkPeriodSeconds - BlinkDurationSeconds;
        if (phase < start)
        {
            value = 0;
            return false;
        }

        double progress = (phase - start) / BlinkDurationSeconds;
        double openness = Math.Abs((2 * progress) - 1);
        value = setting.OutputMinimum
            + ((setting.OutputMaximum - setting.OutputMinimum) * openness);
        return double.IsFinite(value);
    }

    internal static double GetBreathValue(
        ModelParameterSettingConfiguration setting,
        TimeSpan elapsed)
    {
        double wave = Math.Sin(elapsed.TotalSeconds / BreathPeriodSeconds * Math.Tau);
        if (setting.OutputMinimum >= 0)
        {
            return setting.OutputMaximum * ((wave + 1) / 2);
        }

        return wave >= 0
            ? wave * setting.OutputMaximum
            : -wave * setting.OutputMinimum;
    }

    private static double PositiveModulo(double value, double modulus)
    {
        double result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}
