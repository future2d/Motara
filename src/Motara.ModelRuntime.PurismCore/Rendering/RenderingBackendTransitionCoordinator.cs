using Motara.ModelRuntime.Abstractions;

namespace Motara.ModelRuntime.PurismCore;

internal sealed class RenderingBackendTransitionCoordinator
{
    private readonly object gate = new();
    private ModelRenderingBackendPreference desiredBackend = ModelRenderingBackendPreference.Cpu;
    private ModelRenderingBackendPreference activeBackend = ModelRenderingBackendPreference.Cpu;
    private ModelRenderingBackendStatus status = ModelRenderingBackendStatus.Cpu;
    private long generation;
    private bool gpuUploadInProgress;
    private bool cpuRebuildInProgress;
    private bool cpuFallbackInProgress;
    private long? gpuRetryBlockedGeneration;
    private long? cpuRetryBlockedGeneration;
    private ModelRenderingBackendFaultReason? lastFaultReason;
    private bool gpuRenderingAvailable;
    private bool disposed;

    internal ModelRenderingBackendPreference DesiredBackend
    {
        get
        {
            lock (gate)
            {
                return desiredBackend;
            }
        }
    }

    internal long Generation
    {
        get
        {
            lock (gate)
            {
                return generation;
            }
        }
    }

    internal ModelRenderingBackendPreference ActiveBackend
    {
        get
        {
            lock (gate)
            {
                return activeBackend;
            }
        }
    }

    internal ModelRenderingBackendStatus Status
    {
        get
        {
            lock (gate)
            {
                return status;
            }
        }
    }

    internal bool CanRenderGpu
    {
        get
        {
            lock (gate)
            {
                return !disposed
                    && activeBackend == ModelRenderingBackendPreference.Gpu
                    && gpuRenderingAvailable;
            }
        }
    }

    internal long SetDesired(ModelRenderingBackendPreference preference)
    {
        if (!Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (desiredBackend == preference)
            {
                return generation;
            }

            desiredBackend = preference;
            generation++;
            gpuUploadInProgress = false;
            cpuRebuildInProgress = false;
            cpuFallbackInProgress = false;
            gpuRetryBlockedGeneration = null;
            cpuRetryBlockedGeneration = null;
            if (activeBackend != ModelRenderingBackendPreference.Gpu || gpuRenderingAvailable)
            {
                lastFaultReason = null;
            }
            status = CreateStatus();
            return generation;
        }
    }

    internal bool TryBeginGpuUpload(long expectedGeneration)
    {
        lock (gate)
        {
            if (disposed
                || expectedGeneration != generation
                || desiredBackend != ModelRenderingBackendPreference.Gpu
                || activeBackend != ModelRenderingBackendPreference.Cpu
                || gpuRetryBlockedGeneration == expectedGeneration
                || gpuUploadInProgress)
            {
                return false;
            }

            gpuUploadInProgress = true;
            cpuRebuildInProgress = false;
            cpuFallbackInProgress = false;
            status = new ModelRenderingBackendStatus(
                ModelRenderingBackendState.SwitchingToGpu,
                lastFaultReason: null,
                framesPerSecond: null);
            return true;
        }
    }

    internal bool TryCommitGpu(long expectedGeneration)
    {
        lock (gate)
        {
            if (disposed
                || expectedGeneration != generation
                || desiredBackend != ModelRenderingBackendPreference.Gpu
                || activeBackend != ModelRenderingBackendPreference.Cpu
                || !gpuUploadInProgress)
            {
                return false;
            }

            activeBackend = ModelRenderingBackendPreference.Gpu;
            gpuRenderingAvailable = true;
            gpuUploadInProgress = false;
            status = new ModelRenderingBackendStatus(
                ModelRenderingBackendState.Gpu,
                lastFaultReason: null,
                framesPerSecond: null);
            return true;
        }
    }

    internal bool FailGpuAttempt(
        long expectedGeneration,
        ModelRenderingBackendFaultReason faultReason)
    {
        if (!Enum.IsDefined(faultReason))
        {
            throw new ArgumentOutOfRangeException(nameof(faultReason));
        }

        lock (gate)
        {
            if (disposed
                || expectedGeneration != generation
                || desiredBackend != ModelRenderingBackendPreference.Gpu
                || activeBackend != ModelRenderingBackendPreference.Cpu
                || !gpuUploadInProgress)
            {
                return false;
            }

            gpuUploadInProgress = false;
            gpuRetryBlockedGeneration = expectedGeneration;
            lastFaultReason = faultReason;
            status = new ModelRenderingBackendStatus(
                ModelRenderingBackendState.Cpu,
                faultReason,
                framesPerSecond: null);
            return true;
        }
    }

    internal bool TryBeginCpuRebuild(long expectedGeneration)
    {
        lock (gate)
        {
            if (disposed
                || expectedGeneration != generation
                || desiredBackend != ModelRenderingBackendPreference.Cpu
                || activeBackend != ModelRenderingBackendPreference.Gpu
                || cpuRetryBlockedGeneration == expectedGeneration
                || cpuRebuildInProgress)
            {
                return false;
            }

            cpuRebuildInProgress = true;
            gpuUploadInProgress = false;
            status = new ModelRenderingBackendStatus(
                ModelRenderingBackendState.SwitchingToCpu,
                lastFaultReason: null,
                framesPerSecond: null);
            return true;
        }
    }

