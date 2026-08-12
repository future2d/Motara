using System.Runtime.InteropServices;
using System.Text;

namespace Motara.Media.Spout2;

internal readonly record struct Spout2ReceivedFrame(
    int Width,
    int Height,
    bool HasAlpha,
    ReadOnlyMemory<byte> Pixels,
    TimeSpan Timestamp);

internal interface ISpout2ReceiverSession : IDisposable
{
    bool TryReceive(out Spout2ReceivedFrame frame);
}

internal interface ISpout2SenderSession : IDisposable
{
    bool TrySend(SignalFrame frame);
}

internal interface ISpout2Interop : IDisposable
{
    bool IsAvailable { get; }
    IReadOnlyList<VideoSignalSourceDescriptor> EnumerateSenders();
    bool TryOpenReceiver(string senderId, out ISpout2ReceiverSession session, out string? errorType);
    bool TryOpenSender(VideoSignalOutputOptions options, out ISpout2SenderSession session, out string? errorType);
}

internal sealed class Spout2NativeInterop : ISpout2Interop
{
    private NativeBridge? bridge;

    public bool IsAvailable => GetBridge() is not null;

    public IReadOnlyList<VideoSignalSourceDescriptor> EnumerateSenders()
    {
        NativeBridge? native = GetBridge();
        if (native is null)
        {
            return [];
        }

        nint handle = native.Create();
        if (handle == 0)
        {
            return [];
        }

        try
        {
            int count = native.SenderCount(handle);
            var sources = new List<VideoSignalSourceDescriptor>(Math.Max(0, count));
            for (int index = 0; index < count; index++)
            {
                var name = new byte[256];
                if (native.SenderInfo(handle, index, name, name.Length, out uint width, out uint height, out double fps) == 0)
                {
                    continue;
                }

                string id = Encoding.ASCII.GetString(name).TrimEnd('\0');
                if (id.Length == 0 || width == 0 || height == 0)
                {
                    continue;
                }

                sources.Add(new VideoSignalSourceDescriptor(
                    VideoSignalProtocol.Spout2,
                    id,
                    id,
                    checked((int)width),
                    checked((int)height),
                    double.IsFinite(fps) && fps > 0 ? fps : 0,
                    HasAlpha: true));
            }

            return sources;
        }
        finally
        {
            native.Destroy(handle);
        }
    }

    public bool TryOpenReceiver(string senderId, out ISpout2ReceiverSession session, out string? errorType)
    {
        NativeBridge? native = GetBridge();
        if (native is null)
        {
            session = null!;
            errorType = "RuntimeMissing";
            return false;
        }

        nint handle = native.Create();
        if (handle == 0 || native.ReceiverOpen(handle, senderId, out uint width, out uint height) == 0 || width == 0 || height == 0)
        {
            if (handle != 0) native.Destroy(handle);
            session = null!;
            errorType = "ReceiverOpenFailed";
            return false;
        }

        session = new Spout2ReceiverSession(native, handle, checked((int)width), checked((int)height));
        errorType = null;
        return true;
    }

    public bool TryOpenSender(VideoSignalOutputOptions options, out ISpout2SenderSession session, out string? errorType)
    {
        NativeBridge? native = GetBridge();
        if (native is null)
        {
            session = null!;
            errorType = "RuntimeMissing";
            return false;
        }

        nint handle = native.Create();
        if (handle == 0 || native.SenderOpen(handle, options.Name, checked((uint)options.Width), checked((uint)options.Height)) == 0)
        {
            if (handle != 0) native.Destroy(handle);
            session = null!;
            errorType = "SenderOpenFailed";
            return false;
        }

        session = new Spout2SenderSession(native, handle);
        errorType = null;
        return true;
    }

    public void Dispose()
    {
        NativeBridge? native = Interlocked.Exchange(ref bridge, null);
        native?.Dispose();
    }

    private NativeBridge? GetBridge()
    {
        NativeBridge? current = Volatile.Read(ref bridge);
        if (current is not null)
        {
            return current;
        }

        NativeBridge? loaded = NativeBridge.TryLoad();
        if (loaded is null)
        {
            return null;
        }

        NativeBridge? existing = Interlocked.CompareExchange(ref bridge, loaded, null);
        if (existing is not null)
        {
            loaded.Dispose();
            return existing;
        }

        return loaded;
    }

    private sealed class Spout2ReceiverSession(
        NativeBridge native,
        nint handle,
        int width,
        int height) : ISpout2ReceiverSession
    {
        private readonly byte[] buffer = new byte[checked(width * height * 4)];
        private nint nativeHandle = handle;

        public bool TryReceive(out Spout2ReceivedFrame frame)
        {
            nint active = Volatile.Read(ref nativeHandle);
            if (active == 0 || native.ReceiverReceive(active, buffer, buffer.Length, out int isNewFrame) <= 0 || isNewFrame == 0)
            {
                frame = default;
                return false;
            }

            frame = new Spout2ReceivedFrame(width, height, true, buffer, TimeSpan.Zero);
            return true;
        }

        public void Dispose()
        {
            nint active = Interlocked.Exchange(ref nativeHandle, 0);
            if (active != 0)
            {
                native.ReceiverClose(active);
                native.Destroy(active);
            }
        }
    }

