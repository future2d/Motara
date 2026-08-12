namespace Motara.ModelRuntime.Abstractions;

public enum ModelRenderingBackendState
{
    Cpu = 0,
    SwitchingToGpu = 1,
    Gpu = 2,
    SwitchingToCpu = 3,
}

public enum ModelRenderingBackendFaultReason
{
    GpuUnavailable = 0,
    GpuContextLost = 1,
    GpuTextureUploadFailed = 2,
    GpuRenderingFailed = 3,
    CpuTextureRebuildFailed = 4,
}

public sealed record ModelRenderingBackendStatus
{
    public ModelRenderingBackendStatus(
        ModelRenderingBackendState state,
        ModelRenderingBackendFaultReason? lastFaultReason,
        int? framesPerSecond)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (lastFaultReason is { } reason && !Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(lastFaultReason));
        }

        if (framesPerSecond is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        State = state;
        LastFaultReason = lastFaultReason;
        FramesPerSecond = framesPerSecond;
    }

    public ModelRenderingBackendState State { get; }

    public ModelRenderingBackendFaultReason? LastFaultReason { get; }

    public int? FramesPerSecond { get; }

    public static ModelRenderingBackendStatus Cpu { get; } = new(
        ModelRenderingBackendState.Cpu,
        lastFaultReason: null,
        framesPerSecond: null);
}