    internal bool TryCommitCpu(long expectedGeneration)
    {
        lock (gate)
        {
            if (disposed
                || expectedGeneration != generation
                || desiredBackend != ModelRenderingBackendPreference.Cpu
                || activeBackend != ModelRenderingBackendPreference.Gpu
                || !cpuRebuildInProgress)
            {
                return false;
            }

            activeBackend = ModelRenderingBackendPreference.Cpu;
            gpuRenderingAvailable = false;
            cpuRebuildInProgress = false;
            cpuRetryBlockedGeneration = null;
            status = ModelRenderingBackendStatus.Cpu;
            return true;
        }
    }

    internal bool TryBeginCpuFallback(
        long expectedGeneration,
        ModelRenderingBackendFaultReason faultReason)
    {
        if (!Enum.IsDefined(faultReason))
        {
            throw new ArgumentOutOfRangeException(nameof(faultReason));
        }

        lock (gate)
        {
            if (disposed
                || expectedGeneration != generation
                || activeBackend != ModelRenderingBackendPreference.Gpu
                || cpuRetryBlockedGeneration == expectedGeneration
                || cpuRebuildInProgress)
            {
                return false;
            }

            cpuRebuildInProgress = true;
            cpuFallbackInProgress = true;
            gpuUploadInProgress = false;
            gpuRetryBlockedGeneration = expectedGeneration;
            lastFaultReason = faultReason;
            gpuRenderingAvailable = false;
            status = new ModelRenderingBackendStatus(
                ModelRenderingBackendState.SwitchingToCpu,
                faultReason,
                framesPerSecond: null);
            return true;
        }
    }

    internal bool TryCommitCpuFallback(long expectedGeneration)
    {
        lock (gate)
        {
            if (disposed
                || expectedGeneration != generation
                || activeBackend != ModelRenderingBackendPreference.Gpu
                || !cpuRebuildInProgress
                || !cpuFallbackInProgress
                || gpuRetryBlockedGeneration != expectedGeneration)
            {
                return false;
            }

            activeBackend = ModelRenderingBackendPreference.Cpu;
            gpuRenderingAvailable = false;
            cpuRebuildInProgress = false;
            cpuFallbackInProgress = false;
            cpuRetryBlockedGeneration = null;
            status = new ModelRenderingBackendStatus(
                ModelRenderingBackendState.Cpu,
                lastFaultReason,
                framesPerSecond: null);
            return true;
        }
    }

    internal bool FailCpuRebuild(long expectedGeneration)
    {
        lock (gate)
        {
            if (disposed
                || expectedGeneration != generation
                || activeBackend != ModelRenderingBackendPreference.Gpu
                || !cpuRebuildInProgress)
            {
                return false;
            }

            cpuRebuildInProgress = false;
            cpuFallbackInProgress = false;
            cpuRetryBlockedGeneration = expectedGeneration;
            lastFaultReason = ModelRenderingBackendFaultReason.CpuTextureRebuildFailed;
            status = new ModelRenderingBackendStatus(
                ModelRenderingBackendState.Gpu,
                lastFaultReason,
                framesPerSecond: null);
            return true;
        }
    }

    internal void BeginDispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            generation++;
            gpuUploadInProgress = false;
            cpuRebuildInProgress = false;
            cpuFallbackInProgress = false;
            gpuRenderingAvailable = false;
            cpuRetryBlockedGeneration = null;
        }
    }

    private ModelRenderingBackendStatus CreateStatus()
    {
        if (activeBackend == ModelRenderingBackendPreference.Gpu && !gpuRenderingAvailable)
        {
            return new ModelRenderingBackendStatus(
                ModelRenderingBackendState.SwitchingToCpu,
                lastFaultReason,
                framesPerSecond: null);
        }

        if (activeBackend == ModelRenderingBackendPreference.Cpu)
        {
            if (desiredBackend == ModelRenderingBackendPreference.Gpu
                && gpuRetryBlockedGeneration == generation)
            {
                return new ModelRenderingBackendStatus(
                    ModelRenderingBackendState.Cpu,
                    lastFaultReason,
                    framesPerSecond: null);
            }

            return desiredBackend == ModelRenderingBackendPreference.Cpu
                ? ModelRenderingBackendStatus.Cpu
                : new ModelRenderingBackendStatus(
                    ModelRenderingBackendState.SwitchingToGpu,
                    lastFaultReason: null,
                    framesPerSecond: null);
        }

        return desiredBackend == ModelRenderingBackendPreference.Gpu
            ? new ModelRenderingBackendStatus(
                ModelRenderingBackendState.Gpu,
                lastFaultReason: null,
                framesPerSecond: null)
            : new ModelRenderingBackendStatus(
                ModelRenderingBackendState.SwitchingToCpu,
                lastFaultReason: null,
                framesPerSecond: null);
    }
}
