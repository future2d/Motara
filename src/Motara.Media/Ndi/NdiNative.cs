using System.Runtime.InteropServices;
using System.Text;

namespace Motara.Media.Ndi;

internal readonly record struct NdiReceivedFrame(int Width, int Height, bool HasAlpha, ReadOnlyMemory<byte> Pixels, TimeSpan Timestamp);
internal interface INdiReceiverSession : IDisposable { bool TryReceive(out NdiReceivedFrame frame); }
internal interface INdiSenderSession : IDisposable { bool TrySend(SignalFrame frame); }
internal interface INdiInterop : IDisposable
{
    bool IsAvailable { get; }
    IReadOnlyList<VideoSignalSourceDescriptor> EnumerateSources();
    bool TryOpenReceiver(string sourceId, out INdiReceiverSession session, out string? errorType);
    bool TryOpenSender(VideoSignalOutputOptions options, out INdiSenderSession session, out string? errorType);
}

public sealed record NdiRuntimeProbeResult(bool IsAvailable, string? ErrorType);

public sealed class NdiRuntimeProbe
{
    private static readonly string[] requiredExports =
    [
        "NDIlib_initialize", "NDIlib_destroy", "NDIlib_find_create_v2", "NDIlib_find_get_current_sources",
        "NDIlib_find_destroy", "NDIlib_recv_create_v3", "NDIlib_recv_capture_v3", "NDIlib_recv_destroy",
        "NDIlib_send_create", "NDIlib_send_send_video_v2", "NDIlib_send_destroy",
    ];

    public static NdiRuntimeProbeResult Probe()
    {
        if (!OperatingSystem.IsWindows()) return new(false, "WindowsOnly");
        foreach (string candidate in Candidates())
        {
            if (!NativeLibrary.TryLoad(candidate, out nint handle)) continue;
            try
            {
                foreach (string export in requiredExports) _ = NativeLibrary.GetExport(handle, export);
                return new(true, null);
            }
            catch (EntryPointNotFoundException) { }
            finally { NativeLibrary.Free(handle); }
        }
        return new(false, "RuntimeMissingOrIncompatible");
    }

    internal static IEnumerable<string> Candidates()
    {
        string? configured = Environment.GetEnvironmentVariable("MOTARA_NDI_RUNTIME_DLL");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return [configured];
        }

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Processing.NDI.Lib.x64.dll"),
            "Processing.NDI.Lib.x64.dll",
            "Processing.NDI.Lib.dll",
        };
        foreach (string root in StandardInstallRoots())
        {
            candidates.Add(Path.Combine(root, "NDI", "NDI 6 Runtime", "v6", "Processing.NDI.Lib.x64.dll"));
            candidates.Add(Path.Combine(root, "NDI", "NDI 5 Runtime", "v5", "Processing.NDI.Lib.x64.dll"));
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> StandardInstallRoots()
    {
        string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return programFiles;
        }

        string? programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86)
            && !StringComparer.OrdinalIgnoreCase.Equals(programFiles, programFilesX86))
        {
            yield return programFilesX86;
        }
    }
}

internal sealed class NdiNativeInterop : INdiInterop
{
    private NativeBridge? bridge;
    public bool IsAvailable => GetBridge()?.RuntimeAvailable == true;

    public IReadOnlyList<VideoSignalSourceDescriptor> EnumerateSources()
    {
        NativeBridge? native = GetBridge();
        if (native is null) return [];
        nint discoveryHandle = native.Create();
        if (discoveryHandle == 0) return [];
        int count;
        try { count = native.SourceCount(discoveryHandle); }
        catch { native.Destroy(discoveryHandle); return []; }
        var result = new List<VideoSignalSourceDescriptor>(Math.Max(0, count));
        for (int i = 0; i < count; i++)
        {
            byte[] buffer = new byte[512];
            if (native.SourceInfo(discoveryHandle, i, buffer, buffer.Length) == 0) continue;
            string name = Encoding.UTF8.GetString(buffer).TrimEnd('\0');
            // NDI discovery exposes names only. The actual frame dimensions
            // are learned from the first captured video frame.
            if (name.Length != 0) result.Add(new(VideoSignalProtocol.Ndi, name, name, 0, 0, 0, true));
        }
        native.Destroy(discoveryHandle);
        return result;
    }

