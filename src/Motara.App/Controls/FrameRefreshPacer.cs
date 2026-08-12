using Motara.Persistence;

namespace Motara.App.Controls;

internal sealed class FrameRefreshPacer
{
    private TimeSpan? previousTimestamp;
    private double accumulatedSeconds;
    private bool skipNextVSyncFrame;

    internal void Reset()
    {
        previousTimestamp = null;
        accumulatedSeconds = 0;
        skipNextVSyncFrame = false;
    }

    internal bool ShouldRefresh(FrameRateMode mode, TimeSpan timestamp)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (mode == FrameRateMode.VSync)
        {
            return true;
        }

        if (mode == FrameRateMode.VSyncHalf)
        {
            bool refresh = !skipNextVSyncFrame;
            skipNextVSyncFrame = !skipNextVSyncFrame;
            return refresh;
        }

        if (previousTimestamp is not { } previous)
        {
            previousTimestamp = timestamp;
            return true;
        }

        TimeSpan elapsed = timestamp - previous;
        previousTimestamp = timestamp;
        if (elapsed > TimeSpan.Zero)
        {
            accumulatedSeconds += elapsed.TotalSeconds;
        }

        double intervalSeconds = mode == FrameRateMode.FramesPerSecond30 ? 1d / 30d : 1d / 60d;
        if (accumulatedSeconds < intervalSeconds)
        {
            return false;
        }

        accumulatedSeconds %= intervalSeconds;
        return true;
    }
}
