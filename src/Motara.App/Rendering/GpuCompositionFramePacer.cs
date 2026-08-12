using Motara.Persistence;

namespace Motara.App.Rendering;

internal sealed class GpuCompositionFramePacer
{
    internal static TimeSpan TickInterval { get; } = TimeSpan.FromSeconds(1d / 60d);

    private FrameRateMode? previousMode;
    private int halfRatePhase;

    internal bool ShouldRender(FrameRateMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (previousMode != mode)
        {
            previousMode = mode;
            halfRatePhase = 0;
        }

        if (mode is FrameRateMode.FramesPerSecond60 or FrameRateMode.VSync)
        {
            return true;
        }

        bool shouldRender = halfRatePhase == 0;
        halfRatePhase ^= 1;
        return shouldRender;
    }
}
