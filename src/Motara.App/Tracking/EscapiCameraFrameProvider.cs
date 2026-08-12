using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Motara.Tracking.Abstractions;

namespace Motara.App.Tracking;

internal readonly record struct EscapiCaptureParameters(
    nint Buffer,
    int Width,
    int Height,
    int Fps);

internal interface IEscapiCaptureApi : IDisposable
{
    int CountCaptureDevices();
    bool InitCapture(int deviceIndex, ref EscapiCaptureParameters parameters);
    void DoCapture(int deviceIndex);
    bool IsCaptureDone(int deviceIndex);
    void DeinitCapture(int deviceIndex);
    string? GetCaptureDeviceName(int deviceIndex);
}

internal interface IMediaPipeFrameProviderAvailability
{
    ValueTask<TrackingSourceAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken);
}

internal static class EscapiNativePaths
{
    internal const string NativeFileName = "escapi_x64.dll";

    internal static string ResolveLibraryPath(string baseDirectory, string? explicitPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        string candidate = !string.IsNullOrWhiteSpace(explicitPath)
            ? explicitPath
            : Path.Combine(baseDirectory, "tracking", "MediaPipe", NativeFileName);
        return Path.GetFullPath(candidate);
    }
}

internal sealed class EscapiCameraFrameProviderFactory : IMediaPipeFrameProviderFactory, IMediaPipeFrameProviderAvailability
{
    private readonly string libraryPath;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly int cameraIndex;
    private readonly int width;
    private readonly int height;
    private readonly int fps;
    private readonly Func<string, IEscapiCaptureApi?> apiFactory;

    internal EscapiCameraFrameProviderFactory(
        string libraryPath,
        TimeProvider timeProvider,
        ILogger? logger = null,
        int cameraIndex = 0,
        int width = 640,
        int height = 480,
        int fps = 30,
        Func<string, IEscapiCaptureApi?>? apiFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfNegative(cameraIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        this.libraryPath = Path.GetFullPath(libraryPath);
        this.timeProvider = timeProvider;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        this.cameraIndex = cameraIndex;
        this.width = width;
        this.height = height;
        this.fps = fps;
        this.apiFactory = apiFactory ?? EscapiCaptureApi.TryLoad;
    }

    public ValueTask<TrackingSourceAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IEscapiCaptureApi? api = apiFactory(libraryPath);
        if (api is null)
        {
            EscapiTrackingLog.NativeUnavailable(logger, libraryPath);
            return ValueTask.FromResult(
                TrackingSourceAvailability.Unavailable("tracking.camera.native_missing"));
        }

        try
        {
            int deviceCount = api.CountCaptureDevices();
            if (deviceCount <= cameraIndex)
            {
                EscapiTrackingLog.CameraUnavailable(logger, cameraIndex, deviceCount);
                return ValueTask.FromResult(
                    TrackingSourceAvailability.Unavailable("tracking.camera.not_found"));
            }

            return ValueTask.FromResult(TrackingSourceAvailability.Available);
        }
        finally
        {
            api.Dispose();
        }
    }

    public ValueTask<IMediaPipeFrameProvider> CreateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IMediaPipeFrameProvider>(new EscapiCameraFrameProvider(
            apiFactory(libraryPath)
                ?? throw new InvalidOperationException("ESCAPI native runtime could not be loaded."),
            timeProvider,
            logger,
            cameraIndex,
            width,
            height,
            fps));
    }
}

internal sealed class EscapiCameraFrameProvider : IMediaPipeFrameProvider
{
    private readonly IEscapiCaptureApi api;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly int cameraIndex;
    private readonly int width;
    private readonly int height;
    private readonly int fps;
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;
    private int reading;
    private int apiDisposed;

