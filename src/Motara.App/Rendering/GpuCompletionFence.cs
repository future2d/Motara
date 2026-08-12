using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace Motara.App.Rendering;

internal interface IGpuCompletionFence : IDisposable
{
    bool IsSignaled { get; }
}

internal static class GpuCompositionFrameSynchronizer
{
    internal static IGpuCompletionFence? Submit(
        Action<bool, bool> flush,
        Func<IGpuCompletionFence?> createFence,
        Action submitFence)
    {
        ArgumentNullException.ThrowIfNull(flush);
        ArgumentNullException.ThrowIfNull(createFence);
        ArgumentNullException.ThrowIfNull(submitFence);

        flush(true, false);
        IGpuCompletionFence? fence = createFence();
        if (fence is null)
        {
            flush(true, true);
            return null;
        }

        try
        {
            submitFence();
        }
        catch
        {
            fence.Dispose();
            throw;
        }

        return fence;
    }
}

internal sealed class OpenGlGpuCompletionFenceFactory
{
    private const uint SyncGpuCommandsComplete = 0x9117;
    private const uint AlreadySignaled = 0x911A;
    private const uint ConditionSatisfied = 0x911C;
    private const uint WaitFailed = 0x911D;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr FenceSyncDelegate(uint condition, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ClientWaitSyncDelegate(IntPtr sync, uint flags, ulong timeout);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void DeleteSyncDelegate(IntPtr sync);

    private readonly FenceSyncDelegate fenceSync;
    private readonly ClientWaitSyncDelegate clientWaitSync;
    private readonly DeleteSyncDelegate deleteSync;

    private OpenGlGpuCompletionFenceFactory(
        FenceSyncDelegate fenceSync,
        ClientWaitSyncDelegate clientWaitSync,
        DeleteSyncDelegate deleteSync)
    {
        this.fenceSync = fenceSync;
        this.clientWaitSync = clientWaitSync;
        this.deleteSync = deleteSync;
    }

    internal static OpenGlGpuCompletionFenceFactory? TryCreate(GlInterface glInterface)
    {
        ArgumentNullException.ThrowIfNull(glInterface);
        IntPtr fencePointer = glInterface.GetProcAddress("glFenceSync");
        IntPtr waitPointer = glInterface.GetProcAddress("glClientWaitSync");
        IntPtr deletePointer = glInterface.GetProcAddress("glDeleteSync");
        if (fencePointer == IntPtr.Zero
            || waitPointer == IntPtr.Zero
            || deletePointer == IntPtr.Zero)
        {
            return null;
        }

        return new OpenGlGpuCompletionFenceFactory(
            Marshal.GetDelegateForFunctionPointer<FenceSyncDelegate>(fencePointer),
            Marshal.GetDelegateForFunctionPointer<ClientWaitSyncDelegate>(waitPointer),
            Marshal.GetDelegateForFunctionPointer<DeleteSyncDelegate>(deletePointer));
    }

    internal IGpuCompletionFence? CreateFence()
    {
        IntPtr sync = fenceSync(SyncGpuCommandsComplete, 0);
        return sync == IntPtr.Zero
            ? null
            : new Fence(sync, clientWaitSync, deleteSync);
    }

    private sealed class Fence(
        IntPtr sync,
        ClientWaitSyncDelegate clientWaitSync,
        DeleteSyncDelegate deleteSync) : IGpuCompletionFence
    {
        private IntPtr sync = sync;

        public bool IsSignaled
        {
            get
            {
                if (sync == IntPtr.Zero)
                {
                    return true;
                }

                uint result = clientWaitSync(sync, 0, 0);
                if (result == AlreadySignaled || result == ConditionSatisfied)
                {
                    DeleteNativeFence();
                    return true;
                }

                if (result == WaitFailed)
                {
                    throw new InvalidOperationException("OpenGL GPU completion fence wait failed.");
                }

                return false;
            }
        }

        public void Dispose() => DeleteNativeFence();

        private void DeleteNativeFence()
        {
            IntPtr nativeSync = Interlocked.Exchange(ref sync, IntPtr.Zero);
            if (nativeSync != IntPtr.Zero)
            {
                deleteSync(nativeSync);
            }
        }
    }
}