    public bool TryOpenReceiver(string sourceId, out INdiReceiverSession session, out string? errorType)
    {
        NativeBridge? native = GetBridge();
        nint bridgeHandle = native?.Create() ?? 0;
        nint receiverHandle = bridgeHandle == 0 ? 0 : native!.ReceiverOpen(bridgeHandle, sourceId);
        if (receiverHandle == 0)
        {
            if (bridgeHandle != 0) native!.Destroy(bridgeHandle);
            session = null!; errorType = native is null || bridgeHandle == 0 ? "RuntimeMissing" : "ReceiverOpenFailed"; return false;
        }
        session = new ReceiverSession(native!, bridgeHandle, receiverHandle);
        errorType = null; return true;
    }

    public bool TryOpenSender(VideoSignalOutputOptions options, out INdiSenderSession session, out string? errorType)
    {
        NativeBridge? native = GetBridge();
        nint bridgeHandle = native?.Create() ?? 0;
        nint handle = bridgeHandle == 0 ? 0 : native!.SenderOpen(bridgeHandle, options.Name);
        if (handle == 0) { if (bridgeHandle != 0) native!.Destroy(bridgeHandle); session = null!; errorType = native is null || bridgeHandle == 0 ? "RuntimeMissing" : "SenderOpenFailed"; return false; }
        session = new SenderSession(native!, bridgeHandle, handle); errorType = null; return true;
    }

    public void Dispose() => Interlocked.Exchange(ref bridge, null)?.Dispose();

    private NativeBridge? GetBridge()
    {
        NativeBridge? current = Volatile.Read(ref bridge);
        if (current is not null) return current;
        NativeBridge? loaded = NativeBridge.TryLoad();
        if (loaded is null) return null;
        NativeBridge? existing = Interlocked.CompareExchange(ref bridge, loaded, null);
        if (existing is not null) { loaded.Dispose(); return existing; }
        return loaded;
    }

    private sealed class ReceiverSession(NativeBridge native, nint bridgeHandle, nint handle) : INdiReceiverSession
    {
        private byte[] buffer = new byte[1920 * 1080 * 4];
        private nint active = handle;
        public bool TryReceive(out NdiReceivedFrame frame)
        {
            nint current = Volatile.Read(ref active);
            if (current == 0) { frame = default; return false; }
            int result = native.ReceiverReceive(bridgeHandle, current, buffer, buffer.Length, out int width, out int height, out int isNew);
            if (result < 0) { buffer = new byte[checked(-result)]; result = native.ReceiverReceive(bridgeHandle, current, buffer, buffer.Length, out width, out height, out isNew); }
            if (result <= 0 || isNew == 0) { frame = default; return false; }
            frame = new(width, height, true, buffer.AsMemory(0, result), TimeSpan.Zero); return true;
        }
        public void Dispose() { nint current = Interlocked.Exchange(ref active, 0); if (current != 0) native.ReceiverClose(bridgeHandle, current); if (bridgeHandle != 0) native.Destroy(bridgeHandle); }
    }

    private sealed class SenderSession(NativeBridge native, nint bridgeHandle, nint handle) : INdiSenderSession
    {
        private nint active = handle;
        public bool TrySend(SignalFrame frame)
        {
            nint current = Volatile.Read(ref active);
            return current != 0 && !frame.Pixels.IsEmpty && native.SenderSend(bridgeHandle, current, frame.Pixels.ToArray(), frame.Metadata.Width, frame.Metadata.Height, frame.Metadata.Width * 4) != 0;
        }
        public void Dispose() { nint current = Interlocked.Exchange(ref active, 0); if (current != 0) native.SenderClose(bridgeHandle, current); if (bridgeHandle != 0) native.Destroy(bridgeHandle); }
    }

