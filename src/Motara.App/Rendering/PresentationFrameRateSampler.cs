namespace Motara.App.Rendering;

internal sealed class PresentationFrameRateSampler
{
    private static readonly TimeSpan MeasurementWindow = TimeSpan.FromSeconds(1);
    private readonly object gate = new();
    private readonly TimeProvider timeProvider;
    private long windowStartedAt;
    private int completedFrames;
    private bool windowStarted;

    internal PresentationFrameRateSampler(TimeProvider? timeProvider = null) =>
        this.timeProvider = timeProvider ?? TimeProvider.System;

    internal double? RecordCompletedFrame()
    {
        lock (gate)
        {
            long now = timeProvider.GetTimestamp();
            if (!windowStarted)
            {
                windowStarted = true;
                windowStartedAt = now;
                completedFrames = 1;
                return null;
            }

            completedFrames++;
            TimeSpan elapsed = timeProvider.GetElapsedTime(windowStartedAt, now);
            if (elapsed < MeasurementWindow)
            {
                return null;
            }

            double framesPerSecond = completedFrames / elapsed.TotalSeconds;
            windowStartedAt = now;
            completedFrames = 0;
            return framesPerSecond;
        }
    }

    internal void Reset()
    {
        lock (gate)
        {
            windowStarted = false;
            windowStartedAt = 0;
            completedFrames = 0;
        }
    }
}