    internal EscapiCameraFrameProvider(
        IEscapiCaptureApi api,
        TimeProvider timeProvider,
        ILogger logger,
        int cameraIndex,
        int width,
        int height,
        int fps)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.cameraIndex = cameraIndex;
        this.width = width;
        this.height = height;
        this.fps = fps;
    }

    public async IAsyncEnumerable<MediaPipeInputFrame> ReadFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref reading, 1) != 0)
        {
            throw new InvalidOperationException("ESCAPI frame provider supports one active reader.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        int byteCount = checked(width * height * 4);
        byte[] bgra = new byte[byteCount];
        byte[] rgba = new byte[byteCount];
        GCHandle pinned = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        bool initialized = false;
        long started = timeProvider.GetTimestamp();
        try
        {
            var parameters = new EscapiCaptureParameters(
                pinned.AddrOfPinnedObject(),
                width,
                height,
                fps);
            if (!api.InitCapture(cameraIndex, ref parameters))
            {
                throw new InvalidOperationException("ESCAPI could not initialize the selected camera.");
            }

            initialized = true;
            EscapiTrackingLog.CaptureStarted(logger, cameraIndex, width, height, fps);
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                api.DoCapture(cameraIndex);
                while (!api.IsCaptureDone(cameraIndex))
                {
                    await Task.Delay(1, linked.Token).ConfigureAwait(false);
                }

                for (int index = 0; index < byteCount; index += 4)
                {
                    rgba[index] = bgra[index + 2];
                    rgba[index + 1] = bgra[index + 1];
                    rgba[index + 2] = bgra[index];
                    rgba[index + 3] = bgra[index + 3];
                }

                yield return new MediaPipeInputFrame(
                    rgba,
                    width,
                    height,
                    (long)timeProvider.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        finally
        {
            if (initialized)
            {
                try
                {
                    api.DeinitCapture(cameraIndex);
                }
                catch (Exception exception)
                {
                    EscapiTrackingLog.CaptureCleanupFailed(logger, exception.GetType().Name);
                }
            }

            pinned.Free();
            Interlocked.Exchange(ref reading, 0);
            DisposeApi();
            if (Volatile.Read(ref disposed) != 0)
            {
                lifetime.Dispose();
            }
            EscapiTrackingLog.CaptureStopped(logger, cameraIndex);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            lifetime.Cancel();
            if (Volatile.Read(ref reading) == 0)
            {
                DisposeApi();
                lifetime.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }

    private void DisposeApi()
    {
        if (Interlocked.Exchange(ref apiDisposed, 1) == 0)
        {
            api.Dispose();
        }
    }
}

internal sealed class EscapiCaptureApi : IEscapiCaptureApi
{
    private readonly nint handle;
    private readonly InitComDelegate initCom;
    private readonly CountDevicesDelegate countDevices;
    private readonly InitCaptureDelegate initCapture;
    private readonly DoCaptureDelegate doCapture;
    private readonly IsCaptureDoneDelegate isCaptureDone;
    private readonly DeinitCaptureDelegate deinitCapture;
    private readonly GetDeviceNameDelegate getDeviceName;
    private int disposed;

    private EscapiCaptureApi(
        nint handle,
        InitComDelegate initCom,
        CountDevicesDelegate countDevices,
        InitCaptureDelegate initCapture,
        DoCaptureDelegate doCapture,
        IsCaptureDoneDelegate isCaptureDone,
        DeinitCaptureDelegate deinitCapture,
        GetDeviceNameDelegate getDeviceName)
    {
        this.handle = handle;
        this.initCom = initCom;
        this.countDevices = countDevices;
        this.initCapture = initCapture;
        this.doCapture = doCapture;
        this.isCaptureDone = isCaptureDone;
        this.deinitCapture = deinitCapture;
        this.getDeviceName = getDeviceName;
    }

    internal static IEscapiCaptureApi? TryLoad(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return null;
        }

        nint handle = 0;
        try
        {
            handle = NativeLibrary.Load(path);
            var api = new EscapiCaptureApi(
                handle,
                Load<InitComDelegate>(handle, "initCOM"),
                Load<CountDevicesDelegate>(handle, "countCaptureDevices"),
                Load<InitCaptureDelegate>(handle, "initCapture"),
                Load<DoCaptureDelegate>(handle, "doCapture"),
                Load<IsCaptureDoneDelegate>(handle, "isCaptureDone"),
                Load<DeinitCaptureDelegate>(handle, "deinitCapture"),
                Load<GetDeviceNameDelegate>(handle, "getCaptureDeviceName"));
            api.InitializeCom();
            return api;
        }
        catch (Exception)
        {
            if (handle != 0)
            {
                NativeLibrary.Free(handle);
            }

            return null;
        }
    }

    private void InitializeCom() => initCom();

    public int CountCaptureDevices() => countDevices();

    public bool InitCapture(int deviceIndex, ref EscapiCaptureParameters parameters) =>
        initCapture(deviceIndex, ref parameters) != 0;

    public void DoCapture(int deviceIndex) => doCapture(deviceIndex);

    public bool IsCaptureDone(int deviceIndex) => isCaptureDone(deviceIndex) != 0;

    public void DeinitCapture(int deviceIndex) => deinitCapture(deviceIndex);

    public string? GetCaptureDeviceName(int deviceIndex)
    {
        var buffer = new StringBuilder(256);
        getDeviceName(deviceIndex, buffer, buffer.Capacity);
        return buffer.Length == 0 ? null : buffer.ToString();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            NativeLibrary.Free(handle);
        }
    }

    private static TDelegate Load<TDelegate>(nint library, string name)
        where TDelegate : Delegate =>
        Marshal.GetDelegateForFunctionPointer<TDelegate>(NativeLibrary.GetExport(library, name));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void InitComDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CountDevicesDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitCaptureDelegate(int deviceIndex, ref EscapiCaptureParameters parameters);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DoCaptureDelegate(int deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int IsCaptureDoneDelegate(int deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DeinitCaptureDelegate(int deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GetDeviceNameDelegate(
        int deviceIndex,
        [Out] StringBuilder name,
        int length);
}

internal static partial class EscapiTrackingLog
{
    [LoggerMessage(6810, LogLevel.Warning, "ESCAPI native runtime unavailable at {LibraryPath}")]
    internal static partial void NativeUnavailable(ILogger logger, string libraryPath);

    [LoggerMessage(6811, LogLevel.Warning, "ESCAPI camera unavailable; requested index={CameraIndex}, deviceCount={DeviceCount}")]
    internal static partial void CameraUnavailable(ILogger logger, int cameraIndex, int deviceCount);

    [LoggerMessage(6812, LogLevel.Information, "ESCAPI capture started; camera={CameraIndex}, width={Width}, height={Height}, fps={Fps}")]
    internal static partial void CaptureStarted(ILogger logger, int cameraIndex, int width, int height, int fps);

    [LoggerMessage(6813, LogLevel.Information, "ESCAPI capture stopped; camera={CameraIndex}")]
    internal static partial void CaptureStopped(ILogger logger, int cameraIndex);

    [LoggerMessage(6814, LogLevel.Warning, "ESCAPI capture cleanup failed; errorType={ErrorType}")]
    internal static partial void CaptureCleanupFailed(ILogger logger, string errorType);
}
