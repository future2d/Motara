using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Motara.Tracking.Abstractions;

namespace Motara.App.Tracking;

internal static class MediaPipeNativePaths
{
    internal const string NativeFileName = "Motara.MediaPipe.Native.dll";
    internal const string ModelFileName = "face_landmarker.task";

    internal static string ResolveLibraryPath(string baseDirectory, string? explicitPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        string candidate = !string.IsNullOrWhiteSpace(explicitPath)
            ? explicitPath
            : Path.Combine(baseDirectory, "tracking", "MediaPipe", NativeFileName);
        return Path.GetFullPath(candidate);
    }

    internal static string ResolveModelPath(string baseDirectory, string? explicitPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        string candidate = !string.IsNullOrWhiteSpace(explicitPath)
            ? explicitPath
            : Path.Combine(baseDirectory, "tracking", "MediaPipe", ModelFileName);
        return Path.GetFullPath(candidate);
    }

    internal static TrackingSourceAvailability CheckAvailability(string libraryPath, string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!File.Exists(libraryPath))
        {
            return TrackingSourceAvailability.Unavailable("tracking.mediapipe.native_missing");
        }

        if (!File.Exists(modelPath))
        {
            return TrackingSourceAvailability.Unavailable("tracking.mediapipe.model_missing");
        }

        return TrackingSourceAvailability.Available;
    }
}

internal readonly record struct MediaPipeBlendshapeValue(int Index, float Score);

internal static class MediaPipeBlendshapeMapper
{
    internal static double[] Map(
        IReadOnlyList<MediaPipeBlendshapeValue> blendshapes,
        int slotCount)
    {
        ArgumentNullException.ThrowIfNull(blendshapes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCount);
        var values = new double[slotCount];
        foreach (MediaPipeBlendshapeValue blendshape in blendshapes)
        {
            if ((uint)blendshape.Index >= (uint)values.Length || !float.IsFinite(blendshape.Score))
            {
                continue;
            }

            values[blendshape.Index] = Math.Clamp(blendshape.Score, 0f, 1f);
        }

        return values;
    }
}

internal sealed record MediaPipeNativeFrame(
    bool FaceDetected,
    ImmutableArray<MediaPipeBlendshapeValue> Blendshapes);

internal sealed class MediaPipeNativeLibrary : IDisposable
{
    private readonly nint libraryHandle;
    private readonly CreateDelegate create;
    private readonly ProcessRgbaDelegate processRgba;
    private readonly FreeErrorDelegate freeError;
    private readonly CloseDelegate close;
    private int disposed;

    private MediaPipeNativeLibrary(
        nint libraryHandle,
        CreateDelegate create,
        ProcessRgbaDelegate processRgba,
        FreeErrorDelegate freeError,
        CloseDelegate close)
    {
        this.libraryHandle = libraryHandle;
        this.create = create;
        this.processRgba = processRgba;
        this.freeError = freeError;
        this.close = close;
    }

    internal static bool TryLoad(
        string libraryPath,
        out MediaPipeNativeLibrary? library,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        library = null;
        error = null;
        nint handle;
        try
        {
            handle = NativeLibrary.Load(libraryPath);
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or BadImageFormatException
            or FileNotFoundException)
        {
            error = exception.Message;
            return false;
        }

        try
        {
            library = new MediaPipeNativeLibrary(
                handle,
                LoadExport<CreateDelegate>(handle, "motara_mp_create"),
                LoadExport<ProcessRgbaDelegate>(handle, "motara_mp_process_rgba"),
                LoadExport<FreeErrorDelegate>(handle, "motara_mp_free_error"),
                LoadExport<CloseDelegate>(handle, "motara_mp_close"));
            return true;
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException
            or ArgumentException)
        {
            NativeLibrary.Free(handle);
            error = exception.Message;
            return false;
        }
    }

    internal MediaPipeNativeSession CreateSession(string modelPath)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        nint errorPointer = 0;
        nint sessionHandle = create(modelPath, out errorPointer);
        string? error = TakeError(errorPointer);
        if (sessionHandle == 0)
        {
            throw new InvalidOperationException(error ?? "MediaPipe native session creation failed.");
        }

