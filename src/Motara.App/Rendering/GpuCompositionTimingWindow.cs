namespace Motara.App.Rendering;

internal readonly record struct GpuCompositionTimingSnapshot(
    int SampleCount,
    double RenderCommandP50Ms,
    double RenderCommandP95Ms,
    double FlushP50Ms,
    double FlushP95Ms,
    double CompositionUpdateP50Ms,
    double CompositionUpdateP95Ms,
    double FrameCycleP50Ms,
    double FrameCycleP95Ms);

internal sealed class GpuCompositionTimingWindow
{
    private const int MaximumSamples = 120;
    private readonly double[] renderCommandSamples = new double[MaximumSamples];
    private readonly double[] flushSamples = new double[MaximumSamples];
    private readonly double[] compositionUpdateSamples = new double[MaximumSamples];
    private readonly double[] frameCycleSamples = new double[MaximumSamples];
    private int sampleCount;
    private int nextSampleIndex;

    internal void Add(
        double renderCommandMs,
        double flushMs,
        double compositionUpdateMs,
        double frameCycleMs)
    {
        int index = nextSampleIndex;
        renderCommandSamples[index] = renderCommandMs;
        flushSamples[index] = flushMs;
        compositionUpdateSamples[index] = compositionUpdateMs;
        frameCycleSamples[index] = frameCycleMs;
        nextSampleIndex = (index + 1) % MaximumSamples;
        sampleCount = Math.Min(sampleCount + 1, MaximumSamples);
    }

    internal GpuCompositionTimingSnapshot SnapshotAndReset()
    {
        int count = sampleCount;
        if (count == 0)
        {
            return default;
        }

        double[] renderCommand = GetOrderedSamples(renderCommandSamples, count);
        double[] flush = GetOrderedSamples(flushSamples, count);
        double[] compositionUpdate = GetOrderedSamples(compositionUpdateSamples, count);
        double[] frameCycle = GetOrderedSamples(frameCycleSamples, count);
        GpuCompositionTimingSnapshot snapshot = new(
            count,
            Percentile(renderCommand, 0.50),
            Percentile(renderCommand, 0.95),
            Percentile(flush, 0.50),
            Percentile(flush, 0.95),
            Percentile(compositionUpdate, 0.50),
            Percentile(compositionUpdate, 0.95),
            Percentile(frameCycle, 0.50),
            Percentile(frameCycle, 0.95));
        sampleCount = 0;
        nextSampleIndex = 0;
        return snapshot;
    }

    private double[] GetOrderedSamples(double[] samples, int count)
    {
        double[] ordered = new double[count];
        if (sampleCount < MaximumSamples || nextSampleIndex == 0)
        {
            Array.Copy(samples, ordered, count);
        }
        else
        {
            int firstCount = MaximumSamples - nextSampleIndex;
            Array.Copy(samples, nextSampleIndex, ordered, 0, firstCount);
            Array.Copy(samples, 0, ordered, firstCount, nextSampleIndex);
        }

        Array.Sort(ordered);
        return ordered;
    }

    private static double Percentile(double[] orderedSamples, double percentile)
    {
        int index = Math.Clamp(
            (int)Math.Ceiling(orderedSamples.Length * percentile) - 1,
            0,
            orderedSamples.Length - 1);
        return orderedSamples[index];
    }
}
