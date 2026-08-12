using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Motara.App.Rendering;

internal sealed class WindowsGpuWorkerPolicy
{
    private readonly Action apply;

    internal WindowsGpuWorkerPolicy()
        : this(ApplyWindowsPolicy)
    {
    }

    internal WindowsGpuWorkerPolicy(Action apply) =>
        this.apply = apply ?? throw new ArgumentNullException(nameof(apply));

    internal void ApplyCurrentThread() => apply();

    private static void ApplyWindowsPolicy()
    {
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var state = new ThreadPowerThrottlingState
        {
            Version = 1,
            ControlMask = ThreadPowerThrottlingExecutionSpeed,
            StateMask = 0,
        };
        if (!SetThreadInformation(
                GetCurrentThread(),
                ThreadInformationClass.ThreadPowerThrottling,
                ref state,
                Marshal.SizeOf<ThreadPowerThrottlingState>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private const uint ThreadPowerThrottlingExecutionSpeed = 0x1;

    private enum ThreadInformationClass
    {
        ThreadPowerThrottling = 3,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadPowerThrottlingState
    {
        internal uint Version;
        internal uint ControlMask;
        internal uint StateMask;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadInformation(
        nint thread,
        ThreadInformationClass informationClass,
        ref ThreadPowerThrottlingState information,
        int informationSize);
}