        return new MediaPipeNativeSession(this, sessionHandle);
    }

    internal MediaPipeNativeFrame Process(
        nint sessionHandle,
        ReadOnlyMemory<byte> rgba,
        int width,
        int height,
        long timestampMilliseconds)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (width <= 0 || height <= 0 || rgba.Length < checked(width * height * 4))
        {
            throw new ArgumentOutOfRangeException(nameof(rgba), "RGBA buffer does not match the frame dimensions.");
        }

        byte[] buffer = rgba.ToArray();
        GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var nativeFrame = new NativeFrame
            {
                Blendshapes = 0,
                BlendshapeCapacity = 52,
                BlendshapeCount = 0,
                FaceDetected = 0,
            };
            nint blendshapeBuffer = Marshal.AllocHGlobal(
                Marshal.SizeOf<NativeBlendshape>() * nativeFrame.BlendshapeCapacity);
            try
            {
                nativeFrame.Blendshapes = blendshapeBuffer;
                nint errorPointer = 0;
                int status = processRgba(
                    sessionHandle,
                    pinned.AddrOfPinnedObject(),
                    width,
                    height,
                    timestampMilliseconds,
                    ref nativeFrame,
                    out errorPointer);
                string? error = TakeError(errorPointer);
                if (status != 0)
                {
                    throw new InvalidOperationException(error ?? "MediaPipe native frame processing failed.");
                }

                int count = Math.Clamp(nativeFrame.BlendshapeCount, 0, nativeFrame.BlendshapeCapacity);
                var values = ImmutableArray.CreateBuilder<MediaPipeBlendshapeValue>(count);
                int stride = Marshal.SizeOf<NativeBlendshape>();
                for (int index = 0; index < count; index++)
                {
                    NativeBlendshape value = Marshal.PtrToStructure<NativeBlendshape>(
                        blendshapeBuffer + (index * stride));
                    values.Add(new MediaPipeBlendshapeValue(value.Index, value.Score));
                }

                return new MediaPipeNativeFrame(nativeFrame.FaceDetected != 0, values.MoveToImmutable());
            }
            finally
            {
                Marshal.FreeHGlobal(blendshapeBuffer);
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    internal void CloseSession(nint sessionHandle)
    {
        if (sessionHandle == 0 || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        nint errorPointer = 0;
        int status = close(sessionHandle, out errorPointer);
        string? error = TakeError(errorPointer);
        if (status != 0)
        {
            throw new InvalidOperationException(error ?? "MediaPipe native session close failed.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        NativeLibrary.Free(libraryHandle);
    }

    private string? TakeError(nint pointer)
    {
        if (pointer == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringAnsi(pointer);
        }
        finally
        {
            freeError(pointer);
        }
    }

    private static TDelegate LoadExport<TDelegate>(nint handle, string name)
        where TDelegate : Delegate
    {
        nint address = NativeLibrary.GetExport(handle, name);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBlendshape
    {
        internal int Index;
        internal float Score;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFrame
    {
        internal nint Blendshapes;
        internal int BlendshapeCapacity;
        internal int BlendshapeCount;
        internal int FaceDetected;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CreateDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string modelPath,
        out nint errorMessage);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ProcessRgbaDelegate(
        nint handle,
        nint rgba,
        int width,
        int height,
        long timestampMilliseconds,
        ref NativeFrame output,
        out nint errorMessage);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeErrorDelegate(nint errorMessage);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CloseDelegate(nint handle, out nint errorMessage);
}

internal sealed class MediaPipeNativeSession : IDisposable
{
    private readonly MediaPipeNativeLibrary library;
    private nint handle;

    internal MediaPipeNativeSession(MediaPipeNativeLibrary library, nint handle)
    {
        this.library = library ?? throw new ArgumentNullException(nameof(library));
        this.handle = handle;
    }

    internal MediaPipeNativeFrame Process(
        ReadOnlyMemory<byte> rgba,
        int width,
        int height,
        long timestampMilliseconds)
    {
        nint currentHandle = Volatile.Read(ref handle);
        ObjectDisposedException.ThrowIf(currentHandle == 0, this);
        return library.Process(currentHandle, rgba, width, height, timestampMilliseconds);
    }

    public void Dispose()
    {
        nint currentHandle = Interlocked.Exchange(ref handle, 0);
        if (currentHandle == 0)
        {
            return;
        }

        library.CloseSession(currentHandle);
    }
}
