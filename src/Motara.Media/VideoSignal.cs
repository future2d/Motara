namespace Motara.Media;

public enum SignalPixelFormat
{
    Bgra8888,
    Rgba8888
}

public enum VideoSignalProtocol
{
    Spout2,
    Ndi
}

public enum VideoSignalState
{
    Stopped,
    Starting,
    Ready,
    Reconnecting,
    Faulted
}

public sealed class VideoSignalStateChangedEventArgs(
    VideoSignalState state,
    Exception? failure = null) : EventArgs
{
    public VideoSignalState State { get; } = state;
    public Exception? Failure { get; } = failure;
}

public readonly record struct SignalFrameMetadata(
    int Width,
    int Height,
    SignalPixelFormat PixelFormat,
    bool HasAlpha,
    long Sequence,
    TimeSpan Timestamp)
{
    public const int BytesPerPixel = 4;
    public int RequiredBufferLength => checked(Width * Height * BytesPerPixel);
}

public sealed record VideoSignalSourceDescriptor(
    VideoSignalProtocol Protocol,
    string Id,
    string DisplayName,
    int Width,
    int Height,
    double FramesPerSecond,
    bool HasAlpha);

public sealed record VideoSignalOutputOptions(
    VideoSignalProtocol Protocol,
    string Name,
    int Width,
    int Height,
    double FramesPerSecond);

public interface ISignalFrameGpuSurface : IDisposable
{
    nint NativeHandle { get; }
}

public sealed class SignalFrame : IDisposable
{
    private byte[]? pixels;
    private ISignalFrameGpuSurface? gpuSurface;

    private SignalFrame(
        SignalFrameMetadata metadata,
        byte[]? pixels,
        ISignalFrameGpuSurface? gpuSurface)
    {
        Metadata = metadata;
        this.pixels = pixels;
        this.gpuSurface = gpuSurface;
    }

    public SignalFrameMetadata Metadata { get; }
    public bool IsDisposed => pixels is null && gpuSurface is null;
    public ReadOnlyMemory<byte> Pixels => pixels ?? ReadOnlyMemory<byte>.Empty;
    public ISignalFrameGpuSurface? GpuSurface => gpuSurface;

    public static SignalFrame CopyFrom(
        int width,
        int height,
        SignalPixelFormat pixelFormat,
        ReadOnlySpan<byte> pixels,
        long sequence,
        TimeSpan timestamp,
        bool hasAlpha = true)
    {
        SignalFrameMetadata metadata = CreateMetadata(width, height, pixelFormat, hasAlpha, sequence, timestamp);
        if (pixels.Length < metadata.RequiredBufferLength)
        {
            throw new ArgumentException("Signal frame pixels are shorter than the declared dimensions.", nameof(pixels));
        }

        return new SignalFrame(metadata, pixels[..metadata.RequiredBufferLength].ToArray(), null);
    }

    public static SignalFrame FromGpuSurface(
        int width,
        int height,
        SignalPixelFormat pixelFormat,
        ISignalFrameGpuSurface gpuSurface,
        long sequence,
        TimeSpan timestamp,
        bool hasAlpha = true)
    {
        ArgumentNullException.ThrowIfNull(gpuSurface);
        SignalFrameMetadata metadata = CreateMetadata(width, height, pixelFormat, hasAlpha, sequence, timestamp);
        return new SignalFrame(metadata, null, gpuSurface);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref pixels, null);
        Interlocked.Exchange(ref gpuSurface, null)?.Dispose();
    }

    private static SignalFrameMetadata CreateMetadata(
        int width,
        int height,
        SignalPixelFormat pixelFormat,
        bool hasAlpha,
        long sequence,
        TimeSpan timestamp)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        _ = checked(width * height * 4);
        return new SignalFrameMetadata(width, height, pixelFormat, hasAlpha, sequence, timestamp);
    }
}

public interface IVideoSignalReceiver : IAsyncDisposable
{
    VideoSignalState State { get; }
    event EventHandler<VideoSignalStateChangedEventArgs>? StateChanged;
    Task StartAsync(VideoSignalSourceDescriptor source, CancellationToken cancellationToken);
    SignalFrame? ReadLatest();
    ValueTask StopAsync(CancellationToken cancellationToken);
}

public interface IVideoSignalSender : IAsyncDisposable
{
    VideoSignalState State { get; }
    event EventHandler<VideoSignalStateChangedEventArgs>? StateChanged;
    Task StartAsync(VideoSignalOutputOptions options, CancellationToken cancellationToken);
    ValueTask PublishAsync(SignalFrame frame, CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}