    private sealed class Spout2SenderSession(NativeBridge native, nint handle) : ISpout2SenderSession
    {
        private nint nativeHandle = handle;

        public bool TrySend(SignalFrame frame)
        {
            nint active = Volatile.Read(ref nativeHandle);
            return active != 0
                && !frame.Pixels.IsEmpty
                && native.SenderSend(
                    active,
                    frame.Pixels.Span,
                    checked((uint)frame.Metadata.Width),
                    checked((uint)frame.Metadata.Height)) != 0;
        }

        public void Dispose()
        {
            nint active = Interlocked.Exchange(ref nativeHandle, 0);
            if (active != 0)
            {
                native.SenderClose(active);
                native.Destroy(active);
            }
        }
    }

    private sealed class NativeBridge : IDisposable
    {
        private nint library;
        private readonly CreateDelegate create;
        private readonly DestroyDelegate destroy;
        private readonly SenderCountDelegate senderCount;
        private readonly SenderInfoDelegate senderInfo;
        private readonly ReceiverOpenDelegate receiverOpen;
        private readonly ReceiverReceiveDelegate receiverReceive;
        private readonly SenderOpenDelegate senderOpen;
        private readonly SenderSendDelegate senderSend;
        private readonly CloseDelegate receiverClose;
        private readonly CloseDelegate senderClose;

        private NativeBridge(nint library)
        {
            this.library = library;
            create = Load<CreateDelegate>("motara_spout2_create");
            destroy = Load<DestroyDelegate>("motara_spout2_destroy");
            senderCount = Load<SenderCountDelegate>("motara_spout2_sender_count");
            senderInfo = Load<SenderInfoDelegate>("motara_spout2_sender_info");
            receiverOpen = Load<ReceiverOpenDelegate>("motara_spout2_receiver_open");
            receiverReceive = Load<ReceiverReceiveDelegate>("motara_spout2_receiver_receive");
            senderOpen = Load<SenderOpenDelegate>("motara_spout2_sender_open");
            senderSend = Load<SenderSendDelegate>("motara_spout2_sender_send");
            receiverClose = Load<CloseDelegate>("motara_spout2_receiver_close");
            senderClose = Load<CloseDelegate>("motara_spout2_sender_close");
        }

        public static NativeBridge? TryLoad()
        {
            string? configuredPath = Environment.GetEnvironmentVariable("MOTARA_SPOUT2_NATIVE_DLL");
            IEnumerable<string> candidates = string.IsNullOrWhiteSpace(configuredPath)
                ? [Path.Combine(AppContext.BaseDirectory, "Motara.Spout2.Native.dll"), "Motara.Spout2.Native.dll"]
                : [configuredPath];
            foreach (string candidate in candidates)
            {
                if (!NativeLibrary.TryLoad(candidate, out nint library))
                {
                    continue;
                }

                try
                {
                    return new NativeBridge(library);
                }
                catch
                {
                    NativeLibrary.Free(library);
                }
            }

            return null;
        }

        public nint Create() => create();
        public void Destroy(nint handle) => destroy(handle);
        public int SenderCount(nint handle) => senderCount(handle);
        public int SenderInfo(nint handle, int index, byte[] name, int capacity, out uint width, out uint height, out double fps) => senderInfo(handle, index, name, capacity, out width, out height, out fps);
        public int ReceiverOpen(nint handle, string senderName, out uint width, out uint height) => receiverOpen(handle, senderName, out width, out height);
        public int ReceiverReceive(nint handle, byte[] pixels, int capacity, out int isNewFrame) => receiverReceive(handle, pixels, capacity, out isNewFrame);
        public int SenderOpen(nint handle, string name, uint width, uint height) => senderOpen(handle, name, width, height);
        public int SenderSend(nint handle, ReadOnlySpan<byte> pixels, uint width, uint height)
        {
            byte[] copy = pixels.ToArray();
            return senderSend(handle, copy, width, height);
        }
        public void ReceiverClose(nint handle) => receiverClose(handle);
        public void SenderClose(nint handle) => senderClose(handle);

        public void Dispose()
        {
            nint loaded = Interlocked.Exchange(ref library, 0);
            if (loaded != 0) NativeLibrary.Free(loaded);
        }

        private T Load<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint CreateDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyDelegate(nint handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SenderCountDelegate(nint handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SenderInfoDelegate(nint handle, int index, byte[] name, int capacity, out uint width, out uint height, out double fps);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReceiverOpenDelegate(nint handle, [MarshalAs(UnmanagedType.LPStr)] string name, out uint width, out uint height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReceiverReceiveDelegate(nint handle, byte[] pixels, int capacity, out int isNewFrame);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SenderOpenDelegate(nint handle, [MarshalAs(UnmanagedType.LPStr)] string name, uint width, uint height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SenderSendDelegate(nint handle, byte[] pixels, uint width, uint height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CloseDelegate(nint handle);
    }
}