    private sealed class NativeBridge : IDisposable
    {
        private nint library;
        private readonly CreateDelegate create; private readonly DestroyDelegate destroy; private readonly SourceCountDelegate sourceCount; private readonly SourceInfoDelegate sourceInfo;
        private readonly ReceiverOpenDelegate receiverOpen; private readonly ReceiverReceiveDelegate receiverReceive; private readonly ReceiverCloseDelegate receiverClose;
        private readonly SenderOpenDelegate senderOpen; private readonly SenderSendDelegate senderSend; private readonly SenderCloseDelegate senderClose;
        private NativeBridge(nint library)
        {
            this.library = library; create = Load<CreateDelegate>("motara_ndi_create"); destroy = Load<DestroyDelegate>("motara_ndi_destroy"); sourceCount = Load<SourceCountDelegate>("motara_ndi_source_count"); sourceInfo = Load<SourceInfoDelegate>("motara_ndi_source_info"); receiverOpen = Load<ReceiverOpenDelegate>("motara_ndi_receiver_open"); receiverReceive = Load<ReceiverReceiveDelegate>("motara_ndi_receiver_receive"); receiverClose = Load<ReceiverCloseDelegate>("motara_ndi_receiver_close"); senderOpen = Load<SenderOpenDelegate>("motara_ndi_sender_open"); senderSend = Load<SenderSendDelegate>("motara_ndi_sender_send"); senderClose = Load<SenderCloseDelegate>("motara_ndi_sender_close");
        }
        public static NativeBridge? TryLoad()
        {
            string? configured = Environment.GetEnvironmentVariable("MOTARA_NDI_NATIVE_DLL");
            IEnumerable<string> candidates = string.IsNullOrWhiteSpace(configured) ? [Path.Combine(AppContext.BaseDirectory, "Motara.Ndi.Native.dll"), "Motara.Ndi.Native.dll"] : [configured];
            foreach (string path in candidates) if (NativeLibrary.TryLoad(path, out nint handle)) try { return new NativeBridge(handle); } catch { NativeLibrary.Free(handle); }
            return null;
        }
        public bool RuntimeAvailable { get { nint h = Create(); if (h == 0) return false; Destroy(h); return true; } }
        public nint Create() => create(); public void Destroy(nint h) => destroy(h); public int SourceCount(nint h) => sourceCount(h);
        public int SourceInfo(nint h, int i, byte[] n, int c) => sourceInfo(h, i, n, c);
        public nint ReceiverOpen(nint h, string n) => receiverOpen(h, n); public int ReceiverReceive(nint h, nint r, byte[] p, int c, out int w, out int he, out int x) => receiverReceive(h, r, p, c, out w, out he, out x);
        public void ReceiverClose(nint h, nint r) => receiverClose(h, r); public nint SenderOpen(nint h, string n) => senderOpen(h, n); public int SenderSend(nint h, nint s, byte[] p, int w, int he, int stride) => senderSend(h, s, p, w, he, stride); public void SenderClose(nint h, nint s) => senderClose(h, s);
        public void Dispose() { nint h = Interlocked.Exchange(ref library, 0); if (h != 0) NativeLibrary.Free(h); }
        private T Load<T>(string n) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, n));
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint CreateDelegate(); [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyDelegate(nint h); [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SourceCountDelegate(nint h); [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SourceInfoDelegate(nint h, int i, byte[] n, int c);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint ReceiverOpenDelegate(nint h, [MarshalAs(UnmanagedType.LPStr)] string n); [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReceiverReceiveDelegate(nint h, nint r, byte[] p, int c, out int w, out int he, out int x); [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReceiverCloseDelegate(nint h, nint r);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint SenderOpenDelegate(nint h, [MarshalAs(UnmanagedType.LPStr)] string n); [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SenderSendDelegate(nint h, nint s, byte[] p, int w, int he, int stride); [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SenderCloseDelegate(nint h, nint s);
    }
}
