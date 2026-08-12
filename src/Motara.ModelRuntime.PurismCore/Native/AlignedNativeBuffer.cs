using System.Runtime.InteropServices;

namespace Motara.ModelRuntime.PurismCore;

internal sealed unsafe class AlignedNativeBuffer : SafeHandle
{
    private AlignedNativeBuffer(void* pointer)
        : base(0, ownsHandle: true)
    {
        SetHandle((nint)pointer);
    }

    public override bool IsInvalid => handle == 0;

    internal nint Pointer => handle;

    internal static AlignedNativeBuffer Allocate(nuint byteCount, nuint alignment)
    {
        ArgumentOutOfRangeException.ThrowIfZero(byteCount);
        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment));
        }

        void* pointer = NativeMemory.AlignedAlloc(byteCount, alignment);
        if (pointer is null)
        {
            throw new InvalidOperationException("Aligned native memory allocation failed.");
        }

        return new AlignedNativeBuffer(pointer);
    }

    protected override bool ReleaseHandle()
    {
        NativeMemory.AlignedFree((void*)handle);
        handle = 0;
        return true;
    }
}
