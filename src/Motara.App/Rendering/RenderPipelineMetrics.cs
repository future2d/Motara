namespace Motara.App.Rendering;

internal readonly record struct RenderPipelineMetricSnapshot(
    long ProducedFrames,
    long PresentedFrames,
    long SupersededFrames,
    long ReadyFramesRecycled,
    long FenceFailures,
    double RenderP50Ms,
    double RenderP95Ms,
    double PresentP50Ms,
    double PresentP95Ms);

internal sealed class RenderPipelineMetrics
{
    private long producedFrames;
    private long presentedFrames;
    private long supersededFrames;
    private long readyFramesRecycled;
    private long fenceFailures;
    private readonly object timingGate = new();
    private readonly List<double> renderSamples = [];
    private readonly List<double> presentSamples = [];

    internal void RecordProduced() => Interlocked.Increment(ref producedFrames);

    internal void RecordPresented() => Interlocked.Increment(ref presentedFrames);

    internal void RecordSuperseded() => Interlocked.Increment(ref supersededFrames);

    internal void RecordSuperseded(long count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref supersededFrames, count);
        }
    }

    internal void RecordReadyFrameRecycled() => Interlocked.Increment(ref readyFramesRecycled);

    internal void RecordFenceFailure() => Interlocked.Increment(ref fenceFailures);

    internal void RecordRenderDuration(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0)
        {
            return;
        }

        lock (timingGate)
        {
            renderSamples.Add(milliseconds);
        }
    }

    internal void RecordPresentationDuration(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0)
        {
            return;
        }

        lock (timingGate)
        {
            presentSamples.Add(milliseconds);
        }
    }

    internal RenderPipelineMetricSnapshot SnapshotAndReset()
    {
        double renderP50;
        double renderP95;
        double presentP50;
        double presentP95;
        lock (timingGate)
        {
            renderP50 = Percentile(renderSamples, 0.50);
            renderP95 = Percentile(renderSamples, 0.95);
            presentP50 = Percentile(presentSamples, 0.50);
            presentP95 = Percentile(presentSamples, 0.95);
            renderSamples.Clear();
            presentSamples.Clear();
        }

        return new RenderPipelineMetricSnapshot(
            Interlocked.Exchange(ref producedFrames, 0),
            Interlocked.Exchange(ref presentedFrames, 0),
            Interlocked.Exchange(ref supersededFrames, 0),
            Interlocked.Exchange(ref readyFramesRecycled, 0),
            Interlocked.Exchange(ref fenceFailures, 0),
            renderP50,
            renderP95,
            presentP50,
            presentP95);
    }

    private static double Percentile(List<double> samples, double percentile)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        double[] ordered = [.. samples.Order()];
        int index = Math.Clamp((int)Math.Ceiling(ordered.Length * percentile) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }
}
